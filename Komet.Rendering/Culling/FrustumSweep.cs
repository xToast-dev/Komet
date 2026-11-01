using HarmonyLib;
using Komet.Core;
using System.Diagnostics;
using System.Numerics;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// MeshDataPool.FrustumCull, bit-exact and in order, a vector at a time. A pool quiet for QuietCulls is sorted into an
// x/z grid whose cell boxes test first (AddModel is first fit, so only sorting lets a box reject: 2/3 camera, 6/7 far
// shadow; sorting on every change cost 8 %). Sphere floats widened in the kernel: 5-10 % faster than doubles.
internal static partial class FrustumSweep
{
    private const int MaxLocations = 65536, MaxPools = 4096, PlaneCount = 6, MaxLanes = 32;
    // Pool ids only grow (PoolTrim deletes pools, the engine numbers new ones on), so mirrors index far past MaxPools
    private const int MaxPoolIds = 1 << 20, FirstMirrors = 64;
    private const int MaxPlanes = PlaneCount + ShadowView.MaxCasters; // a shadow pass's own and the view's casters'
    private const int WordBits = 64, MaxWords = MaxLocations / WordBits;
    private const int CellShift = 5, MaxCells = 16384, MinBucketed = 256, PerCell = 8, MaxShift = 30;
    private const int QuietCulls = 8;
    private const double Slack = 1.0; // a cell box grows by a block, so rounding never shrinks it inside a member
    // Diff: search distance for a survivor, change absorbed before a relayout
    private const int Window = 8, MaxSearch = 32, ScanBudget = 8;
    private const int Churn = 64, RelayoutsPerFrame = 4, MaxRelayoutTries = 64;
    // Workers: ~0.3 ms of culling is the least worth waking one for
    private const int RowsPerHelper = 4096;

    // every cell can pad up to a vector; Diff appends behind the grid until a quarter of the rows are new
    private const int MaxSlots =
        MaxLocations + (MaxLocations / PerCell + 1) * MaxLanes + MaxLocations / 4 + 2 * MaxLanes;

    private const int Empty = -1, Gone = -1, Foreign = -1, Unseen = -1, Lost = -2;
    // radius / Sqrt3 in float is AABBisOutside's half edge; occlusion boxes divide alike, to the bit
    internal const float Sqrt3 = 1.7320508f;

    private static int _frameRebuilds, _frameRelayouts;
    private static long _rebuilds, _diffed, _relayouts, _settles; // _relayouts: FrustumSweepTests counts them
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;

    // Shadow passes skip casters whose shadow cannot reach the view (ShadowView)
    private static bool Casting => ShadowView.Casting;
    public static long Tested { get; private set; } // totals while Counting.Hud, main thread
    public static long Visible { get; private set; }
    public static long Skipped { get; private set; }
    // Interlocked: workers update them too
    public static long Rebuilds => Interlocked.Read(ref _rebuilds);
    public static long Diffed => Interlocked.Read(ref _diffed);
    public static long Settles => Interlocked.Read(ref _settles);
    public static long Ticks { get; private set; }
    public static int MaxRebuilds { get; private set; } // the most in one frame since ResetPeaks

    public static void ResetPeaks()
    {
        MaxRebuilds = 0;
    }

    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        Clear(); // pool ids restart; mirrors hold the previous world's locations
        _logger = logger;
        var cull = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull));
        var frame = AccessTools.Method(typeof(MeshDataPoolMasterManager), nameof(MeshDataPoolMasterManager.OnFrame));
        var prefix = new HarmonyMethod(FrustumCull);
        if (!NotNull(harmony) || !NotNull(cull) || !Seams() ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "frustum") != null) ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "playerPos") != null) ||
            !Assert(Vector<double>.Count is 2 or 4 or 8) || !Assert(Il.Binds(cull, prefix.method))) return;
        _ = harmony.Patch(cull, prefix);
        // without it the relayout budget never refills
        if (NotNull(frame)) _ = harmony.Patch(frame, postfix: new HarmonyMethod(NextFrame));
        var render = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var (pre, post) = (new HarmonyMethod(Precull), new HarmonyMethod(Culled));
        _parallel = NotNull(render) && Assert(AccessTools.Field(typeof(MeshDataPoolManager), "pools")?.FieldType ==
                                              typeof(List<MeshDataPool>)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPoolManager), "frustumCuller")?.FieldType ==
                           typeof(FrustumCulling)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPool), "dimensionId")?.FieldType == typeof(int)) &&
                    Assert(Il.Binds(render, pre.method)) && harmony.Patch(render, pre, post) is not null;
        // a stage batch opens once a frame; every change to a pool's list ends it first
        var loop = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainRenderLoop));
        var (add, remove) = (AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        _staging = _parallel && NotNull(loop) && NotNull(add) && NotNull(remove) &&
                   harmony.Patch(loop, new HarmonyMethod(EndFrame), new HarmonyMethod(EndFrame)) is not null &&
                   harmony.Patch(add, new HarmonyMethod(Unbatch) { priority = Priority.First }) is not null &&
                   harmony.Patch(remove, new HarmonyMethod(Unbatch) { priority = Priority.First }) is not null;
    }

    // The pool fields the mirrors read; modelRef's users check it themselves
    private static bool Seams()
    {
        var pool = typeof(MeshDataPool);
        return Assert(AccessTools.Field(pool, "poolLocations") != null) &&
               Assert(AccessTools.Field(pool, "poolId") != null) &&
               Assert(AccessTools.Field(pool, "poolOrigin") != null) &&
               Assert(AccessTools.Field(typeof(List<ModelDataPoolLocation>), "_version") != null);
    }

    // TerrainRebuild: new chunk renderer (pool ids restart at 0), plans and mirrors go; staging and settings stay
    internal static void Renewed()
    {
        EndFrame();
        Array.Clear(Plans);
        Array.Clear(PlanLengths);
        // ids restart at 0, so a table grown by a long session's ids goes back to its first size
        if (_mirrors.Length > FirstMirrors) _mirrors = new Mirror?[FirstMirrors];
        else Array.Clear(_mirrors);
        Unnumbered.Clear();
    }

    // Install and the feature's Stop: no batch, plan or mirror outlives its world (a plan holds the old ClientMain's
    // managers), and the job arrays go with it
    internal static void Clear()
    {
        Renewed();
        Forget();
        (_frameRebuilds, _frameRelayouts, _jobCount, _jobCuller) = (0, 0, 0, null);
        (_jobPools, _jobMirrors, _jobManager, _parallel, _staging) = ([], [], [], true, false);
    }

    // Once a frame (ChunkRenderer.OnBeforeRenderOpaque)
    internal static void NextFrame()
    {
        var rebuilt = Interlocked.Exchange(ref _frameRebuilds, 0);
        if (Counting.Hud) MaxRebuilds = Math.Max(MaxRebuilds, rebuilt);
        _ = Interlocked.Exchange(ref _frameRelayouts, 0);
    }

    private static bool FrustumCull(MeshDataPool __instance, FrustumCulling frustumCuller,
        EnumFrustumCullMode frustumCullMode)
    {
        // OcclusionCulling's prefix ran first and left the pool to the GPU
        if (OcclusionCulling.Bypassing && Dimension(__instance) == 0) return false;
        if (!Enabled || frustumCullMode == EnumFrustumCullMode.NoCull) return true;
        var locations = Locations(__instance);
        if (!NotNull(locations) || !NotNull(frustumCuller) || locations.Count > MaxLocations) return true;
        var slot = SlotOf(__instance);
        if (slot != Foreign && slot < _mirrors.Length && _mirrors[slot] is { } done && done.Pass == _pass &&
            ReferenceEquals(done.Owner, locations))
        {
            if (Counting.Hud) (Tested, Visible) = (Tested + locations.Count, Visible + done.Groups);
            return false; // Precull wrote the pool's ranges and counts
        }

        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        var groups = Cull(frustumCuller, frustumCullMode, locations, slot, __instance.indicesStartsByte,
            __instance.indicesSizes, out var rendered, out var allocated);
        if (groups < 0) return true;
        (__instance.indicesGroupsCount, __instance.RenderedTriangles, __instance.AllocatedTris) =
            (groups, rendered, allocated);
        if (start != 0)
            (Tested, Visible, Ticks) = (Tested + locations.Count, Visible + groups,
                Ticks + Stopwatch.GetTimestamp() - start);
        return false;
    }

    // AddModel gives ids with an origin; SystemRenderDecals' pool shares id 0, so a pool without origin is Foreign (-1)
    internal static int SlotOf(MeshDataPool pool)
    {
        if (!NotNull(pool) || PoolOrigin(pool) is null) return Foreign;
        var id = PoolId(pool);
        return Index(id, MaxPoolIds) ? id : Foreign;
    }

    // Visible ranges written, -1 when the engine has to cull
    internal static int Cull(FrustumCulling culler, EnumFrustumCullMode mode, List<ModelDataPoolLocation> locations,
        int slot, int[] starts, int[] sizes, out int rendered, out int allocated)
    {
        (rendered, allocated) = (0, 0);
        if (!Usable(culler, mode)) return -1;
        var m = MirrorOf(slot, locations);
        if (m is null) return -1;
        allocated = m.AllocatedTris;
        var groups = Swept(m, culler, mode, starts, sizes, out rendered);
        if (Counting.Hud) (Skipped, m.SkippedRows) = (Skipped + m.SkippedRows, 0);
        return groups;
    }

    // NoCull and out-of-enum values take IsVisible's default (!Hide, no frustum)
    private static bool Usable(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var planes = Planes(culler);
        var known = mode is EnumFrustumCullMode.CullNormal or EnumFrustumCullMode.CullInstant
            or EnumFrustumCullMode.CullInstantShadowPassNear or EnumFrustumCullMode.CullInstantShadowPassFar;
        return NotNull(planes) && Assert(planes.Length == PlaneCount) && known &&
               (mode == EnumFrustumCullMode.CullInstant || NotNull(PlayerPos(culler)));
    }

    // Touches only mirror and pool arrays: a call's pools can run in parallel
    private static int Swept(Mirror m, FrustumCulling culler, EnumFrustumCullMode mode, int[] starts, int[] sizes,
        out int rendered)
    {
        rendered = 0;
        if (!Assert(starts.Length >= 2 * m.Length) || !Assert(sizes.Length >= m.Length)) return -1;
        if (m.Length == 0) return 0;
        var shadow = mode is EnumFrustumCullMode.CullInstantShadowPassNear
            or EnumFrustumCullMode.CullInstantShadowPassFar;
        var planes = Planes(culler);
        var casting = shadow && Casting;
        Span<Plane> all = casting ? stackalloc Plane[MaxPlanes] : default;
        if (casting) planes.AsSpan(0, PlaneCount).CopyTo(all);
        var count = mode == EnumFrustumCullMode.CullNormal ? 5 : PlaneCount; // InFrustumAndRange leaves the far plane to the range test
        var swept = casting ? all[..(PlaneCount + ShadowView.Casters(planes[0], all[PlaneCount..]))] : planes.AsSpan(0, count);
        Sweep(m, swept, shadow, culler);
        return Emit(m, culler, mode, starts, sizes, out rendered);
    }
}

// Below ParallelRows the hand-off costs more than it saves. _pass stamps the Render call a mirror's result belongs to.
internal static partial class FrustumSweep
{
    private static readonly Action<int> Job = CullJob;

    // Per job: pool, mirror and manager within its batch. Tally drops pools and mirrors: left in, a trimmed pool's
    // mirror or a replaced renderer's (its rows hold the old locations) outlived Deleted and Renewed
    private static MeshDataPool?[] _jobPools = [];
    private static Mirror?[] _jobMirrors = [];
    private static int[] _jobManager = [];
    private static int _jobCount, _pass;
    private static FrustumCulling? _jobCuller;
    private static EnumFrustumCullMode _jobMode;
    private static bool _parallel = true; // the Render patch is in

    internal static int ParallelRows { get; set; } = 8192;
    internal static long ParallelCalls { get; private set; } // calls the workers culled

    // Mirrors update on the main thread (engine lists, shared relayout budget). Mini-dimension pools stay the engine's.
    // Whatever cannot use a batch ends it first (Stage).
    internal static void Precull(MeshDataPoolManager __instance, EnumFrustumCullMode frustumCullMode)
    {
        _pass++;
        if (!Enabled || !_parallel || frustumCullMode == EnumFrustumCullMode.NoCull || WorkerPool.Running == 0 ||
            WorkerPool.FrameOff || OcclusionCulling.Bypassing)
        {
            EndStage();
            return;
        }

        var (pools, culler) = (Pools(__instance), Culler(__instance));
        if (NotNull(pools) && pools.Count == 0) return; // a pass without pools: nothing to cull, nothing a batch holds
        // before the player exists (world load) the engine's loop culls
        if (!NotNull(pools) || !NotNull(culler) ||
            (frustumCullMode != EnumFrustumCullMode.CullInstant && PlayerPos(culler) is null) ||
            !Usable(culler, frustumCullMode))
        {
            EndStage();
            return;
        }

        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        if (!Staged(__instance, pools, culler, frustumCullMode)) Alone(pools, culler, frustumCullMode);
        if (start != 0) Ticks += Stopwatch.GetTimestamp() - start;
    }

    private static void Alone(List<MeshDataPool> pools, FrustumCulling culler, EnumFrustumCullMode frustumCullMode)
    {
        _jobCount = 0;
        var rows = Collect(pools, 0);
        if (rows < ParallelRows || _jobCount < 2)
        {
            Tally();
            return;
        }

        (_jobCuller, _jobMode) = (culler, frustumCullMode);
        var result = WorkerPool.RunFrame(Job, _jobCount, rows / RowsPerHelper - 1);
        if (result == WorkerPool.FrameResult.Done) ParallelCalls++;
        else if (result == WorkerPool.FrameResult.Failed)
        {
            _pass++; // no half-finished result is used; the engine culls every pool of this call
            _logger?.Error(
                "Komet: frustum culling on the worker threads failed, this frame culls on the main thread{0}: {1}",
                WorkerPool.FrameOff ? " and so does every later one" : "", WorkerPool.LastError);
        }

        Tally();
    }

    // Deferred: the job updates each mirror itself (Update), rows are the list counts.
    private static int Collect(List<MeshDataPool> pools, int manager, bool deferred = false)
    {
        var (rows, n) = (0, Math.Min(pools.Count, MaxPools));
        if (!Index(manager, MaxStage) || !Assert(_jobCount + n <= MaxJobs)) return 0;
        if (_jobPools.Length < _jobCount + n)
        {
            var size = Capacity(_jobCount + n, _jobPools.Length, int.MaxValue);
            Array.Resize(ref _jobPools, size);
            Array.Resize(ref _jobMirrors, size);
            Array.Resize(ref _jobManager, size);
        }

        for (var i = 0; i < Math.Min(n, MaxPools); i++)
        {
            var pool = pools[i];
            if (pool is null || Dimension(pool) == 1) continue;
            var (slot, locations) = (SlotOf(pool), Locations(pool));
            if (slot == Foreign || locations is null || locations.Count > MaxLocations) continue;
            var m = deferred ? Owned(slot, locations) : MirrorOf(slot, locations);
            // a new mirror takes its list here, so a second list under one slot gets its own mirror
            if (deferred && m is { Owner: null }) m = Update(m, locations);
            if (m is null || (!deferred && !ReferenceEquals(m.Owner, locations))) continue;
            (_jobPools[_jobCount], _jobMirrors[_jobCount], _jobManager[_jobCount]) = (pool, m, manager);
            _jobCount++;
            rows += deferred ? locations.Count : m.Length;
        }

        return rows;
    }

    private static void CullJob(int j)
    {
        if (!Index(j, _jobCount)) return;
        var (pool, m, culler) = (_jobPools[j], _jobMirrors[j], _jobCuller);
        if (pool is null || m is null || culler is null) return;
        var groups = Swept(m, culler, _jobMode, pool.indicesStartsByte, pool.indicesSizes, out var rendered);
        if (groups < 0) return;
        (pool.indicesGroupsCount, pool.RenderedTriangles, pool.AllocatedTris) = (groups, rendered, m.AllocatedTris);
        (m.Groups, m.Pass) = (groups, _pass);
    }

    // Skipped rows count for the HUD where drawn: here after Alone's batch, in Stamp for stage batches
    private static void Tally(bool drawn = true)
    {
        for (var j = 0; j < Math.Min(_jobCount, MaxJobs); j++)
        {
            if (_jobMirrors[j] is { } m)
            {
                if (Counting.Hud && drawn) Skipped += m.SkippedRows;
                m.SkippedRows = 0;
            }

            (_jobPools[j], _jobMirrors[j]) = (null, null);
        }

        (_jobCount, _jobCuller) = (0, null);
    }

    // Results stamped in this call are never read by a later one
    internal static void Culled() => _pass++;
}

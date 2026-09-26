using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// MeshDataPool.FrustumCull with the engine's ranges, bit for bit and in its order: a per-pool Mirror of flat arrays, the planes a
// vector wide in Plane.AABBisOutside's operation order, objects read only by survivors. A pool quiet for QuietCulls culls is sorted
// into an x/z grid whose cell boxes go first: AddModel fills pools first fit, so only sorting lets a box reject (2/3 camera, 6/7 far
// shadow), and sorting on every change cost 8 %. A changed list (List._version) goes through Diff. Geometry is stored as the sphere's
// floats and widened in the kernel: 5-10 % faster than doubles on the engine-driven walk (same process).
internal static class FrustumSweep
{
    private const int MaxLocations = 65536, MaxPools = 4096, PlaneCount = 6, MaxLanes = 32;
    private const int CellShift = 5, MaxCells = 16384, MinBucketed = 256, PerCell = 8, MaxShift = 30;
    private const int QuietCulls = 8;
    private const int WordBits = 64, MaxWords = MaxLocations / WordBits;
    private const int Window = 8, MaxSearch = 32, ScanBudget = 8; // Diff's search
    private const int Churn = 64, RelayoutsPerFrame = 4;

    // every cell can pad up to a vector, and Diff appends behind the grid until a quarter of the rows are new
    private const int MaxSlots =
        MaxLocations + (MaxLocations / PerCell + 1) * MaxLanes + MaxLocations / 4 + 2 * MaxLanes;

    private const double Slack = 1.0; // a cell box grows by a block, so rounding never shrinks it inside a member
    private const int Empty = -1, Gone = -1, Foreign = -1, Unseen = -1, Lost = -2;
    private const float Sqrt3 = 1.7320508f; // Plane.SQRT3
    private const int RowsPerHelper = 4096; // about 0.3 ms of culling: less is not worth waking a worker for
    private static Mirror?[] _mirrors = new Mirror[64];
    private static readonly ConditionalWeakTable<List<ModelDataPoolLocation>, Mirror> Unnumbered = [];
    private static int _frameRebuilds, _frameRelayouts;
    private static readonly Action<int> Job = CullJob;
    private static MeshDataPool?[] _jobPools = [];
    private static Mirror[] _jobMirrors = [];
    private static int _jobCount, _pass;
    private static FrustumCulling? _jobCuller;
    private static EnumFrustumCullMode _jobMode;
    private static bool _parallel = true; // the Render patch is in
    private static ILogger? _logger;

    // One Render call culls its pools on Komet's worker pool first (a frame job); the engine's loop then only draws. Below ParallelRows
    // the hand-off costs more than it saves. _pass stamps which call a mirror's result belongs to.
    internal static int ParallelRows { get; set; } = 8192;
    internal static long ParallelCalls { get; private set; } // Render calls whose pools the workers culled

    public static bool Enabled { get; set; } = true;
    public static long Tested { get; private set; } // totals while Counting.Hud, main thread
    public static long Visible { get; private set; }
    public static long Skipped { get; private set; }
    public static long Rebuilds { get; private set; }
    public static long Diffed { get; private set; }
    internal static long Relayouts { get; private set; }
    public static long Settles { get; private set; }
    public static long Ticks { get; private set; }
    public static int MaxRebuilds { get; private set; } // the most in one frame since ResetPeaks

    public static void ResetPeaks()
    {
        _ = Assert(MaxRebuilds >= 0) && Assert(Rebuilds >= Diffed);
        MaxRebuilds = 0;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustum")]
    private static extern ref Plane[] Planes(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    private static extern ref BlockPos PlayerPos(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    private static extern ref List<MeshDataPool> Pools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustumCuller")]
    private static extern ref FrustumCulling Culler(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimensionId")]
    private static extern ref int Dimension(MeshDataPool pool);

    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        Clear(); // pool ids restart, and a mirror holds the previous world's locations
        _logger = logger;
        var cull = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull));
        var frame = AccessTools.Method(typeof(MeshDataPoolMasterManager), nameof(MeshDataPoolMasterManager.OnFrame));
        var prefix = new HarmonyMethod(FrustumCull);
        if (!NotNull(harmony) || !NotNull(cull) || !MeshPool.Seams() ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "frustum") != null) ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "playerPos") != null) ||
            !Assert(Vector<double>.Count is >= 2 and <= MaxLanes) || !Assert(Il.Binds(cull, prefix.method))) return;
        _ = NotNull(harmony.Patch(cull, prefix));
        // without it the relayout budget never refills, and mirrors out of room behind their grid are rebuilt in full
        if (NotNull(frame)) _ = NotNull(harmony.Patch(frame, postfix: new HarmonyMethod(NextFrame)));
        InstallParallel(harmony);
    }

    private static void InstallParallel(Harmony harmony)
    {
        var render = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var (pre, post) = (new HarmonyMethod(Precull), new HarmonyMethod(Culled));
        _parallel = NotNull(render) && Assert(AccessTools.Field(typeof(MeshDataPoolManager), "pools")?.FieldType ==
                                              typeof(List<MeshDataPool>)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPoolManager), "frustumCuller")?.FieldType ==
                           typeof(FrustumCulling)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPool), "dimensionId")?.FieldType == typeof(int)) &&
                    Assert(Il.Binds(render, pre.method)) && NotNull(harmony.Patch(render, pre, post));
    }

    internal static void Clear()
    {
        Array.Clear(_mirrors);
        Unnumbered.Clear();
        (_frameRebuilds, _frameRelayouts, _jobCount, _jobCuller) = (0, 0, 0, null);
        (_jobPools, _jobMirrors, _parallel) = ([], [], true);
        _ = Assert(_mirrors.Length <= MaxPools);
    }

    // Postfix on MeshDataPoolMasterManager.OnFrame, once a frame (ChunkRenderer.OnBeforeRenderOpaque)
    internal static void NextFrame()
    {
        if (Counting.Hud && Assert(_frameRebuilds >= 0)) MaxRebuilds = Math.Max(MaxRebuilds, _frameRebuilds);
        _ = Assert(_frameRelayouts <= RelayoutsPerFrame);
        (_frameRebuilds, _frameRelayouts) = (0, 0);
    }

    private static bool FrustumCull(MeshDataPool __instance, FrustumCulling frustumCuller,
        EnumFrustumCullMode frustumCullMode)
    {
        if (!Enabled || frustumCullMode == EnumFrustumCullMode.NoCull) return true;
        var locations = MeshPool.Locations(__instance);
        if (!NotNull(locations) || !NotNull(frustumCuller) || locations.Count > MaxLocations) return true;
        var slot = SlotOf(__instance);
        if (slot != Foreign && slot < _mirrors.Length && _mirrors[slot] is { } done && done.Pass == _pass &&
            ReferenceEquals(done.Owner, locations))
        {
            if (Counting.Hud) (Tested, Visible) = (Tested + locations.Count, Visible + done.Groups);
            return false; // Precull wrote the pool's ranges and counts in this Render call
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

    // MeshDataPoolManager.AddModel hands out an id together with an origin. SystemRenderDecals allocates its pool alone and leaves it at
    // id 0 beside the first chunk pool, so a pool without origin is keyed by its list instead (-1).
    internal static int SlotOf(MeshDataPool pool)
    {
        if (!NotNull(pool) || MeshPool.Origin(pool) is null) return Foreign;
        var id = MeshPool.PoolId(pool);
        return Index(id, MaxPools) ? id : Foreign;
    }

    // The count of visible ranges written, -1 when the engine has to cull
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

    // NoCull and any value outside the enum take IsVisible's default branch, !Hide without a frustum test
    private static bool Usable(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var planes = Planes(culler);
        var known = mode is EnumFrustumCullMode.CullNormal or EnumFrustumCullMode.CullInstant
            or EnumFrustumCullMode.CullInstantShadowPassNear or
            EnumFrustumCullMode.CullInstantShadowPassFar;
        return NotNull(planes) && Assert(planes.Length == PlaneCount) && known &&
               (mode == EnumFrustumCullMode.CullInstant || NotNull(PlayerPos(culler)));
    }

    // Touches only the mirror and the pool's own arrays, so the pools of one Render call can run on several threads
    private static int Swept(Mirror m, FrustumCulling culler, EnumFrustumCullMode mode, int[] starts, int[] sizes,
        out int rendered)
    {
        rendered = 0;
        if (!Assert(starts.Length >= 2 * m.Length) || !Assert(sizes.Length >= m.Length)) return -1;
        if (m.Length == 0) return 0;
        var shadow = mode is EnumFrustumCullMode.CullInstantShadowPassNear
            or EnumFrustumCullMode.CullInstantShadowPassFar;
        // InFrustumAndRange leaves the far plane to the range test
        Sweep(m, Planes(culler).AsSpan(0, mode == EnumFrustumCullMode.CullNormal ? 5 : PlaneCount), shadow, culler);
        return Emit(m, culler, mode, starts, sizes, out rendered);
    }

    // Prefix on MeshDataPoolManager.Render: the mirrors are brought up to date here on the main thread (they read the engine's lists and
    // share the relayout budget), then every pool's sweep and emit runs on the workers. Mini-dimension pools stay the engine's.
    internal static void Precull(MeshDataPoolManager __instance, EnumFrustumCullMode frustumCullMode)
    {
        _pass++;
        if (!Enabled || !_parallel || frustumCullMode == EnumFrustumCullMode.NoCull || WorkerPool.Running == 0 ||
            WorkerPool.FrameOff) return;
        var (pools, culler) = (Pools(__instance), Culler(__instance));
        // before the player exists (world load) the engine's own loop culls, as it does for any pool FrustumCull declines
        if (!NotNull(pools) || !NotNull(culler) || pools.Count == 0 ||
            (frustumCullMode != EnumFrustumCullMode.CullInstant && PlayerPos(culler) is null) ||
            !Usable(culler, frustumCullMode)) return;
        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        var rows = Collect(pools);
        if (rows < ParallelRows || _jobCount < 2) return; // the engine's loop culls pool by pool as before
        (_jobCuller, _jobMode) = (culler, frustumCullMode);
        var result =
            WorkerPool.RunFrame(Job, _jobCount, rows / RowsPerHelper - 1); // the main thread takes a share itself
        if (result == WorkerPool.FrameResult.Done)
        {
            ParallelCalls++;
        }
        else if (result == WorkerPool.FrameResult.Failed && Assert(WorkerPool.LastError is not null))
        {
            _pass++; // no half-finished result is used; the engine's loop culls every pool of this call
            _logger?.Error(
                "Komet: frustum culling on the worker threads failed, this frame culls on the main thread{0}: {1}",
                WorkerPool.FrameOff ? " and so does every later one" : "", WorkerPool.LastError);
        }

        Tally(start);
    }

    private static int Collect(List<MeshDataPool> pools)
    {
        var (rows, n) = (0, Math.Min(pools.Count, MaxPools));
        if (_jobPools.Length < n) (_jobPools, _jobMirrors) = (new MeshDataPool?[n], new Mirror[n]);
        _jobCount = 0;
        for (var i = 0; i < Math.Min(n, MaxPools); i++)
        {
            var pool = pools[i];
            if (pool is null || Dimension(pool) == 1) continue;
            var (slot, locations) = (SlotOf(pool), MeshPool.Locations(pool));
            if (slot == Foreign || locations is null || locations.Count > MaxLocations) continue;
            var m = MirrorOf(slot, locations);
            if (m is null || !ReferenceEquals(m.Owner, locations)) continue;
            (_jobPools[_jobCount], _jobMirrors[_jobCount]) = (pool, m);
            _jobCount++;
            rows += m.Length;
        }

        return rows;
    }

    private static void CullJob(int j)
    {
        if (!Index(j, _jobCount)) return;
        var (pool, m, culler) = (_jobPools[j], _jobMirrors[j], _jobCuller);
        if (pool is null || culler is null) return;
        var groups = Swept(m, culler, _jobMode, pool.indicesStartsByte, pool.indicesSizes, out var rendered);
        if (groups < 0) return;
        (pool.indicesGroupsCount, pool.RenderedTriangles, pool.AllocatedTris) = (groups, rendered, m.AllocatedTris);
        (m.Groups, m.Pass) = (groups, _pass);
    }

    private static void Tally(long start)
    {
        for (var j = 0; j < Math.Min(_jobCount, MaxPools); j++)
        {
            (Skipped, _jobMirrors[j].SkippedRows) = (Skipped + _jobMirrors[j].SkippedRows, 0);
            _jobPools[j] = null; // no pool outlives its world through the job table
        }

        if (start != 0) Ticks += Stopwatch.GetTimestamp() - start;
        _jobCuller = null;
    }

    // Postfix: a result stamped in this call is never read by a later FrustumCull
    internal static void Culled()
    {
        _pass++;
    }

    private static Mirror? MirrorOf(int slot, List<ModelDataPoolLocation> locations)
    {
        var m = Owned(slot, locations);
        if (m is null || !Assert(locations.Count <= MaxLocations)) return null;
        var current = CollectionsMarshal.AsSpan(locations);
        var same = ReferenceEquals(m.Owner, locations);
        if (same && MeshPool.Version(locations) == m.ListVersion)
        {
            Tidy(m, true);
            return m;
        }

        var diffed = same && m.Length > 0 && Diff(m, current);
        if ((!diffed && !Rebuild(m, locations, current)) || !Assert(m.Length == current.Length)) return null;
        (m.ListVersion, m.Quiet) = (MeshPool.Version(locations), 0);
        if (Counting.Hud)
            (Rebuilds, Diffed, _frameRebuilds) = (Rebuilds + 1, Diffed + (diffed ? 1 : 0), _frameRebuilds + 1);
        if (diffed) Tidy(m, false);
        return m;
    }

    // The first list to claim a slot keeps it; another list under the same id gets its own mirror, keyed weakly by the list
    private static Mirror? Owned(int slot, List<ModelDataPoolLocation> locations)
    {
        if (!NotNull(locations)) return null;
        if (slot == Foreign) return Unnumbered.GetOrCreateValue(locations);
        if (!Index(slot, MaxPools)) return null;
        if (slot >= _mirrors.Length)
            Array.Resize(ref _mirrors, Math.Min(MaxPools, Math.Max(slot + 1, 2 * _mirrors.Length)));
        var m = _mirrors[slot] ??= new Mirror();
        return m.Owner is null || ReferenceEquals(m.Owner, locations) ? m : Unnumbered.GetOrCreateValue(locations);
    }

    private static bool Rebuild(Mirror m, List<ModelDataPoolLocation> owner,
        ReadOnlySpan<ModelDataPoolLocation> locations)
    {
        var (n, w) = (locations.Length, Vector<double>.Count);
        (m.Owner, m.Length) = (null, 0); // nobody's until every row is in
        if (!NotNull(owner) || !Assert(n <= MaxLocations) || !Grow(m, n, w)) return false;
        Array.Clear(m.Refs, n, m.Refs.Length - n); // no reference keeps a removed location alive
        var (allocated, ordered) = (0, true);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var loc = locations[i];
            // IsVisible reads CullVisible only while !Hide, Emit reads both: a null one goes to the engine, which throws as it would
            if (!NotNull(loc) || !NotNull(loc.CullVisible))
            {
                Array.Clear(m.Refs, 0, i);
                return false;
            }

            // LodLevel and CullVisible are written once, right after InsertAt (TesselatedChunkPart.AddModelAndStoreLocation)
            m.Refs[i] = new LocRef(loc, loc.CullVisible);
            (m.Start[i], m.Count[i], m.Lod[i]) =
                (loc.IndicesStart * 4, loc.IndicesEnd - loc.IndicesStart, loc.LodLevel);
            allocated += m.Count[i] / 3;
            ordered &= i == 0 || m.Start[i - 1] <= m.Start[i];
            Place(m, loc.FrustumCullSphere, i);
            (m.Slot[i], m.Inv[i]) = (i, i);
        }

        (m.Owner, m.Length, m.AllocatedTris, m.Ordered) = (owner, n, allocated, ordered);
        (m.Cells, m.Dead, m.Fresh, m.Tried) = (0, 0, 0, false);
        Tail(m, n, w);
        return true;
    }

    // Between culls the list only gains and loses entries (InsertAt, RemoveLocation), and nothing rewrites what a row holds after
    // pooling, so survivors are matched by reference and keep their geometry slot (a grid stays valid); only new rows are read.
    private static bool Diff(Mirror m, ReadOnlySpan<ModelDataPoolLocation> list)
    {
        var (n, w) = (list.Length, Vector<double>.Count);
        // a Settle over rows beyond what Grow sized the geometry for would find no room for its grid's padding
        if (!Assert(m.Length <= m.Refs.Length) || !Assert(m.NRefs.Length == m.Refs.Length) || n == 0 ||
            n > m.Refs.Length || Geometry(n, w) > m.Cx.Length) return false;
        var (j, fresh, budget, old) = (0, 0, ScanBudget * (long)n + MaxLocations / 16, m.Length);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var p = j < old && ReferenceEquals(m.Refs[j].Loc, list[i]) ? j : Find(m, list[i], j, ref budget);
            if (p == Lost) return false;
            m.Match[i] = p;
            if (p == Unseen) fresh++;
            else j = p + 1;
        }

        var slots = (m.Used + fresh + w - 1) / w * w;
        return 4 * fresh <= n + Churn && slots <= m.Cx.Length && Merge(m, list, fresh, slots);
    }

    // The old row of the location from j on, Unseen when it is new, Lost when looking would cost more than a rebuild
    private static int Find(Mirror m, ModelDataPoolLocation? target, int j, ref long budget)
    {
        var old = m.Length;
        if (!NotNull(target) || !Assert(old <= m.Refs.Length))
            return Lost; // the engine throws on it, and Rebuild lets it
        var near = Math.Min(old, j + Window);
        for (var p = j; p < Math.Min(near, MaxLocations); p++)
            if (ReferenceEquals(m.Refs[p].Loc, target))
                return p;
        if (near >= old) return Unseen;
        if (m.Ordered) return Search(m, target, near);
        budget -= old - near;
        if (budget < 0) return Lost;
        for (var p = near; p < Math.Min(old, MaxLocations); p++)
            if (ReferenceEquals(m.Refs[p].Loc, target))
                return p;
        return Unseen;
    }

    // MeshDataPool keeps poolLocations ordered by IndicesStart (TryAppend adds past the last, TrySqueezeInbetween in front of the first
    // behind the gap). A start repeats only for an empty mesh, and that run is compared by reference.
    private static int Search(Mirror m, ModelDataPoolLocation target, int from)
    {
        var (lo, hi, start) = (from, m.Length, target.IndicesStart * 4);
        if (!Assert(from <= hi) || !Assert(hi <= m.Start.Length)) return Lost;
        for (var step = 0; step < MaxSearch && lo < hi; step++)
        {
            var mid = (lo + hi) >>> 1;
            if (m.Start[mid] < start) lo = mid + 1;
            else hi = mid;
        }

        for (var p = lo; p < Math.Min(m.Length, MaxLocations) && m.Start[p] == start; p++)
            if (ReferenceEquals(m.Refs[p].Loc, target))
                return p;
        return Unseen;
    }

    // Writes the new rows into the spare arrays and swaps them in; on false the caller's Rebuild overwrites the garbage
    private static bool Merge(Mirror m, ReadOnlySpan<ModelDataPoolLocation> list, int fresh, int slots)
    {
        var (n, old, at, gone, copied) = (list.Length, m.Length, m.Used, 0, 0);
        var (allocated, ordered) = (m.AllocatedTris, m.Ordered);
        if (!Assert(slots <= m.Inv.Length) || !Assert(n <= m.NRefs.Length)) return false;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var p = m.Match[i];
            if (p < 0)
            {
                if (!NewRow(m, list[i], i, at++, ref allocated)) return Abandon(m, n);
            }
            else if (i >= copied)
            {
                for (var d = gone; d < Math.Min(p, MaxLocations); d++) allocated -= Drop(m, d);
                var run = Run(m, i, p, n);
                if (run == 0) return Abandon(m, n);
                Array.Copy(m.Refs, p, m.NRefs, i, run);
                Array.Copy(m.Start, p, m.NStart, i, run);
                Array.Copy(m.Count, p, m.NCount, i, run);
                Array.Copy(m.Lod, p, m.NLod, i, run);
                Array.Copy(m.Slot, p, m.NSlot, i, run);
                (gone, copied) = (p + run, i + run);
            }

            m.Inv[m.NSlot[i]] = i;
            ordered &= i == 0 || m.NStart[i - 1] <= m.NStart[i];
        }

        for (var d = gone; d < Math.Min(old, MaxLocations); d++) allocated -= Drop(m, d);
        if (!Assert(at - m.Used == fresh)) return Abandon(m, n);
        for (var s = at; s < Math.Min(slots, MaxSlots); s++) Blank(m, s);
        (m.Refs, m.NRefs, m.Start, m.NStart, m.Count, m.NCount) =
            (m.NRefs, m.Refs, m.NStart, m.Start, m.NCount, m.Count);
        (m.Lod, m.NLod, m.Slot, m.NSlot) = (m.NLod, m.Lod, m.NSlot, m.Slot);
        Array.Clear(m.NRefs, 0, old);
        (m.Length, m.Used, m.Slots, m.AllocatedTris, m.Ordered) = (n, at, slots, allocated, ordered);
        (m.Dead, m.Fresh) = (m.Dead + old - (n - fresh), m.Fresh + fresh);
        return true;
    }

    private static bool NewRow(Mirror m, ModelDataPoolLocation loc, int i, int slot, ref int allocated)
    {
        if (!NotNull(loc.CullVisible) || !Index(i, m.NRefs.Length))
            return false; // Rebuild hands the pool to the engine
        m.NRefs[i] = new LocRef(loc, loc.CullVisible);
        (m.NStart[i], m.NCount[i]) = (loc.IndicesStart * 4, loc.IndicesEnd - loc.IndicesStart);
        (m.NLod[i], m.NSlot[i]) = (loc.LodLevel, slot);
        Place(m, loc.FrustumCullSphere, slot);
        allocated += m.NCount[i] / 3;
        return true;
    }

    private static bool Abandon(Mirror m, int n)
    {
        if (Assert(n <= m.NRefs.Length)) Array.Clear(m.NRefs, 0, n);
        _ = Assert(m.Length <= m.Refs.Length);
        return false;
    }

    private static int Run(Mirror m, int i, int p, int n)
    {
        if (!Index(i, n) || !Assert(m.Match[i] == p)) return 0;
        var run = 1;
        for (var k = 1; k < Math.Min(n - i, MaxLocations) && m.Match[i + k] == p + k; k++) run++;
        return Assert(p + run <= m.Length) ? run : 0;
    }

    private static int Drop(Mirror m, int row)
    {
        if (!Index(row, m.Length) || !Index(m.Slot[row], m.Inv.Length)) return 0;
        m.Inv[m.Slot[row]] = Gone;
        return m.Count[row] / 3;
    }

    // A mirror Diff left a quarter stale is laid down again from its own arrays, a few a frame: a strip unload touches every pool
    private static void Tidy(Mirror m, bool quiet)
    {
        var w = Vector<double>.Count;
        if (!Assert(m.Length <= m.Refs.Length) || !Assert(m.Slots <= m.Cx.Length)) return;
        if (quiet && m.Cells == 0 && !m.Tried && m.Length >= MinBucketed && ++m.Quiet >= QuietCulls)
        {
            Settle(m, w);
            return;
        }

        if (4 * (m.Dead + m.Fresh) <= m.Length + Churn || _frameRelayouts >= RelayoutsPerFrame) return;
        _frameRelayouts++;
        if (Counting.Hud) Relayouts++;
        if (m.Cells > 0 && m.Length >= MinBucketed) Settle(m, w);
        else if (Gather(m, m.Length)) Lay(m, m.Length, w);
    }

    private static void Settle(Mirror m, int w)
    {
        var n = m.Length;
        if (!Assert(n >= MinBucketed) || !Gather(m, n)) return;
        var (minX, maxX, minZ, maxZ) = (int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            (minX, maxX) = (Math.Min(minX, (int)m.Sx[i]), Math.Max(maxX, (int)m.Sx[i]));
            (minZ, maxZ) = (Math.Min(minZ, (int)m.Sz[i]), Math.Max(maxZ, (int)m.Sz[i]));
        }

        if (Assert(minX <= maxX) && Group(m, n, w, minX, maxX, minZ, maxZ))
        {
            (m.Base, m.Used, m.Dead, m.Fresh) = (m.Slots, m.Slots, 0, 0);
            if (Counting.Hud) Settles++;
            return;
        }

        m.Tried = true; // the sort may have half moved the geometry: back to list order, and no second try
        Lay(m, n, w);
    }

    private static bool Gather(Mirror m, int n)
    {
        if (!Assert(n > 0) || !Stage(m, n)) return false;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var s = m.Slot[i];
            if (!Index(s, m.Slots)) return false;
            (m.Sx[i], m.Sy[i], m.Sz[i]) = (m.Cx[s], m.Cy[s], m.Cz[s]);
            (m.Shx[i], m.Shy[i], m.Shz[i]) = (m.Hx[s], m.Hy[s], m.Hz[s]);
        }

        return true;
    }

    private static void Lay(Mirror m, int n, int w)
    {
        m.Cells = 0;
        if (!Assert(n <= m.Sx.Length) || !Assert(n <= m.Cx.Length)) return;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            (m.Cx[i], m.Cy[i], m.Cz[i]) = (m.Sx[i], m.Sy[i], m.Sz[i]);
            (m.Hx[i], m.Hy[i], m.Hz[i]) = (m.Shx[i], m.Shy[i], m.Shz[i]);
            (m.Slot[i], m.Inv[i]) = (i, i);
        }

        Tail(m, n, w);
        (m.Dead, m.Fresh) = (0, 0);
    }

    private static bool Stage(Mirror m, int n)
    {
        if (!Assert(n > 0)) return false;
        if (m.Sx.Length >= n) return true;
        var room = Math.Min(MaxLocations, Math.Max(n, 2 * m.Sx.Length));
        if (!Assert(room >= n)) return false;
        (m.Sx, m.Sy, m.Sz) = (new float[room], new float[room], new float[room]);
        (m.Shx, m.Shy, m.Shz) = (new float[room], new float[room], new float[room]);
        return true;
    }

    // Room for Diff's spare rows too, so no diff allocates
    private static bool Grow(Mirror m, int n, int w)
    {
        var slots = Geometry(n, w);
        if (!Assert(slots >= n) || !Assert(slots <= MaxSlots)) return false;
        if (m.Refs.Length < n)
        {
            var cap = Math.Min(MaxLocations, Math.Max(n, 2 * m.Refs.Length));
            if (!Assert(cap >= n)) return false;
            (m.Refs, m.NRefs, m.Cand) = (new LocRef[cap], new LocRef[cap], new ulong[(cap + WordBits - 1) / WordBits]);
            (m.Start, m.Count, m.Lod, m.Slot) = (new int[cap], new int[cap], new int[cap], new int[cap]);
            (m.NStart, m.NCount, m.NLod, m.NSlot, m.Match) =
                (new int[cap], new int[cap], new int[cap], new int[cap], new int[cap]);
        }

        if (m.Cx.Length >= slots) return true;
        var room = Math.Min(MaxSlots, Math.Max(slots, 2 * m.Cx.Length));
        if (!Assert(room >= slots)) return false;
        (m.Cx, m.Cy, m.Cz) = (new float[room], new float[room], new float[room]);
        (m.Hx, m.Hy, m.Hz) = (new float[room], new float[room], new float[room]);
        (m.Outside, m.Inv) = (new long[room], new int[room]);
        return true;
    }

    // The rows, a full grid's padding (PerCell members a cell, each rounded up to a whole vector) and Diff's rows behind it
    private static int Geometry(int n, int w)
    {
        _ = Assert(n is >= 0 and <= MaxLocations) && Assert(w is > 0 and <= MaxLanes);
        return n + (n / PerCell + 1) * w + n / 4 + 2 * w;
    }

    private static void Tail(Mirror m, int n, int w)
    {
        if (!Assert(w > 0)) return;
        m.Slots = (n + w - 1) / w * w;
        if (!Assert(m.Cx.Length >= m.Slots)) return;
        for (var i = n; i < Math.Min(m.Slots, MaxSlots); i++) Blank(m, i);
        (m.Base, m.Used) = (m.Slots, m.Slots);
    }

    private static void Blank(Mirror m, int slot)
    {
        if (!Index(slot, m.Cx.Length) || !Index(slot, m.Inv.Length)) return;
        (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (0, 0, 0);
        (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (0, 0, 0);
        m.Inv[slot] = Gone;
    }

    // Counting sort of the geometry into an x/z grid by sphere centre; the box tested per cell is built from its members' corners
    private static bool Group(Mirror m, int n, int w, int minX, int maxX, int minZ, int maxZ)
    {
        m.Cells = 0;
        if (!Assert(n >= MinBucketed) || minX > maxX || minZ > maxZ) return false;
        var want = Math.Min(MaxCells, Math.Max(1, n / PerCell));
        var shift = MaxShift; // each step halves the grid, so the pool's span ends this well before MaxShift
        for (var s = CellShift; s < MaxShift; s++)
            if (Span(minX, maxX, s) * Span(minZ, maxZ, s) <= want)
            {
                shift = s;
                break;
            }

        if (!Assert(shift < MaxShift)) return false;
        var (wide, deep) = (Span(minX, maxX, shift), Span(minZ, maxZ, shift));
        var cells = wide * deep;
        if (cells <= 1 || cells > MaxCells)
            return false; // one cell is no grid, and the sweep would only pay for the box
        (m.Shift, m.OriginX, m.OriginZ, m.Wide) = (shift, minX >> shift, minZ >> shift, (int)wide);
        return Bin(m, n, w, (int)cells);
    }

    private static long Span(int min, int max, int shift)
    {
        if (!Assert(min <= max) || !Assert(shift is >= 0 and < MaxShift)) return 1;
        return (long)(max >> shift) - (min >> shift) + 1;
    }

    // Bins turn from member counts into each cell's first slot, on a whole vector; an empty cell is marked, as Box cannot tell "empty"
    // from "starts at zero" once the scatter made them cursors
    private static bool Bin(Mirror m, int n, int w, int cells)
    {
        if (!Room(m, n, cells)) return false;
        Array.Clear(m.Bins, 0, cells);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var cell = (((int)m.Sz[i] >> m.Shift) - m.OriginZ) * m.Wide + ((int)m.Sx[i] >> m.Shift) - m.OriginX;
            if (!Index(cell, cells)) return false;
            (m.Key[i], m.Bins[cell]) = (cell, m.Bins[cell] + 1);
        }

        var (at, used) = (0, 0);
        for (var c = 0; c < Math.Min(cells, MaxCells); c++)
        {
            var count = m.Bins[c];
            m.Bins[c] = count == 0 ? Empty : at;
            if (count == 0) continue;
            at += (count + w - 1) / w * w;
            used++;
        }

        m.Slots = at;
        return Assert(at <= m.Cx.Length) && Grid(m, used, w) && Scatter(m, n, cells) && Box(m, cells, w);
    }

    private static bool Scatter(Mirror m, int n, int cells)
    {
        if (!Assert(cells <= m.LoX.Length)) return false;
        for (var c = 0; c < Math.Min(cells, MaxCells); c++)
        {
            (m.LoX[c], m.HiX[c]) = (double.MaxValue, double.MinValue);
            (m.LoY[c], m.HiY[c]) = (double.MaxValue, double.MinValue);
            (m.LoZ[c], m.HiZ[c]) = (double.MaxValue, double.MinValue);
        }

        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var cell = m.Key[i];
            var slot = m.Bins[cell]++;
            if (!Index(slot, m.Slots)) return false;
            (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (m.Sx[i], m.Sy[i], m.Sz[i]);
            (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (m.Shx[i], m.Shy[i], m.Shz[i]);
            double cx = m.Sx[i], cy = m.Sy[i], cz = m.Sz[i];
            double hx = m.Shx[i], hy = m.Shy[i], hz = m.Shz[i];
            (m.Slot[i], m.Inv[slot]) = (slot, i);
            (m.LoX[cell], m.HiX[cell]) = (Math.Min(m.LoX[cell], cx - hx), Math.Max(m.HiX[cell], cx + hx));
            (m.LoY[cell], m.HiY[cell]) = (Math.Min(m.LoY[cell], cy - hy), Math.Max(m.HiY[cell], cy + hy));
            (m.LoZ[cell], m.HiZ[cell]) = (Math.Min(m.LoZ[cell], cz - hz), Math.Max(m.HiZ[cell], cz + hz));
        }

        return true;
    }

    private static bool Room(Mirror m, int n, int cells)
    {
        if (m.Key.Length < n) m.Key = new int[Math.Min(MaxLocations, Math.Max(n, 2 * m.Key.Length))];
        if (m.Bins.Length < cells)
        {
            var room = Math.Min(MaxCells, Math.Max(cells, 2 * m.Bins.Length));
            m.Bins = new int[room];
            (m.LoX, m.HiX, m.LoY) = (new double[room], new double[room], new double[room]);
            (m.HiY, m.LoZ, m.HiZ) = (new double[room], new double[room], new double[room]);
        }

        return Assert(m.Bins.Length >= cells) && Assert(m.Key.Length >= n);
    }

    private static bool Grid(Mirror m, int cells, int w)
    {
        var padded = (cells + w - 1) / w * w;
        if (!Assert(cells > 0) || !Assert(padded <= MaxCells + MaxLanes)) return false;
        if (m.BStart.Length >= padded)
        {
            m.Cells = cells;
            return true;
        }

        var room = Math.Min(MaxCells + MaxLanes, Math.Max(padded, 2 * m.BStart.Length));
        if (!Assert(room >= padded)) return false;
        (m.BStart, m.BCount) = (new int[room], new int[room]);
        (m.BCx, m.BCy, m.BCz) = (new double[room], new double[room], new double[room]);
        (m.BHx, m.BHy, m.BHz) = (new double[room], new double[room], new double[room]);
        m.BOutside = new long[room];
        m.Cells = cells;
        return true;
    }

    private static bool Box(Mirror m, int cells, int w)
    {
        var (b, at) = (0, 0);
        for (var c = 0; c < Math.Min(cells, MaxCells) && b < m.Cells; c++)
        {
            if (m.Bins[c] == Empty) continue;
            var count = m.Bins[c] - at; // the cursor stopped one past the cell's last member
            if (!Assert(count > 0) || !Index(at, m.Slots) || !Assert(at + count <= m.Slots)) return false;
            (m.BStart[b], m.BCount[b]) = (at, count);
            (m.BCx[b], m.BHx[b]) = ((m.LoX[c] + m.HiX[c]) / 2, (m.HiX[c] - m.LoX[c]) / 2 + Slack);
            (m.BCy[b], m.BHy[b]) = ((m.LoY[c] + m.HiY[c]) / 2, (m.HiY[c] - m.LoY[c]) / 2 + Slack);
            (m.BCz[b], m.BHz[b]) = ((m.LoZ[c] + m.HiZ[c]) / 2, (m.HiZ[c] - m.LoZ[c]) / 2 + Slack);
            var padded = (count + w - 1) / w * w;
            for (var i = at + count; i < Math.Min(at + padded, MaxSlots); i++) Pad(m, b, i);
            (at, b) = (at + padded, b + 1);
        }

        return Assert(b == m.Cells) && Assert(at == m.Slots);
    }

    private static void Place(Mirror m, Sphere s, int slot)
    {
        if (!Index(slot, m.Cx.Length)) return;
        (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (s.x, s.y, s.z);
        // the float division of AABBisOutside; the kernel widens it as the engine does
        (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (s.radius / Sqrt3, s.radiusY / Sqrt3, s.radiusZ / Sqrt3);
    }

    // At its cell's centre a padding lane costs at most the vector it sits in
    private static void Pad(Mirror m, int b, int slot)
    {
        if (!Index(slot, m.Cx.Length) || !Index(b, m.Cells)) return;
        (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = ((float)m.BCx[b], (float)m.BCy[b], (float)m.BCz[b]);
        (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (0, 0, 0);
        m.Inv[slot] = Gone;
    }

    // InFrustumShadowPass's range test, in its float operations, ahead of any plane
    private static void Range(Mirror m, FrustumCulling culler, int from, int to)
    {
        var player = PlayerPos(culler);
        if (!NotNull(player) || !Assert(to <= m.Slots) || !Assert(m.Outside.Length >= m.Slots)) return;
        float px = player.X, pz = player.Z;
        for (var i = from; i < Math.Min(to, MaxSlots); i++)
            m.Outside[i] = Math.Abs(px - m.Cx[i]) >= culler.shadowRangeX ||
                           Math.Abs(pz - m.Cz[i]) >= culler.shadowRangeZ
                ? -1
                : 0;
    }

    // Plane.AABBisOutside a vector at a time, the normal's sign picking the corner; rows Diff added since the grid sit behind it
    private static void Sweep(Mirror m, ReadOnlySpan<Plane> planes, bool ranged, FrustumCulling culler)
    {
        var w = Vector<double>.Count;
        if (!Assert(planes.Length is > 0 and <= PlaneCount) || !Assert(m.Outside.Length >= m.Slots)) return;
        Span<PlaneV> pv = stackalloc PlaneV[PlaneCount];
        var count = Math.Min(planes.Length, PlaneCount);
        for (var p = 0; p < Math.Min(count, PlaneCount); p++) pv[p] = new PlaneV(planes[p]);
        var allOut = new Vector<long>(-1);
        Array.Clear(m.Cand, 0, (m.Length + WordBits - 1) / WordBits);
        var from = 0;
        if (m.Cells > 0)
        {
            SweepBoxes(m, pv, count, w, allOut);
            for (var b = 0; b < Math.Min(m.Cells, MaxCells); b++)
            {
                var (at, padded) = (m.BStart[b], (m.BCount[b] + w - 1) / w * w);
                if (!Assert(at + padded <= m.Base)) return;
                if (m.BOutside[b] != 0)
                {
                    if (Counting.Hud) m.SkippedRows += m.BCount[b];
                    continue;
                }

                if (ranged) Range(m, culler, at, at + padded);
                for (var i = at; i < Math.Min(at + padded, MaxSlots); i += Vector<double>.Count)
                    SweepVector(m, pv, count, ranged, i, allOut);
            }

            from = m.Base;
        }

        if (ranged) Range(m, culler, from, m.Slots);
        for (var i = from; i < Math.Min(m.Slots, MaxSlots); i += Vector<double>.Count)
            SweepVector(m, pv, count, ranged, i, allOut);
    }

    // The plane kernel over the cell boxes. No range test: that runs in float on a sphere centre, which a box is not.
    private static void SweepBoxes(Mirror m, ReadOnlySpan<PlaneV> pv, int count, int w, Vector<long> allOut)
    {
        var padded = (m.Cells + w - 1) / w * w;
        if (!Assert(padded <= m.BCx.Length) || !Assert(padded <= m.BOutside.Length)) return;
        for (var i = m.Cells; i < Math.Min(padded, MaxCells + MaxLanes); i++)
        {
            (m.BCx[i], m.BCy[i], m.BCz[i]) = (0, 0, 0);
            (m.BHx[i], m.BHy[i], m.BHz[i]) = (0, 0, 0);
        }

        for (var i = 0; i < Math.Min(padded, MaxCells + MaxLanes); i += Vector<double>.Count)
        {
            var outside = Vector<long>.Zero;
            Vector<double> cx = new(m.BCx, i), cy = new(m.BCy, i), cz = new(m.BCz, i);
            Vector<double> hx = new(m.BHx, i), hy = new(m.BHy, i), hz = new(m.BHz, i);
            for (var p = 0; p < Math.Min(count, PlaneCount); p++)
            {
                var d = (cx + hx * pv[p].Sx) * pv[p].Nx + (cy + hy * pv[p].Sy) * pv[p].Ny +
                        (cz + hz * pv[p].Sz) * pv[p].Nz + pv[p].D;
                outside |= Vector.LessThan(d, Vector<double>.Zero);
                if (Vector.EqualsAll(outside, allOut)) break;
            }

            outside.CopyTo(m.BOutside, i);
        }
    }

    private static void SweepVector(Mirror m, ReadOnlySpan<PlaneV> pv, int count, bool ranged, int i,
        Vector<long> allOut)
    {
        if (!Assert(i + Vector<double>.Count <= m.Outside.Length)) return;
        var outside = ranged ? new Vector<long>(m.Outside, i) : Vector<long>.Zero;
        if (ranged && Vector.EqualsAll(outside, allOut)) return;
        Vector<double> cx = Widen(m.Cx, i), cy = Widen(m.Cy, i), cz = Widen(m.Cz, i);
        Vector<double> hx = Widen(m.Hx, i), hy = Widen(m.Hy, i), hz = Widen(m.Hz, i);
        for (var p = 0; p < Math.Min(count, PlaneCount); p++)
        {
            var d = (cx + hx * pv[p].Sx) * pv[p].Nx + (cy + hy * pv[p].Sy) * pv[p].Ny +
                    (cz + hz * pv[p].Sz) * pv[p].Nz + pv[p].D;
            outside |= Vector.LessThan(d, Vector<double>.Zero);
        }

        Mark(m, outside, i, allOut);
    }

    // A vector of doubles from as many floats at i. The width is a JIT constant, so this folds to one load and one convert.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<double> Widen(float[] values, int i)
    {
        if (Vector<double>.Count == Vector256<double>.Count)
            return Vector256.WidenLower(Vector128.Create(values.AsSpan(i, 4)).ToVector256Unsafe()).AsVector();
        if (Vector<double>.Count == Vector128<double>.Count)
            return Vector128.WidenLower(Vector64.Create(values.AsSpan(i, 2)).ToVector128Unsafe()).AsVector();
        if (Vector<double>.Count == Vector512<double>.Count)
            return Vector512.WidenLower(Vector256.Create(values.AsSpan(i, 8)).ToVector512Unsafe()).AsVector();
        Span<double> wide = stackalloc double[MaxLanes];
        for (var k = 0; k < Math.Min(Vector<double>.Count, MaxLanes); k++) wide[k] = values[i + k];
        return new Vector<double>(wide);
    }

    private static void Mark(Mirror m, Vector<long> outside, int i, Vector<long> allOut)
    {
        if (Vector.EqualsAll(outside, allOut) || !Assert(i + Vector<long>.Count <= m.Inv.Length)) return;
        var keep = ~Lanes(outside) & (uint.MaxValue >> (32 - Vector<long>.Count));
        for (var k = 0; k < MaxLanes && keep != 0; k++)
        {
            var row = m.Inv[i + BitOperations.TrailingZeroCount(keep)];
            keep &= keep - 1;
            if (row >= 0 && Index(row, m.Length)) m.Cand[row >> 6] |= 1UL << row;
        }
    }

    // One bit per lane outside. The width is a JIT constant, so this folds to one movemask.
    private static uint Lanes(Vector<long> outside)
    {
        if (!Assert(Vector<long>.Count <= MaxLanes)) return uint.MaxValue;
        if (Vector<long>.Count == Vector256<long>.Count) return outside.AsVector256().ExtractMostSignificantBits();
        if (Vector<long>.Count == Vector128<long>.Count) return outside.AsVector128().ExtractMostSignificantBits();
        if (Vector<long>.Count == Vector512<long>.Count)
            return (uint)outside.AsVector512().ExtractMostSignificantBits();
        var bits = 0u;
        for (var k = 0; k < Math.Min(Vector<long>.Count, MaxLanes); k++)
            if (outside[k] != 0)
                bits |= 1u << k;
        return bits;
    }

    // IsVisible's terms are a side-effect-free conjunction (FrustumVisible's only reader, ClientChunk.IsFrustumVisible, has no caller),
    // so LOD and range run first on the flat arrays and only the rest read Hide and Bools, branch-free so the misses overlap.
    // VisibleBufIndex is read once: ChunkCuller swaps it on its own thread, so one snapshot per pool is as good as the engine's reads.
    private static int Emit(Mirror m, FrustumCulling culler, EnumFrustumCullMode mode, int[] starts, int[] sizes,
        out int rendered)
    {
        rendered = 0;
        if (!Scratch(m) || !Assert(starts.Length >= 2 * m.Length) || !Assert(sizes.Length >= m.Length)) return -1;
        var k = mode switch
        {
            EnumFrustumCullMode.CullNormal => Survivors(m, culler),
            EnumFrustumCullMode.CullInstantShadowPassFar => Candidates(m, 1),
            _ => Candidates(m, int.MinValue) // LodLevel plays no part in CullInstant and the near shadow pass
        };
        if (k < 0) return -1;
        var (rows, ok, refs, buffer) = (m.Rows, m.Ok, m.Refs, ModelDataPoolLocation.VisibleBufIndex);
        for (var q = 0; q < Math.Min(k, MaxLocations); q++)
        {
            ref readonly var row = ref refs[rows[q]];
            ok[q] = !row.Loc.Hide;
            ok[q] &= row.Vis[buffer]; // no short circuit: a branch on the first load would hold up the second
        }

        var groups = 0;
        for (var q = 0; q < Math.Min(k, MaxLocations); q++)
        {
            if (!ok[q]) continue;
            var i = rows[q];
            (starts[groups * 2], sizes[groups]) = (m.Start[i], m.Count[i]);
            rendered += m.Count[i] / 3;
            groups++;
        }

        return groups;
    }

    private static bool Scratch(Mirror m)
    {
        var n = m.Length;
        if (!Assert(n <= MaxLocations)) return false;
        if (m.Rows.Length >= n) return true;
        var cap = Math.Min(MaxLocations, Math.Max(n, 2 * m.Rows.Length));
        (m.Rows, m.Ok) = (new int[cap], new bool[cap]);
        return Assert(m.Ok.Length >= n);
    }

    // The shadow passes had their range test in the sweep; only the far pass drops LOD 0 (IsVisible, CullInstantShadowPassFar)
    private static int Candidates(Mirror m, int minLod)
    {
        var (k, words) = (0, (m.Length + WordBits - 1) / WordBits);
        if (!Assert(words <= m.Cand.Length) || !Assert(m.Rows.Length >= m.Length)) return -1;
        for (var word = 0; word < Math.Min(words, MaxWords); word++)
        {
            var bits = m.Cand[word];
            for (var b = 0; b < WordBits && bits != 0; b++)
            {
                var i = word * WordBits + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (m.Lod[i] >= minLod) m.Rows[k++] = i;
            }
        }

        return k;
    }

    private static int Survivors(Mirror m, FrustumCulling culler)
    {
        var (k, words, player) = (0, (m.Length + WordBits - 1) / WordBits, PlayerPos(culler));
        if (!NotNull(player) || !Assert(words <= m.Cand.Length) || !Assert(m.Rows.Length >= m.Length)) return -1;
        for (var word = 0; word < Math.Min(words, MaxWords); word++)
        {
            var bits = m.Cand[word];
            for (var b = 0; b < WordBits && bits != 0; b++)
            {
                var i = word * WordBits + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (LodVisible(m, i, player, culler)) m.Rows[k++] = i;
            }
        }

        return k;
    }

    // IsVisible's CullNormal range and LOD test with the engine's operations
    private static bool LodVisible(Mirror m, int i, BlockPos player, FrustumCulling culler)
    {
        if (!Index(i, m.Length)) return false;
        var slot = m.Slot[i];
        if (!Index(slot, m.Slots)) return false;
        double d = player.HorDistanceSqTo(m.Cx[slot], m.Cz[slot]);
        return m.Lod[i] switch
        {
            0 => culler.lod0BiasSq > 0f && d < culler.lod0BiasSq + 1024f,
            1 => d < culler.ViewDistanceSq,
            2 => d <= culler.lod2BiasSq,
            3 => d > culler.lod2BiasSq && d < culler.ViewDistanceSq,
            _ => false
        };
    }

    // A reference stored into ModelDataPoolLocation[] or Bools[] pays the covariant store check (neither class is sealed), which reads the
    // object's method table: a cache miss per row. A struct array takes the store with a write barrier only.
    private readonly record struct LocRef(ModelDataPoolLocation Loc, Bools Vis);

    private sealed class Mirror
    {
        public double[] BCx = [], BCy = [], BCz = [], BHx = [], BHy = [], BHz = [];
        public long[] BOutside = [];
        public int[] BStart = [], BCount = [];

        // Geometry: [0, Base) the grid or list order, [Base, Used) what Diff added since, Slots padded
        public int Base, Used;

        public int[] Bins = [];
        public ulong[] Cand = []; // per row: the geometry survived the planes
        public int Cells; // 0 = no grid
        public float[] Cx = [], Cy = [], Cz = [], Hx = [], Hy = [], Hz = []; // slot order
        public int Dead, Fresh;
        public int[] Inv = []; // slot -> row, Gone for padding and removed rows
        public int[] Key = [];
        public int Length, AllocatedTris, ListVersion;
        public double[] LoX = [], HiX = [], LoY = [], HiY = [], LoZ = [], HiZ = [];
        public int[] Lod = [];
        public int[] Match = [];
        public LocRef[] NRefs = [];
        public int[] NStart = [], NCount = [], NLod = [], NSlot = [];
        public bool[] Ok = [];
        public bool Ordered; // the rows' starts never decrease, so Diff may bisect
        public long[] Outside = [];
        public object? Owner;
        public int Pass = -1, Groups; // the Render call whose result the pool holds, and its range count
        public int Quiet;
        public LocRef[] Refs = [];
        public int[] Rows = []; // Emit's scratch, per mirror so pools cull in parallel
        public int Shift, OriginX, OriginZ, Wide;
        public long SkippedRows;
        public int[] Slot = []; // row -> slot
        public int Slots;
        public int[] Start = [], Count = [];
        public float[] Sx = [], Sy = [], Sz = [], Shx = [], Shy = [], Shz = [];
        public bool Tried; // Settle found no grid worth having for this list
    }

    private readonly struct PlaneV(Plane p)
    {
        public readonly Vector<double> Nx = new(p.normalX), Ny = new(p.normalY), Nz = new(p.normalZ), D = new(p.D);

        public readonly Vector<double> Sx = new(p.normalX > 0 ? 1.0 : -1.0),
            Sy = new(p.normalY > 0 ? 1.0 : -1.0),
            Sz = new(p.normalZ > 0 ? 1.0 : -1.0);
    }
}

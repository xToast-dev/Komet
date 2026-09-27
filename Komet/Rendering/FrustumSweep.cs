using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// MeshDataPool.FrustumCull with the engine's result, bit for bit and in its order, but a vector at a time. The parts:
//   FrustumSweep     the patches, the prefix that takes a pool's cull, and Swept: one pool's cull, Sweep then Emit
//   .Workers         a Render call's pools culled on Komet's worker pool (Precull)
//   .Stage           all Render calls of a render stage culled as one batch
//   .Mirror          per pool, the engine's location list as flat arrays, kept up to date by diffing the list
//   .Grid            the geometry's layout: a pool quiet for QuietCulls culls is sorted into an x/z grid whose cell boxes are
//                    tested first. AddModel fills pools first fit, so only sorting lets a box reject (2/3 camera, 6/7 far shadow),
//                    and sorting on every change cost 8 %
//   .Kernel          Plane.AABBisOutside on a vector of rows in its operation order, then IsVisible's other terms
// Geometry is stored as the sphere's floats and widened in the kernel: 5-10 % faster than doubles.
internal static partial class FrustumSweep
{
    // Limits
    private const int MaxLocations = 65536, MaxPools = 4096, PlaneCount = 6, MaxLanes = 32;
    private const int WordBits = 64, MaxWords = MaxLocations / WordBits;
    // Grid: cells of 2^CellShift blocks and more, about PerCell rows each, for pools of MinBucketed rows and more
    private const int CellShift = 5, MaxCells = 16384, MinBucketed = 256, PerCell = 8, MaxShift = 30;
    private const int QuietCulls = 8;
    private const double Slack = 1.0; // a cell box grows by a block, so rounding never shrinks it inside a member
    // Diff: how far it looks for a survivor, and how much change it absorbs before a relayout (a few a frame)
    private const int Window = 8, MaxSearch = 32, ScanBudget = 8;
    private const int Churn = 64, RelayoutsPerFrame = 4, MaxRelayoutTries = 64;
    // Workers: about 0.3 ms of culling; less is not worth waking a worker for
    private const int RowsPerHelper = 4096;

    // every cell can pad up to a vector, and Diff appends behind the grid until a quarter of the rows are new
    private const int MaxSlots =
        MaxLocations + (MaxLocations / PerCell + 1) * MaxLanes + MaxLocations / 4 + 2 * MaxLanes;

    private const int Empty = -1, Gone = -1, Foreign = -1, Unseen = -1, Lost = -2;
    private const float Sqrt3 = 1.7320508f; // Plane.SQRT3

    private static int _frameRebuilds, _frameRelayouts;
    private static long _rebuilds, _diffed, _relayouts, _settles;
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static long Tested { get; private set; } // totals while Counting.Hud, main thread
    public static long Visible { get; private set; }
    public static long Skipped { get; private set; }
    // Updated by the workers too while they bring their batch's mirrors up to date: through Interlocked
    public static long Rebuilds => Interlocked.Read(ref _rebuilds);
    public static long Diffed => Interlocked.Read(ref _diffed);
    internal static long Relayouts => Interlocked.Read(ref _relayouts);
    public static long Settles => Interlocked.Read(ref _settles);
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
        // a stage batch may only open once every frame, and every change to a pool's list, is known to end it first
        var loop = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainRenderLoop));
        var (add, remove) = (AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        _staging = _parallel && NotNull(loop) && NotNull(add) && NotNull(remove) &&
                   NotNull(harmony.Patch(loop, new HarmonyMethod(EndFrame), new HarmonyMethod(EndFrame))) &&
                   NotNull(harmony.Patch(add, new HarmonyMethod(Unbatch) { priority = Priority.First })) &&
                   NotNull(harmony.Patch(remove, new HarmonyMethod(Unbatch) { priority = Priority.First }));
    }

    internal static void Clear()
    {
        EndFrame(); // no batch may outlive the world whose pools it holds
        Forget();
        ForgetPlans();
        Array.Clear(_mirrors);
        Unnumbered.Clear();
        (_frameRebuilds, _frameRelayouts, _jobCount, _jobCuller) = (0, 0, 0, null);
        (_jobPools, _jobMirrors, _jobManager, _parallel, _staging) = ([], [], [], true, false);
        _ = Assert(_mirrors.Length <= MaxPools);
    }

    // Postfix on MeshDataPoolMasterManager.OnFrame, once a frame (ChunkRenderer.OnBeforeRenderOpaque)
    internal static void NextFrame()
    {
        var rebuilt = Interlocked.Exchange(ref _frameRebuilds, 0);
        if (Counting.Hud && Assert(rebuilt >= 0)) MaxRebuilds = Math.Max(MaxRebuilds, rebuilt);
        _ = Assert(Interlocked.Exchange(ref _frameRelayouts, 0) <= RelayoutsPerFrame);
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
            or EnumFrustumCullMode.CullInstantShadowPassNear or EnumFrustumCullMode.CullInstantShadowPassFar;
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
}

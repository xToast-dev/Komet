using System.Diagnostics;

namespace Komet.Rendering;

// One Render call culls its pools on Komet's worker pool first (a frame job); the engine's loop then only draws. Below ParallelRows
// the hand-off costs more than it saves. _pass stamps which Render call a mirror's result belongs to.
internal static partial class FrustumSweep
{
    private static readonly Action<int> Job = CullJob;

    // The job table: pool, mirror and manager within its batch per job; what they are culled with
    private static MeshDataPool?[] _jobPools = [];
    private static Mirror[] _jobMirrors = [];
    private static int[] _jobManager = [];
    private static int _jobCount, _pass;
    private static FrustumCulling? _jobCuller;
    private static EnumFrustumCullMode _jobMode;
    private static bool _parallel = true; // the Render patch is in

    internal static int ParallelRows { get; set; } = 8192;
    internal static long ParallelCalls { get; private set; } // Render calls whose pools the workers culled

    // Prefix on MeshDataPoolManager.Render: the mirrors are brought up to date here on the main thread (they read the engine's lists and
    // share the relayout budget), then every pool's sweep and emit runs on the workers: as part of its stage's batch (Staged), or on its
    // own (Alone). Mini-dimension pools stay the engine's. Whatever cannot use a batch ends it first: the engine's loop must never cull
    // a pool beside a worker that culls it.
    internal static void Precull(MeshDataPoolManager __instance, EnumFrustumCullMode frustumCullMode)
    {
        _pass++;
        if (!Enabled || !_parallel || frustumCullMode == EnumFrustumCullMode.NoCull || WorkerPool.Running == 0 ||
            WorkerPool.FrameOff)
        {
            EndStage();
            return;
        }

        var (pools, culler) = (Pools(__instance), Culler(__instance));
        if (NotNull(pools) && pools.Count == 0) return; // a pass without pools: nothing to cull, nothing a batch holds
        // before the player exists (world load) the engine's own loop culls, as it does for any pool FrustumCull declines
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

    // This call's pools alone, as one frame batch it waits for; below ParallelRows the engine's loop culls pool by pool
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
        // the main thread takes a share itself
        var result = WorkerPool.RunFrame(Job, _jobCount, rows / RowsPerHelper - 1);
        if (result == WorkerPool.FrameResult.Done) ParallelCalls++;
        else if (result == WorkerPool.FrameResult.Failed && Assert(WorkerPool.LastError is not null))
        {
            _pass++; // no half-finished result is used; the engine's loop culls every pool of this call
            _logger?.Error(
                "Komet: frustum culling on the worker threads failed, this frame culls on the main thread{0}: {1}",
                WorkerPool.FrameOff ? " and so does every later one" : "", WorkerPool.LastError);
        }

        Tally();
    }

    // The pools of one manager, appended to the job table as the `manager`th of a batch; the rows they hold. Deferred, the job brings
    // each mirror up to date itself (Update), and the rows are the lists' counts.
    private static int Collect(List<MeshDataPool> pools, int manager, bool deferred = false)
    {
        var (rows, n) = (0, Math.Min(pools.Count, MaxPools));
        if (!Index(manager, MaxStage) || !Assert(_jobCount + n <= MaxJobs)) return 0;
        if (_jobPools.Length < _jobCount + n) Reserve(_jobCount + n);
        for (var i = 0; i < Math.Min(n, MaxPools); i++)
        {
            var pool = pools[i];
            if (pool is null || Dimension(pool) == 1) continue;
            var (slot, locations) = (SlotOf(pool), MeshPool.Locations(pool));
            if (slot == Foreign || locations is null || locations.Count > MaxLocations) continue;
            var m = deferred ? Owned(slot, locations) : MirrorOf(slot, locations);
            // a new mirror takes its list here, so a second list under the same slot gets a mirror of its own
            if (deferred && m is { Owner: null }) m = Update(m, locations);
            if (m is null || (!deferred && !ReferenceEquals(m.Owner, locations))) continue;
            (_jobPools[_jobCount], _jobMirrors[_jobCount], _jobManager[_jobCount]) = (pool, m, manager);
            _jobCount++;
            rows += deferred ? locations.Count : m.Length;
        }

        return rows;
    }

    // The job table grown to hold `n`, keeping what it holds
    private static void Reserve(int n)
    {
        var size = Capacity(n, _jobPools.Length, int.MaxValue);
        Array.Resize(ref _jobPools, size);
        Array.Resize(ref _jobMirrors, size);
        Array.Resize(ref _jobManager, size);
        _ = Assert(_jobPools.Length >= n) && Assert(_jobManager.Length == _jobMirrors.Length);
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

    // The job table let go of its pools: none outlives its world through it. Rows a cell skipped count for the HUD where their pool
    // was drawn: here after Alone's batch, in Stamp for a stage batch's, and not at all for a pool no call took out of one.
    private static void Tally(bool drawn = true)
    {
        for (var j = 0; j < Math.Min(_jobCount, MaxJobs); j++)
        {
            if (Counting.Hud && drawn) Skipped += _jobMirrors[j].SkippedRows;
            (_jobMirrors[j].SkippedRows, _jobPools[j]) = (0, null);
        }

        (_jobCount, _jobCuller) = (0, null);
    }

    // Postfix: a result stamped in this call is never read by a later FrustumCull
    internal static void Culled() => _pass++;
}

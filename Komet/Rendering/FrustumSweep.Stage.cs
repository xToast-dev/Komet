using System.Runtime.InteropServices;
using Plane = Vintagestory.API.Client.Plane;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// ChunkRenderer draws a stage as one Render call per pass and atlas, all with the frustum the stage set up. Culled call by call,
// the main thread waited for every call's pools; a stage batch hands the workers, at the stage's first call, the pools of every
// manager this stage called last frame, so later calls find theirs culled or nearly.
//
// A batch's results equal the call's own cull: the pools only change before the stages and in the tasks after them (the frame's
// start and end close every batch), the culler's state is the same (SameStage), and the flags the emit reads (Hide, CullVisible)
// are written by the chunk culler's thread, never in step with a frame. The engine's loop must never cull a pool beside a worker.
internal static partial class FrustumSweep
{
    private const int MaxStage = 64, Modes = 5, MaxOrdinals = 8, MaxWaits = 1 << 24, MaxJobs = MaxPools * MaxStage;

    private static readonly Action<int> StageJob = CullStaged;

    // The stage under way: the culler's state its first call saw
    private static readonly Plane[] StagePlanes = new Plane[PlaneCount];
    private static FrustumCulling? _stageCuller;
    private static EnumFrustumCullMode _stageMode;
    private static int _stageX, _stageY, _stageZ, _stageDimension, _stageView;
    private static float _stageLod0;
    private static double _stageLod2, _stageRangeX, _stageRangeZ;
    private static bool _staging, _staged, _stagePlayer, _stageCasting, _open;
    private static int _stage, _ordinal;

    // Stages per mode this frame; the managers the stage under way called, and what each (mode, ordinal) stage called
    private static readonly int[] Ordinals = new int[Modes];
    private static readonly MeshDataPoolManager?[] Called = new MeshDataPoolManager?[MaxStage];
    private static readonly MeshDataPoolManager?[] Plans = new MeshDataPoolManager?[Modes * MaxOrdinals * MaxStage];
    private static readonly int[] PlanLengths = new int[Modes * MaxOrdinals];
    private static int _called;

    // The open batch: managers in order, each one's first job (and the last's end), pool count and jobs done
    private static readonly MeshDataPoolManager?[] Batched = new MeshDataPoolManager?[MaxStage];
    private static readonly int[] BatchStart = new int[MaxStage + 1], BatchPools = new int[MaxStage];
    private static readonly int[] BatchDone = new int[MaxStage];
    private static int _batched;

    public static bool Stages { get; set; } = true;
    internal static long StagedCalls { get; private set; } // calls a stage batch culled, while Counting.Hud

    // Prefix and postfix on ClientMain.MainRenderLoop: every batch ends with the frame
    internal static void EndFrame()
    {
        EndStage();
        Array.Clear(Ordinals);
        _ = Assert(!_open) && Assert(!_staged);
    }

    // Prefix on MeshDataPool.TryAdd and RemoveLocation: no worker may read a list about to change. ChunkRenderer removes queued
    // locations between the shadow stages and the opaque one; the rest of the stage under way culls call by call.
    internal static void Unbatch()
    {
        Shut();
        _ = Assert(!_open);
    }

    // True when a stage batch culled this call's pools (stamped for its FrustumCull, as Alone's are)
    private static bool Staged(MeshDataPoolManager manager, List<MeshDataPool> pools, FrustumCulling culler,
        EnumFrustumCullMode mode)
    {
        if (!_staging || !Stages)
        {
            EndStage();
            return false;
        }

        if (!_staged || !SameStage(culler, mode))
        {
            EndStage();
            Begin(culler, mode);
            _ = Open(manager, culler, mode);
        }

        Record(manager);
        if (_open && Consume(manager, pools)) return true;
        Shut(); // this stage now culls call by call
        return false;
    }

    private static void Begin(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var index = (int)mode;
        _ordinal = Index(index, Modes) && Ordinals[index] < MaxOrdinals ? Ordinals[index]++ : -1;
        (_staged, _stageCuller, _stageMode, _stageCasting) = (true, culler, mode, Casting);
        Planes(culler).AsSpan(0, PlaneCount).CopyTo(StagePlanes);
        var player = PlayerPos(culler);
        _stagePlayer = player is not null;
        if (player is not null)
            (_stageX, _stageY, _stageZ, _stageDimension) = (player.X, player.Y, player.Z, player.dimension);
        (_stageView, _stageLod0, _stageLod2) = (culler.ViewDistanceSq, culler.lod0BiasSq, culler.lod2BiasSq);
        (_stageRangeX, _stageRangeZ) = (culler.shadowRangeX, culler.shadowRangeZ);
        _ = Assert(_called == 0) && Assert(_batched == 0);
    }

    // Everything the sweep and emit read of the culler, as Begin saw it; planes bit for bit
    private static bool SameStage(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var (planes, player) = (Planes(culler), PlayerPos(culler));
        if (!ReferenceEquals(culler, _stageCuller) || mode != _stageMode || !NotNull(planes) ||
            !Assert(planes.Length >= PlaneCount) || (player is not null) != _stagePlayer || Casting != _stageCasting)
            return false;
        if (player is not null &&
            (player.X != _stageX || player.Y != _stageY || player.Z != _stageZ || player.dimension != _stageDimension))
            return false;
        return culler.ViewDistanceSq == _stageView && Same(culler.lod0BiasSq, _stageLod0) &&
               Same(culler.lod2BiasSq, _stageLod2) && Same(culler.shadowRangeX, _stageRangeX) &&
               Same(culler.shadowRangeZ, _stageRangeZ) &&
               MemoryMarshal.AsBytes(planes.AsSpan(0, PlaneCount))
                   .SequenceEqual(MemoryMarshal.AsBytes(StagePlanes.AsSpan()));
    }

    private static bool Same(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void Record(MeshDataPoolManager manager)
    {
        if (Array.IndexOf(Called, manager, 0, _called) >= 0 || !Assert(_called <= MaxStage)) return;
        if (_called < MaxStage) Called[_called++] = manager;
    }

    // The stage's plan as one batch, `first` in front; false when not worth the workers
    private static bool Open(MeshDataPoolManager first, FrustumCulling culler, EnumFrustumCullMode mode)
    {
        (_jobCount, _batched) = (0, 0);
        var rows = Add(first);
        var plan = Plan(mode, _ordinal);
        for (var i = 0; plan >= 0 && i < Math.Min(PlanLengths[plan], MaxStage); i++)
            if (Plans[plan * MaxStage + i] is { } manager && !ReferenceEquals(manager, first)) rows += Add(manager);
        if (rows < ParallelRows || _jobCount < 2)
        {
            Forget();
            return false;
        }

        (_jobCuller, _jobMode, _stage) = (culler, mode, _stage + 1);
        _open = WorkerPool.OpenFrame(StageJob, _jobCount, Math.Max(1, rows / RowsPerHelper));
        if (!_open) Forget();
        return _open;
    }

    private static int Add(MeshDataPoolManager manager)
    {
        var pools = Pools(manager);
        if (!NotNull(pools) || _batched >= MaxStage || !ReferenceEquals(Culler(manager), _stageCuller)) return 0;
        var k = _batched++;
        (Batched[k], BatchPools[k], BatchDone[k], BatchStart[k]) = (manager, pools.Count, 0, _jobCount);
        var rows = Collect(pools, k, deferred: true);
        BatchStart[k + 1] = _jobCount;
        return Assert(BatchStart[k + 1] >= BatchStart[k]) ? rows : 0;
    }

    // This call's pools out of the open batch: the main thread runs the jobs no worker claimed, then waits for the rest. False when
    // the batch does not hold the manager as it is now.
    private static bool Consume(MeshDataPoolManager manager, List<MeshDataPool> pools)
    {
        var k = Array.IndexOf(Batched, manager, 0, _batched);
        if (k < 0 || !Index(k, MaxStage) || pools.Count != BatchPools[k]) return false;
        var (from, to) = (BatchStart[k], BatchStart[k + 1]);
        _ = WorkerPool.ClaimBelow(to);
        var spin = new SpinWait();
        for (var i = 0; i < MaxWaits && Volatile.Read(ref BatchDone[k]) < to - from; i++) spin.SpinOnce(-1);
        if (!Assert(Volatile.Read(ref BatchDone[k]) == to - from)) return false;
        for (var j = from; j < Math.Min(to, MaxJobs); j++) Stamp(j);
        if (Counting.Hud) StagedCalls++;
        return true;
    }

    // A pool the batch culled with its list unchanged: fields as the engine's FrustumCull leaves them, stamped for this call
    private static void Stamp(int j)
    {
        var (pool, m) = (_jobPools[j], _jobMirrors[j]);
        if (!NotNull(pool) || !NotNull(m) || m.Batch != _stage) return;
        var locations = Locations(pool);
        if (!ReferenceEquals(m.Owner, locations) || Version(locations) != m.ListVersion) return;
        (pool.indicesGroupsCount, pool.RenderedTriangles, pool.AllocatedTris) = (m.Groups, m.Rendered, m.AllocatedTris);
        m.Pass = _pass;
        if (Counting.Hud) (Skipped, m.SkippedRows) = (Skipped + m.SkippedRows, 0);
    }

    // One pool of the open batch, on a worker or in Consume. Its manager's count goes up whatever happens, so a waiting call never
    // waits on a job that threw; the pool is stamped with the stage only when culled.
    private static void CullStaged(int j)
    {
        var k = Index(j, _jobCount) ? _jobManager[j] : -1;
        try
        {
            var (pool, m, culler) = k >= 0 ? (_jobPools[j], _jobMirrors[j], _jobCuller) : (null, null, null);
            if (pool is null || m is null || culler is null) return;
            var locations = Locations(pool);
            if (locations is null || !ReferenceEquals(Update(m, locations)?.Owner, locations)) return;
            var groups = Swept(m, culler, _jobMode, pool.indicesStartsByte, pool.indicesSizes, out var rendered);
            if (groups >= 0) (m.Groups, m.Rendered, m.Batch) = (groups, rendered, _stage);
        }
        finally
        {
            if (Index(k, MaxStage)) _ = Interlocked.Increment(ref BatchDone[k]);
        }
    }

    // The stage ends: its batch is closed and what it called is its place's plan next frame
    private static void EndStage()
    {
        Shut();
        if (!_staged) return;
        var plan = Plan(_stageMode, _ordinal);
        if (plan >= 0 && Assert(_called <= MaxStage))
        {
            Array.Copy(Called, 0, Plans, plan * MaxStage, _called);
            Array.Clear(Plans, plan * MaxStage + _called, MaxStage - _called);
            PlanLengths[plan] = _called;
        }

        Array.Clear(Called, 0, _called);
        (_staged, _called, _stageCuller) = (false, 0, null);
    }

    // Closes the open batch: claimed jobs have ended, the others never run
    private static void Shut()
    {
        if (!_open) return;
        _open = false;
        var result = WorkerPool.CloseFrame();
        if (result == WorkerPool.FrameResult.Failed && Assert(WorkerPool.LastError is not null))
            _logger?.Error(
                "Komet: frustum culling on the worker threads failed, those pools cull on the main thread{0}: {1}",
                WorkerPool.FrameOff ? " from now on" : "", WorkerPool.LastError);
        Forget();
    }

    private static void Forget()
    {
        Tally(drawn: false);
        Array.Clear(Batched, 0, _batched);
        _batched = 0;
        _ = Assert(_jobCount == 0);
    }

    private static int Plan(EnumFrustumCullMode mode, int ordinal)
    {
        var index = (int)mode;
        return Index(index, Modes) && ordinal is >= 0 and < MaxOrdinals ? index * MaxOrdinals + ordinal : -1;
    }
}

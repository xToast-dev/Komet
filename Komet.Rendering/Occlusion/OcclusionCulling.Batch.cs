using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// The frame's rows calls culled in two compute breaks instead of one each. At the first surface call every RenderOpaque
// call's draws are laid out and uploaded at once and the surfaces' pass 0 runs as one dispatch; after the surfaces the
// pyramid, their pass 1 and the later passes' pass 0 (and sort) run back to back. Under Vulkan each break ends the
// rendering and drains the GPU when it begins again: one a call cost more than the culling hid. A call whose held draws,
// culler, camera or back-face state differ from what was laid out is culled on its own, as before.
internal static partial class OcclusionCulling
{
    private const int MaxBatched = 64;
    private static readonly int[] SurfacePasses = [Opaque, Topsoil];
    private static readonly int[] LaterPasses = [2, 1, 8]; // RenderOpaque's after the surfaces
    private static readonly Batched[] Batch = new Batched[MaxBatched];
    private static int _batched, _batchCall = -1, _laterFirst, _laterCount, _laterMaxRows;
    private static bool _batchTried, _laterReady, _laterSorted;
    private static FrustumCulling? _batchCuller;

    // Calls batched (laid out at the frame's first surface call), and those culled on their own after all
    public static long BatchedCalls { get; private set; }
    public static long UnbatchedCalls { get; private set; }

    private static void BeginBatch()
    {
        (_batched, _batchCall, _laterFirst, _laterCount, _laterMaxRows) = (0, -1, 0, 0, 0);
        (_batchTried, _laterReady, _laterSorted, _batchCuller) = (false, false, false, null);
    }

    private static void ClearBatch()
    {
        Array.Clear(Batch);
        BeginBatch();
    }

    // At the frame's first surface rows call (its matrices, camera and cull state set): every opaque call laid out, rows
    // and draw data uploaded, the surfaces' pass 0 dispatched. False leaves the frame to per-call culling.
    private static bool Prepare(FrustumCulling? culler, Vec3d camera, bool backFaces)
    {
        _batchTried = true;
        if (_renderer is null || PassPools(_renderer) is not { } passes || culler is null || _frameDraws != 0 ||
            _frameRanges != 0)
        {
            return false;
        }

        var (cursor, draws) = (0, 0);
        var surfaceRows = LayPasses(passes, SurfacePasses, (culler, camera), (true, backFaces), ref cursor, ref draws);
        var (surfaceRanges, surfaceDraws) = (cursor, draws);
        if (surfaceDraws == 0) return false;

        _laterFirst = draws;
        _laterMaxRows = LayPasses(passes, LaterPasses, (culler, camera), (false, false), ref cursor, ref draws);
        _laterCount = draws - _laterFirst;
        var counters = CounterBuffers[_frame % Ring];
        UploadRows((0, draws), culler, camera, (0, backFaces));
        Prime((0, draws), counters, true);
        BindRows(counters);
        var shift = (camera.X - _px, camera.Y - _py, camera.Z - _pz);
        RowsPass(Previous, shift, (0, _previousReady ? Test : Keep, StatUints, 0), (0, surfaceDraws), surfaceRows);
        Gpu.Texture(DepthPyramid.Unit, 0);
        (_late, _compared, _surfaceRanges, _surfaceDraws) = (true, _previousReady, surfaceRanges, surfaceDraws);
        (_surfaceMaxRows, _surfaceRows, _surfaceBackFaces) = (surfaceRows, true, backFaces);
        (_frameRanges, _frameDraws, _rowsCuller, _batchCuller) = (cursor, draws, culler, culler);
        return true;
    }

    // Each pass's atlases as their calls will draw them; returns the most rows a pool of them has
    private static int LayPasses(MeshDataPoolManager?[]?[] passes, int[] which, (FrustumCulling Culler, Vec3d Camera) at,
        (bool Surface, bool BackFaces) how, ref int cursor, ref int draws)
    {
        var most = 0;
        foreach (var pass in which.Bounded(MaxPasses))
        {
            if (!Index(pass, Math.Min(passes.Length, MaxPasses)) || passes[pass] is not { } atlases) continue;

            foreach (var manager in atlases.Bounded(MaxAtlases))
            {
                if (manager is not null && _batched < MaxBatched &&
                    LayCall(manager, at, (how.BackFaces ? Runs : 1, MaxDraws), ref cursor, ref draws) is { } laid)
                {
                    Batch[_batched++] = new Batched(manager, laid.First, laid.Count, how.Surface);
                    most = Math.Max(most, _callMaxRows);
                }
            }
        }

        return most;
    }

    // One call's draws as Lay makes them when its Render hands them to Hold (factor commands a row, draws before end)
    private static (int First, int Count)? LayCall(MeshDataPoolManager manager,
        (FrustumCulling Culler, Vec3d Camera) at,
        (int Factor, int End) how, ref int cursor, ref int draws)
    {
        if (Pools(manager) is not { } pools || !ReferenceEquals(Culler(manager), at.Culler) ||
            cursor + Scan(pools) * how.Factor > MaxRanges || draws + pools.Count > how.End ||
            !Rowed(at.Culler, pools, true))
        {
            return null;
        }

        Held.Clear(); // the VAOs the call's Render will hand Hold: its pools with locations (Bypassed lets each draw)
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            if (Dimension(pools[i]) == 0 && Locations(pools[i]) is { Count: > 0 } && ModelRef(pools[i]) is VAO vao)
            {
                Held.Add(vao);
            }
        }

        var first = draws;
        draws = Lay(pools, at.Camera, ref cursor, (draws, how.End), how.Factor);
        return (first, draws - first);
    }

    // A rows call's hold from the batch (laid out now at the frame's first surface call); false: culled on its own
    private static bool HoldingBatched(MeshDataPoolManager manager, List<MeshDataPool> pools, Vec3d playerpos,
        (string Origin, bool Surface, bool Last, bool BackFaces) call)
    {
        if (_camera is null)
        {
            (Eye.X, Eye.Y, Eye.Z, _camera, _view) = (playerpos.X, playerpos.Y, playerpos.Z, Eye, Seen());
        }

        if (!Capture()) return false;

        if (!_batchTried && call.Surface) _ = Prepare(Culler(manager), _camera, call.BackFaces);

        var entry = Batching(manager, playerpos, call.Surface, call.BackFaces);
        if (entry < 0) return false;

        _ = Scan(pools); // Mini, for Hold
        (_holding, Bypassing, _batchCall, _origin) = (true, true, entry, call.Origin);
        (_surface, _lastSurface, _backFaces) = (call.Surface, call.Last, call.BackFaces);
        Held.Clear();
        return true;
    }

    // The batched entry of a call about to be held, -1 when it is not batched or its state differs from the layout
    private static int Batching(MeshDataPoolManager manager, Vec3d playerpos, bool surface, bool backFaces)
    {
        if (_batchCuller is null || !ReferenceEquals(Culler(manager), _batchCuller) || _camera is not { } camera ||
            !Same((camera.X, camera.Y, camera.Z), playerpos))
        {
            return -1;
        }

        if (surface ? backFaces != _surfaceBackFaces : !_laterReady) return -1;

        for (var b = 0; b < Math.Min(_batched, MaxBatched); b++)
        {
            if (ReferenceEquals(Batch[b].Manager, manager)) return Batch[b].Surface == surface ? b : -1;
        }

        return -1;
    }

    // The held draws are those laid out: issued from the batch's commands; false when culled on their own instead
    private static bool IssuingBatched(int entry)
    {
        var batched = Batch[entry];
        if (!Matches(batched)) return false;

        var draws = (batched.First, batched.Count);
        var counters = CounterBuffers[_frame % Ring];
        Issue(draws, (0, StatUints), counters, !batched.Surface && _laterSorted && Orderless());
        if (batched is { Surface: true, Count: > 0 })
        {
            Remember(draws); // a pool with nothing to draw this frame (at the start)
        }

        return true;
    }

    private static bool Same((double X, double Y, double Z) camera, Vec3d playerpos) =>
        Math.Abs(camera.X - playerpos.X) + Math.Abs(camera.Y - playerpos.Y) + Math.Abs(camera.Z - playerpos.Z) <
               1e-6;

    private static bool Matches(Batched batched)
    {
        var matches = Held.Count == batched.Count;
        for (var i = 0; i < Math.Min(Held.Count, MaxPools) && matches; i++)
        {
            matches = ReferenceEquals(Held[i], Draws[batched.First + i].Vao);
        }

        if (!matches)
        {
            UnbatchedCalls++;
            return false;
        }

        Held.Clear();
        BatchedCalls++;
        return true;
    }

    // After the surfaces' pass 1, under this frame's pyramid: the later calls' pass 0 and their sort
    private static void CullLater(int counters)
    {
        if (_laterCount == 0 || _batchCuller is not { } culler || _camera is not { } camera) return;

        Frustum(culler, camera, (0, false));
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, Test, StatUints, 0), (_laterFirst, _laterCount), _laterMaxRows);
        _laterSorted = _laterMaxRows > 0 && Sorted((_laterFirst, _laterCount));
        _laterReady = true;
    }

    private readonly record struct Batched(MeshDataPoolManager Manager, int First, int Count, bool Surface);
}

// A shadow stage's rows calls, likewise one dispatch at its first call instead of one break each (RenderShadow draws four
// passes per atlas). Shadow passes test no occlusion: nothing else changes between the calls.
internal static partial class OcclusionCulling
{
    private static readonly int[] ShadowPasses = [Opaque, Topsoil, 2, 1]; // RenderShadow's
    private static readonly Batched[] ShadowBatch = new Batched[MaxBatched];
    private static int _shadowBatched, _shadowBatchCall = -1;
    private static bool _shadowTried;
    private static FrustumCulling? _shadowBatchCuller;
    private static (double X, double Y, double Z)? _shadowBatchCamera;
    private static ChunkRenderer? _shadowRenderer;

    private static void ClearShadowBatch()
    {
        Array.Clear(ShadowBatch);
        BeginShadowBatch(null);
    }

    // RenderShadow draws one stage
    private static void BeginShadowBatch(ChunkRenderer? renderer)
    {
        (_shadowBatched, _shadowBatchCall, _shadowTried, _shadowRenderer) = (0, -1, false, renderer);
        (_shadowBatchCuller, _shadowBatchCamera) = (null, null);
    }

    // At a stage's first held shadow call: its calls laid out and dispatched at once
    private static void PrepareShadow(FrustumCulling? culler, Vec3d camera, int mode)
    {
        if (_shadowTried) return;

        _shadowTried = true;
        if (_shadowRenderer is null || PassPools(_shadowRenderer) is not { } passes || culler is null) return;

        var (cursor, draws, most) = (_shadowRanges, _shadowDraws, 0);
        foreach (var pass in ShadowPasses.Bounded(MaxPasses))
        {
            if (!Index(pass, Math.Min(passes.Length, MaxPasses)) || passes[pass] is not { } atlases) continue;

            foreach (var manager in atlases.Bounded(MaxAtlases))
            {
                if (manager is not null && _shadowBatched < MaxBatched &&
                    LayCall(manager, (culler, camera), (1, 2 * MaxDraws), ref cursor, ref draws) is { } laid)
                {
                    ShadowBatch[_shadowBatched++] = new Batched(manager, laid.First, laid.Count, false);
                    most = Math.Max(most, _callMaxRows);
                }
            }
        }

        if (draws == _shadowDraws) return;

        _shadowMode = mode;
        CullShadow((_shadowRanges, cursor - _shadowRanges), (_shadowDraws, draws - _shadowDraws),
            (culler, camera, most),
            false);
        (_shadowBatchCuller, _shadowBatchCamera) = (culler, (camera.X, camera.Y, camera.Z));
    }

    private static int ShadowBatching(MeshDataPoolManager manager, Vec3d playerpos)
    {
        if (_shadowBatchCuller is null || !ReferenceEquals(Culler(manager), _shadowBatchCuller) ||
            _shadowBatchCamera is not { } camera || !Same(camera, playerpos))
        {
            return -1;
        }

        for (var b = 0; b < Math.Min(_shadowBatched, MaxBatched); b++)
        {
            if (ReferenceEquals(ShadowBatch[b].Manager, manager)) return b;
        }

        return -1;
    }

    private static bool IssuingShadowBatched(int entry)
    {
        var batched = ShadowBatch[entry];
        if (!Matches(batched)) return false;

        Issue((batched.First, batched.Count), (ShadowBase, StatUints + MaxDraws), CounterBuffers[_frame % Ring]);
        return true;
    }
}

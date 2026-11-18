using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Shadow passes cull from the same rows, without occlusion, while Vulkan draws them. Draws (MaxDraws on), commands (ShadowBase on), counts (after pass 1) have
// their own place so camera passes never overwrite them. Casters culled by the camera view (ShadowView) when made.
internal static partial class OcclusionCulling
{
    private static bool _shadowing, _shadowCall, _casting;
    private static int _shadowMode, _shadowRanges, _shadowDraws = MaxDraws, _shadowFrame = -1;
    private static Vec3d? _shadowCamera;

    // Under Vulkan: whether it takes GL's draws now; counted draws have no GL fallback, so only then is a call held
    internal static Func<bool>? Takes { get; set; }

    private static void ShadowOpening(ChunkRenderer __instance)
    {
        _ = Assert(!_holding);
        BeginShadowBatch(__instance);
        (_holding, Bypassing, _shadowCall) = (false, false, false); // as Opening: a call that threw left them set
        _shadowing = Active && RowsEnabled && VulkanDraws && Ready() && _rowsReady;
        _casting = _shadowing && ShadowView.Casting;
    }

    private static void ShadowClosing()
    {
        _ = Assert(!_holding);
        (_shadowing, _shadowCall, _casting, _shadowBatchCall) = (false, false, false, -1);
    }

    private static void HoldingShadow(MeshDataPoolManager manager, Vec3d playerpos, string origin,
        EnumFrustumCullMode mode)
    {
        var pass = mode switch
        {
            EnumFrustumCullMode.CullInstantShadowPassNear => 1,
            EnumFrustumCullMode.CullInstantShadowPassFar => 2,
            _ => 0
        };
        if (pass == 0 || !NotNull(manager) || !NotNull(playerpos) || Pools(manager) is not { } pools) return;

        if (VulkanDraws && Takes?.Invoke() != true)
        {
            return; // e.g. DistantShadows' own map: OpenGL's, culled by the CPU
        }

        if (_shadowFrame != _frame) (_shadowRanges, _shadowDraws, _shadowFrame) = (0, MaxDraws, _frame);

        PrepareShadow(Culler(manager), playerpos, pass);
        if (ShadowBatching(manager, playerpos) is var entry and >= 0)
        {
            _ = Scan(pools); // Mini, for Hold
            (_holding, Bypassing, _shadowCall, _shadowBatchCall, _origin) = (true, true, true, entry, origin);
            (_shadowCamera, _shadowMode) = (playerpos, pass); // for culling it on its own after all
            Held.Clear();
            return;
        }

        var locations = Scan(pools);
        if (_shadowRanges + locations > MaxRanges || _shadowDraws + pools.Count > 2 * MaxDraws ||
            !Rowed(Culler(manager), pools, true))
        {
            return;
        }

        (_holding, Bypassing, _shadowCall, _shadowCamera, _shadowMode, _origin) =
            (true, true, true, playerpos, pass, origin);
        Held.Clear();
    }

    private static void IssuingShadow(FrustumCulling? culler, List<MeshDataPool> pools)
    {
        if (culler is null || _shadowCamera is not { } camera) return;

        var (first, cursor) = (_shadowRanges, _shadowRanges);
        var draws = Lay(pools, camera, ref cursor, (_shadowDraws, 2 * MaxDraws), 1);
        if (draws > _shadowDraws)
        {
            CullShadow((first, cursor - first), (_shadowDraws, draws - _shadowDraws), (culler, camera, _callMaxRows),
                true);
        }
    }

    // issue: the draws follow now (one call), else from the stage's batch call by call
    private static void CullShadow((int First, int Count) ranges, (int First, int Count) draws,
        (FrustumCulling Culler, Vec3d Camera, int Rows) by, bool issue)
    {
        var (culler, camera) = (by.Culler, by.Camera);
        if (!Assert(draws.First >= MaxDraws && draws.First + draws.Count <= 2 * MaxDraws) ||
            !Assert(ranges.First + ranges.Count <= MaxRanges))
        {
            return;
        }

        var counters = CounterBuffers[_frame % Ring];
        UploadRows(draws, culler, camera, (_shadowMode, false));
        Prime(draws, counters, false);
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, Keep, StatUints + MaxDraws, ShadowBase), draws, by.Rows);
        if (issue) Issue(draws, (ShadowBase, StatUints + MaxDraws), counters);

        (_shadowRanges, _shadowDraws) = (ranges.First + ranges.Count, draws.First + draws.Count);
        Gpu.Texture(DepthPyramid.Unit, 0);
    }

    // One shadow call as its own frame (GPU tests), casters culled by the view when given
    internal static bool RunShadow(List<MeshDataPool> pools, FrustumCulling culler, Vec3d camera,
        EnumFrustumCullMode mode,
        FrustumCulling? view = null)
    {
        if (!NotNull(pools) || !NotNull(culler) || !Ready() || !_rowsReady) return false;

        BeginFrame(true);
        _shadowing = RowsEnabled;
        ShadowView.Use(view);
        _casting = view is not null;
        var manager = (MeshDataPoolManager)RuntimeHelpers.GetUninitializedObject(
            typeof(MeshDataPoolManager));
        (Pools(manager), Culler(manager)) = (pools, culler);
        HoldingShadow(manager, camera, "origin", mode);
        var held = _holding;
        foreach (var pool in pools.Bounded(MaxPools))
        {
            if (held && ModelRef(pool) is { } model) Held.Add(model);
        }

        if (held) Issuing(manager);

        ShadowClosing();
        ShadowView.Use(null);
        EndFrame();
        return held;
    }
}

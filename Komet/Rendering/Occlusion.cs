using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Occlusion culling of the terrain as a measurement that changes no frame: the ranges RenderOpaque draws are tested against the
// previous frame's depth pyramid (what culling before drawing could take away) and this frame's (the most any could). Off, it
// costs nothing: the patches go in at the first switch-on and return at once while off.
internal static partial class Occlusion
{
    public const int MaxRanges = 1 << 17;
    private const int MaxLocations = 65536, MaxPools = 4096;
    private const double Order = 0.371; // right after the terrain's opaque renderer (0.37), before entities
    private const float Sqrt3 = FrustumSweep.Sqrt3;

    internal static readonly Range[] Gathered = new Range[MaxRanges];
    private static Harmony? _harmony;
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;
    private static Hook? _hook;
    private static int _gathered;
    private static bool _opaque, _camera; // inside RenderOpaque; the frame's camera is taken
    private static double _x, _y, _z;

    // Main thread (HUD); the first true patches the engine, false leaves the patches inert
    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            if (value) Patch();
        }
    }

    public static bool Patched { get; private set; }

    // Last frame's draw ranges no pool location matched (pool order not as assumed)
    public static long Unmatched { get; private set; }

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Clear();
        (_harmony, _api, _logger, Patched) = (null, null, null, false);
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(logger)) return;
        (_harmony, _api, _logger) = (harmony, api, logger);
    }

    // The world closes; the GPU objects go with it
    public static void Clear()
    {
        Enabled = false;
        if (_hook is not null && _api?.Event is { } events) events.UnregisterRenderer(_hook, EnumRenderStage.Opaque);
        _hook = null;
        (_gathered, _opaque, _camera, Unmatched) = (0, false, false, 0);
        Release();
    }

    private static void Patch()
    {
        if (Patched || _harmony is null || _api is null || !NotNull(_logger)) return;
        Patched = true;
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque),
            [typeof(float)]);
        // the manager, not MeshDataPool.RenderMesh: that one gets inlined before a late patch
        var draw = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var self = typeof(Occlusion);
        try
        {
            if (!NotNull(opaque) || !NotNull(draw) ||
                !Assert(AccessTools.Field(typeof(MeshDataPool), "poolLocations") != null) ||
                !Assert(AccessTools.Field(typeof(MeshDataPoolManager), "pools") != null)) return;
            _ = NotNull(_harmony.Patch(opaque, new HarmonyMethod(AccessTools.Method(self, nameof(Opening))),
                new HarmonyMethod(AccessTools.Method(self, nameof(Closing)))));
            _ = NotNull(_harmony.Patch(draw, postfix: new HarmonyMethod(AccessTools.Method(self, nameof(Gather)))));
            _hook = new Hook();
            _api.Event.RegisterRenderer(_hook, EnumRenderStage.Opaque, "komet-occlusion");
        }
        catch (Exception e) when (e is HarmonyException or ArgumentException or InvalidOperationException
                                      or NotSupportedException)
        {
            _logger.Warning("Komet: the occlusion measurement could not patch the engine ({0})", e.Message);
        }
    }

    // The pools drawn until the postfix are the opaque terrain
    private static void Opening()
    {
        if (!Enabled || !Assert(!_opaque) || !NotNull(_api)) return;
        (_opaque, _camera, _gathered) = (true, false, 0);
        BeginStatistics();
    }

    private static void Closing()
    {
        if (!_opaque) return;
        _opaque = false;
        EndStatistics();
        _ = Assert(_gathered <= MaxRanges);
    }

    // Each pool's draw list is still the ranges the frustum left. Mini dimensions draw whole pools under their own transform and
    // stay out.
    private static void Gather(MeshDataPoolManager __instance)
    {
        if (!_opaque || !NotNull(__instance)) return;
        if (!_camera && _api?.World?.Player?.Entity?.CameraPos is { } camera)
            (_x, _y, _z, _camera) =
                (camera.X, camera.Y, camera.Z, _api.Render is { } render && Capture(render, ViewProjection));
        var pools = Pools(__instance);
        if (!_camera || !NotNull(pools)) return;
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) == 1 || pool.indicesGroupsCount == 0) continue;
            var swept = FrustumSweep.Boxes(pool, Gathered.AsSpan(_gathered), (_x, _y, _z));
            var unmatched = 0;
            _gathered += swept >= 0
                ? swept
                : Match(pool.indicesStartsByte, pool.indicesSizes, pool.indicesGroupsCount, Locations(pool),
                    Gathered.AsSpan(_gathered), (_x, _y, _z), out unmatched);
            Unmatched += unmatched;
        }

        _ = Assert(_gathered <= MaxRanges);
    }

    // FrustumCull emits visible locations in list order, which is index order, so one walk pairs each range with its location
    internal static int Match(int[] starts, int[] sizes, int groups, List<ModelDataPoolLocation> locations,
        Span<Range> into, (double X, double Y, double Z) camera, out int unmatched)
    {
        unmatched = 0;
        if (!NotNull(starts) || !NotNull(sizes) || !NotNull(locations) || !Assert(groups >= 0)) return 0;
        var (written, next) = (0, 0);
        var count = Math.Min(Math.Min(groups, sizes.Length), starts.Length / 2);
        for (var i = 0; i < Math.Min(count, MaxLocations) && written < into.Length; i++)
        {
            var start = starts[2 * i] / 4;
            next = Seek(locations, next, start);
            if (next >= locations.Count || locations[next].IndicesStart != start)
            {
                unmatched++;
                continue;
            }

            into[written++] = Box(locations[next].FrustumCullSphere, sizes[i], start, camera);
            next++;
        }

        return written;
    }

    private static int Seek(List<ModelDataPoolLocation> locations, int from, int start)
    {
        if (!Assert(from >= 0) || !Assert(start >= 0)) return locations.Count;
        for (var j = from; j < Math.Min(locations.Count, MaxLocations); j++)
            if (locations[j].IndicesStart >= start)
                return j;
        return locations.Count;
    }

    // In doubles until the difference
    internal static Range Box(Sphere sphere, int count, int first, (double X, double Y, double Z) camera)
    {
        _ = Assert(sphere.radius >= 0) && Assert(sphere.radiusY >= 0);
        return Box((sphere.x, sphere.y, sphere.z), (sphere.radius / Sqrt3, sphere.radiusY / Sqrt3, sphere.radiusZ / Sqrt3),
            count, first, camera);
    }

    internal static Range Box((float X, float Y, float Z) centre, (float X, float Y, float Z) half, int count, int first,
        (double X, double Y, double Z) camera)
    {
        var (cx, cy, cz) = ((float)(centre.X - camera.X), (float)(centre.Y - camera.Y), (float)(centre.Z - camera.Z));
        _ = Assert(count >= 0) && Assert(half.X >= 0 && half.Y >= 0 && half.Z >= 0);
        return new Range(cx - half.X, cy - half.Y, cz - half.Z, count, cx + half.X, cy + half.Y, cz + half.Z, first);
    }

    // std430 as occlusion.comp declares it: a vec3 and a uint fill 16 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Range(
        float LowX, float LowY, float LowZ, int Indices, float HighX, float HighY, float HighZ, int First);

    private sealed class Hook : IRenderer
    {
        public double RenderOrder => Order;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Enabled && Assert(stage == EnumRenderStage.Opaque)) Frame();
        }

        public void Dispose() => _ = Assert(ReferenceEquals(_hook, this) || _hook is null);
    }
}

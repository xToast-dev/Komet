using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// The tessellation's hot engine methods optimized from their first call. Left to tiering they ran quick-jitted for the first ~12 s of
// a world: the runtime holds tier-up back while new methods keep being jitted, a world's start jits over ten thousand, and the
// per-vertex helpers stay calls instead of inlined code - every pass 10-20 times slower than a minute later, the backlog of a world
// join 22 000 chunks deep (trace and bench, 2026-10-07). A method Harmony patches is rebuilt as a method of its own that the JIT
// compiles fully optimized at once, small callees inlined; the transpiler here changes nothing in the IL. Only large methods, which
// tiering never inlines anyway; one another patch has rebuilt already is left alone.
internal static class JitWarm
{
    private const int MaxMethods = 32;

    private static readonly (Type Type, string Name)[] Hot =
    [
        (typeof(CubeTesselator), nameof(CubeTesselator.Tesselate)),
        (typeof(CubeTesselator), nameof(CubeTesselator.DrawBlockFace)),
        (typeof(BleedingCubeTesselator), nameof(BleedingCubeTesselator.Tesselate)),
        (typeof(TopsoilTesselator), nameof(TopsoilTesselator.Tesselate)),
        (typeof(TopsoilTesselator), "DrawBlockFaceTopSoil"),
        (typeof(SurfaceLayerTesselator), nameof(SurfaceLayerTesselator.Tesselate)),
        (typeof(SurfaceLayerTesselator), nameof(SurfaceLayerTesselator.DrawBlockFace)),
        (typeof(LiquidTesselator), nameof(LiquidTesselator.Tesselate)),
        (typeof(ChunkTesselator), nameof(ChunkTesselator.BuildBlockPolygons)),
        (typeof(ChunkTesselator), nameof(ChunkTesselator.CalculateVisibleFaces_Fluids)),
        (typeof(ShapeTesselator), "TesselateShapeElements")
    ];

    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony, ILogger? logger)
    {
        var warmed = 0;
        var same = AccessTools.Method(typeof(JitWarm), nameof(Same));
        if (!Enabled || !NotNull(harmony) || !NotNull(same)) return;
        foreach (var method in Methods().Bounded(MaxMethods))
            if (Harmony.GetPatchInfo(method) is null && NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(same))))
                warmed++;
        if (warmed == 0) logger?.Warning("Komet JitWarm: none of the engine's tessellation methods was found");
        _ = Assert(warmed <= MaxMethods);
    }

    // The 7-argument AddJsonModelDataToMesh, not its small 5-argument wrapper, which stays inlinable
    internal static List<MethodInfo> Methods()
    {
        var methods = new List<MethodInfo>();
        foreach (var (type, name) in Hot.Bounded(MaxMethods))
            foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(256))
                if (method.Name == name && !method.IsAbstract && method.GetMethodBody() is not null)
                    methods.Add(method);
        var json = AccessTools.GetDeclaredMethods(typeof(JsonTesselator))
            .FirstOrDefault(static m => m.Name == nameof(JsonTesselator.AddJsonModelDataToMesh) && m.GetParameters().Length == 7);
        if (json is not null) methods.Add(json);
        return Assert(methods.Count <= MaxMethods) ? methods : [];
    }

    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions) =>
        NotNull(instructions) ? instructions : [];
}

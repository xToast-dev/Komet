using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace Komet.Shapes;

// A berry bush (BEBehaviorFruitingBushMesh, survival mod) caches its mesh by block, health, berry stage and texture variant, and
// every miss parses the bush's shape file again (Assets.Get<Shape>: the whole JSON into a new Shape) only to tesselate it once. A
// world full of bushes has hundreds of such keys over a dozen shape files: 450 MB of parsing garbage in the first minute of a join,
// on the tesselation threads. The mesh builder reads the shape and never writes it (ShapeTesselator.TesselateShape), so each file is
// parsed once per world and the same Shape handed to every miss. The rewrite replaces that one Get<Shape> call in the builder.
internal static class BushShapes
{
    private const int MaxInstructions = 1024, MaxNested = 64, MaxShapes = 4096;
    private const string Behavior = "Vintagestory.GameContent.BEBehaviorFruitingBushMesh";

    private static readonly ConcurrentDictionary<AssetLocation, Lazy<Shape?>> Parsed = new();

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        if (!NotNull(harmony) || Builder() is not { } builder) return;
        _ = NotNull(harmony.Patch(builder, transpiler: new HarmonyMethod(Rewrite)));
    }

    public static void Clear() => Parsed.Clear(); // a new world may bring other assets

    // The closure ensureMeshExists hands ObjectCacheUtil.GetOrCreate
    internal static MethodInfo? Builder()
    {
        var type = AccessTools.TypeByName(Behavior);
        if (type is null) return null;
        foreach (var nested in AccessTools.InnerTypes(type).Bounded(MaxNested))
            foreach (var method in AccessTools.GetDeclaredMethods(nested).Bounded(MaxNested))
                if (method.Name.StartsWith("<ensureMeshExists>", StringComparison.Ordinal) && method.ReturnType == typeof(MeshData))
                    return method;
        return null;
    }

    // Exactly one IAssetManager.Get<Shape>(AssetLocation) in the builder, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var get = AccessTools.Method(typeof(BushShapes), nameof(Get));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(get)) return code;
        var at = Il.Single(code, c => c.operand is MethodInfo { Name: "Get", IsGenericMethod: true } m &&
                                      m.DeclaringType == typeof(IAssetManager) && m.GetGenericArguments()[0] == typeof(Shape));
        Rewritten = Assert(at >= 0) && Il.Substitute(code, at, get);
        return code;
    }

    internal static Shape? Get(IAssetManager assets, AssetLocation location)
    {
        if (!Enabled || !NotNull(assets) || location is null) return assets.Get<Shape>(location);
        if (Parsed.TryGetValue(location, out var known)) return known.Value;
        var key = location.Clone(); // the builder's location is its own, but the cache keeps this one
        _ = Assert(Parsed.Count < MaxShapes);
        return Parsed.GetOrAdd(key, new Lazy<Shape?>(() => assets.Get<Shape>(key), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }
}

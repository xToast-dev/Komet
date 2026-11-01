using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.World;

// The cloud thread builds a Vec3d per tile every 40 ms (~34k tiles, 33 MB/s); one per thread serves (IL-checked). Both
// cloud renderers have the same tile update: the fluffy clouds' map and the classic CloudRenderer of simple clouds.
internal static class CloudTileScratch
{
    private const string Renderer = "FluffyClouds.CloudRendererMap", Classic = "Vintagestory.GameContent.CloudRenderer";
    private const string Method = "UpdateCloudTilesOffThread";
    private const string Reader = "Vintagestory.GameContent.WeatherDataReaderBase";
    private const string PreLoad = "Vintagestory.GameContent.WeatherDataReaderPreLoad";
    private const int MaxInstructions = 4096, FluffyBit = 1, ClassicBit = 2;

    private static readonly (string Type, int Bit)[] Renderers = [(Renderer, FluffyBit), (Classic, ClassicBit)];
    [ThreadStatic] private static Vec3d? _scratch;
    private static int _found, _rewritten; // per renderer: its method found, its rewrite taken

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _found != 0 && _rewritten == _found;

    public static void Install(Harmony harmony)
    {
        (_found, _rewritten) = (0, 0);
        if (!NotNull(harmony)) return;
        foreach (var (type, bit) in Renderers.Bounded(2))
        {
            if (Target(type) is not { } method) continue;
            _found |= bit;
            _ = harmony.Patch(method, transpiler: new HarmonyMethod(Substitute));
        }
    }

    // Both renderers' tile updates, which the transpiler rewrites
    internal static MethodBase?[] Replaced => [Target(Renderer), Target(Classic)];

    // VSEssentials is not referenced: found by name once loaded
    internal static MethodInfo? Target(string type = Renderer)
    {
        var renderer = AccessTools.TypeByName(type);
        if (!NotNull(renderer)) return null;
        var method = AccessTools.Method(renderer, Method, [typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    internal static ConstructorInfo? Constructor() =>
        AccessTools.Constructor(typeof(Vec3d), [typeof(double), typeof(double), typeof(double)]);

    // The methods the vector goes to, with its argument position: each reads only X, Y and Z
    private static MethodInfo?[] Readers()
    {
        var (reader, preload) = (AccessTools.TypeByName(Reader), AccessTools.TypeByName(PreLoad));
        if (!NotNull(reader) || !NotNull(preload)) return [];
        return
        [
            AccessTools.Method(reader, "LoadAdjacentSims", [typeof(Vec3d)]),
            AccessTools.Method(reader, "LoadLerp",
                [typeof(Vec3d), typeof(bool), typeof(float), typeof(float), typeof(float)]),
            AccessTools.Method(preload, "LoadLerp", [typeof(Vec3d)])
        ];
    }

    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var bit = original?.DeclaringType?.FullName == Classic ? ClassicBit : FluffyBit;
        _rewritten &= ~bit; // Harmony reruns a transpiler whenever another mod patches the method
        var (ctor, mine, readers) =
            (Constructor(), AccessTools.Method(typeof(CloudTileScratch), nameof(At)), Readers());
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor));
        if (!Assert(at >= 0) || !Index(at + 1, code.Count) || !Assert(code[at + 1].IsStloc())) return code;
        if (!Assert(Il.Confined(code, at + 1, (c, operand) => operand == 0
                ? c.opcode == OpCodes.Ldfld && c.operand is FieldInfo field && field.DeclaringType == typeof(Vec3d)
                : operand == 1 && c.operand is MethodInfo method && Array.IndexOf(readers, method) >= 0)))
            return code;
        if (Assert(Il.Substitute(code, at, mine))) _rewritten |= bit;
        return code;
    }

    // Stands in for new Vec3d(x, y, z): the caller reads the vector before the next tile asks for it again
    internal static Vec3d At(double x, double y, double z)
    {
        if (!Enabled) return new Vec3d(x, y, z);
        return (_scratch ??= new Vec3d()).Set(x, y, z);
    }
}

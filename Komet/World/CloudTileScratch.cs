using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.World;

// The cloud thread refreshes every cloud tile each 40 ms and builds a fresh Vec3d for each tile's world position: with ~34k tiles
// that is a steady 33 MB/s of garbage, half of what the client still allocates once the world has finished loading. The vector
// never leaves the loop body, so one vector per thread serves every tile: its one local is only read for its fields or handed to the
// weather readers (Readers), which only read X, Y and Z and keep nothing. Checked on the IL.
// The method also runs once on the main thread for the first cloud tick, which is why the scratch is per thread and not per map.
internal static class CloudTileScratch
{
    private const string Renderer = "FluffyClouds.CloudRendererMap",
        Method = "UpdateCloudTilesOffThread",
        Reader = "Vintagestory.GameContent.WeatherDataReaderBase",
        PreLoad = "Vintagestory.GameContent.WeatherDataReaderPreLoad";

    private const int MaxInstructions = 4096;

    [ThreadStatic] private static Vec3d? _scratch;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CloudTileScratch), nameof(Substitute))));
    }

    // VSEssentials is a mod assembly Komet does not reference, so the renderer is found by name once the game has loaded it
    internal static MethodInfo? Target()
    {
        var renderer = AccessTools.TypeByName(Renderer);
        if (!NotNull(renderer)) return null;
        var method = AccessTools.Method(renderer, Method, [typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    internal static ConstructorInfo? Constructor()
    {
        var ctor = AccessTools.Constructor(typeof(Vec3d), [typeof(double), typeof(double), typeof(double)]);
        return NotNull(ctor) && Assert(ctor.DeclaringType == typeof(Vec3d)) ? ctor : null;
    }

    // The methods the loop hands its vector to, as their position: each reads only its X, Y and Z and keeps no reference to it
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

    // Exactly one new Vec3d(x, y, z), stored to a local that is only read for Vec3d's fields or handed to Readers as their position.
    // Anything else means the method changed shape, and the engine's own IL is handed back.
    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
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
        Rewritten = Assert(Il.Substitute(code, at, mine));
        return code;
    }

    // Stands in for new Vec3d(x, y, z): the caller reads the vector before the next tile asks for it again
    internal static Vec3d At(double x, double y, double z)
    {
        if (!Enabled || !Finite(x) || !Finite(z)) return new Vec3d(x, y, z);
        var scratch = _scratch ??= new Vec3d();
        return scratch.Set(x, y, z);
    }
}

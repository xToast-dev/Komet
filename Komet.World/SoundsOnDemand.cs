using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// A join decodes every sound of the game and its mods to PCM (ScreenManager.LoadSoundsSlow), kept for the session:
// ~220 MB of byte arrays in a heap dump, most of them never played. The engine's own "optimize RAM" mode 2 stops after
// the long loops it decodes first (weather, environment, temporal storms, rifts) and decodes any other sound when it is
// first played, on the thread that creates its LoadedSoundNative. Here LoadSoundsSlow reads that mode as at least 2;
// nothing else of mode 2 changes, chunk lifetimes and music unloading stay as the player set them.
internal static class SoundsOnDemand
{
    private const int MaxInstructions = 1024, OnDemand = 2;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    // 1.23 renamed the slow join load; both read OptimizeRamMode once
    private static MethodInfo? Target() =>
        AccessTools.DeclaredMethod(typeof(ScreenManager), "LoadSoundsSlow", [typeof(ClientMain)]) ??
        AccessTools.DeclaredMethod(typeof(ScreenManager), "LoadSoundsSlowAsync", [typeof(ClientMain)]);

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        if (Target() is not { } load) return;

        _ = harmony.Patch(load,
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(SoundsOnDemand), nameof(Rewrite))));
    }

    // The one ClientSettings.OptimizeRamMode read becomes Mode(); any other count keeps the IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var mode = AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.OptimizeRamMode));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(mode)) return code;

        Rewritten = Il.SubstituteSingle(code, c => c.Calls(mode),
            AccessTools.Method(typeof(SoundsOnDemand), nameof(Mode)));
        return code;
    }

    internal static int Mode()
    {
        var mode = ClientSettings.OptimizeRamMode;
        return Enabled ? Math.Max(mode, OnDemand) : mode;
    }
}

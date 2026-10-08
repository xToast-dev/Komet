namespace Komet.Testing;

public sealed class TestHarmony(string id) : Harmony(id), IDisposable
{
    public void Dispose() => UnpatchAll(Id);
}

// Another mod's patches whose only effect is being there: code that stands down for foreign patches sees them, and patching a caller
// makes Harmony rebuild it.
public static class Foreign
{
    public static HarmonyMethod Prefix => new(typeof(Foreign), nameof(RunOriginal));

    // Does nothing; also a prefix that lets the original run
    public static HarmonyMethod Postfix => new(typeof(Foreign), nameof(Nothing));

    public static HarmonyMethod Transpiler => new(typeof(Foreign), nameof(Same));

    private static bool RunOriginal() => true;

    private static void Nothing()
    {
        // only its presence matters
    }

    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions) => instructions;
}

namespace Komet.Testing;

// A test's Harmony id, unpatched when the test's `using` scope ends
public sealed class TestHarmony(string id) : Harmony(id), IDisposable
{
    public void Dispose()
    {
        UnpatchAll(Id);
    }
}

// Another mod's patches whose only effect is being there: code that stands down for foreign patches sees them, and patching a caller
// makes Harmony rebuild it. A fresh HarmonyMethod per use.
public static class Foreign
{
    // Lets the original run
    public static HarmonyMethod Prefix => new(typeof(Foreign), nameof(RunOriginal));

    // Does nothing; also a prefix that lets the original run
    public static HarmonyMethod Postfix => new(typeof(Foreign), nameof(Nothing));

    // Changes nothing
    public static HarmonyMethod Transpiler => new(typeof(Foreign), nameof(Same));

    private static bool RunOriginal()
    {
        return true;
    }

    private static void Nothing()
    {
        // only its presence matters
    }

    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions)
    {
        return instructions;
    }
}

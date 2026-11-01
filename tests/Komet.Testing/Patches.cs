namespace Komet.Testing;

public sealed class TestHarmony(string id) : Harmony(id), IDisposable
{
    public void Dispose() => UnpatchAll(Id);

    // Whether this instance has a patch of the kind (any kind for All) on the method
    public bool Owns(MethodBase method, HarmonyPatchType kind = HarmonyPatchType.All) => Owns(method, Id, kind);

    // Whether the owner has a patch of the kind (any kind for All) on the method
    public static bool Owns(MethodBase method, string owner, HarmonyPatchType kind = HarmonyPatchType.All) =>
        Owners(method, kind).Contains(owner);

    // The owners of the method's patches of the kind, in Harmony's order; empty when it is not patched
    public static string[] Owners(MethodBase method, HarmonyPatchType kind = HarmonyPatchType.All)
    {
        if (GetPatchInfo(method) is not { } info) return [];
        var patches = kind switch
        {
            HarmonyPatchType.Prefix => info.Prefixes,
            HarmonyPatchType.Postfix => info.Postfixes,
            HarmonyPatchType.Transpiler => info.Transpilers,
            HarmonyPatchType.Finalizer => info.Finalizers,
            _ => [.. info.Prefixes, .. info.Postfixes, .. info.Transpilers, .. info.Finalizers]
        };
        return [.. patches.Select(p => p.owner).Distinct()];
    }

    // The owners of the method's prefixes
    public static string[] Prefixed(MethodBase method) => Owners(method, HarmonyPatchType.Prefix);
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

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace Komet.World;

// Garbage the integrated server's block ticks make for nothing kept (trace 2026-10-31, user world, 20 s, 254 MB): every
// grass block's growth check walks the 7x2x7 blocks around it and asks each one's attributes for "killPlantRadius", a new
// JsonObject per block asked (9 MB); a barrel holding what no recipe takes looks for its recipe every 3 s, and every
// recipe answers with a new empty list (3 MB). The soil's lookup keeps the child it made per attributes and key and
// hands it out again while the attributes hold the same token under the key (found by the engine's own lookup); a
// recipe's empty answer is one list kept empty, which its callers only count. Each rewrite takes its one shape; an
// engine with another keeps its body.
internal static class TickScratch
{
    private const string Soil = "Vintagestory.GameContent.BlockSoil", Recipe = "Vintagestory.GameContent.BarrelRecipe";
    private const int Slots = 4096, MaxInstructions = 2048;

    private static readonly Kept?[] Children = new Kept?[Slots];

    public static bool Enabled { get; set; } = true;
    private static readonly Rewrites Done = new();

    public static int Rewritten => Done.Count; // of the two
    public static bool Absent { get; private set; } // no survival mod: neither block exists

    public static void Install(Harmony harmony)
    {
        Done.Clear();
        var (soil, recipe) = (AccessTools.TypeByName(Soil), AccessTools.TypeByName(Recipe));
        Absent = soil is null && recipe is null;
        if (!NotNull(harmony)) return;
        if (Walk(soil) is { } walk) _ = harmony.Patch(walk, transpiler: new HarmonyMethod(RewriteSoil));
        if (recipe is not null && AccessTools.DeclaredMethod(recipe, "PairInput") is { } pair)
            _ = harmony.Patch(pair, transpiler: new HarmonyMethod(RewritePairs));
    }

    // ShouldReceiveServerGameTicks' WalkBlocks lambda, the one method it takes the address of in its own type
    private static MethodInfo? Walk(Type? soil)
    {
        if (soil is null || AccessTools.DeclaredMethod(soil, "ShouldReceiveServerGameTicks") is not { } check) return null;
        var code = PatchProcessor.GetOriginalInstructions(check);
        var at = Il.Single(code, c => c.opcode == OpCodes.Ldftn && c.operand is MethodInfo { DeclaringType.DeclaringType: var owner } &&
                                      owner == soil);
        return at >= 0 ? code[at].operand as MethodInfo : null;
    }

    // The lambda's one JsonObject[key] becomes Child
    internal static List<CodeInstruction> RewriteSoil(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var item = AccessTools.DeclaredMethod(typeof(JsonObject), "get_Item", [typeof(string)]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(item)) return code;
        Done.Mark(original, Il.SubstituteSingle(code, c => c.operand is MethodInfo m && m == item,
            AccessTools.Method(typeof(TickScratch), nameof(Child))));
        return code;
    }

    // Each `return new List<(ItemSlot, BarrelRecipeIngredient)>()` becomes Empty's kept list; the list it fills stays new
    internal static List<CodeInstruction> RewritePairs(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var empties = 0;
        for (var i = 0; i < Math.Min(code.Count - 1, MaxInstructions); i++)
        {
            if (code[i].opcode != OpCodes.Newobj || code[i + 1].opcode != OpCodes.Ret ||
                code[i].operand is not ConstructorInfo { DeclaringType: { IsGenericType: true } list } ctor ||
                list.GetGenericTypeDefinition() != typeof(List<>) || ctor.GetParameters().Length != 0) continue;
            var empty = AccessTools.Method(typeof(TickScratch), nameof(Empty)).MakeGenericMethod(list.GetGenericArguments()[0]);
            if (Il.Substitute(code, i, empty)) empties++;
        }

        Done.Mark(original, empties > 0);
        return code;
    }

    // attributes[key], the child kept while the attributes hold the same token under the key; a null attributes object
    // fails as the engine's indexer call did
    internal static JsonObject Child(JsonObject attributes, string key)
    {
        if (!Enabled || attributes?.Token is not JObject token || key is null) return attributes![key!];
        _ = token.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value);
        var slot = (uint)HashCode.Combine(RuntimeHelpers.GetHashCode(attributes), RuntimeHelpers.GetHashCode(key)) % Slots;
        if (Children[slot] is { } kept && ReferenceEquals(kept.Attributes, attributes) && ReferenceEquals(kept.Key, key) &&
            ReferenceEquals(kept.Value.Token, value)) return kept.Value;
        var child = new JsonObject(value);
        Children[slot] = new Kept(attributes, key, child);
        return child;
    }

    // An answer of no pairs: one kept list per element type, a new one kept should a caller have filled it
    internal static List<T> Empty<T>()
    {
        if (!Enabled) return [];
        var empty = EmptyOf<T>.List;
        if (empty.Count == 0) return empty;
        EmptyOf<T>.List = [];
        return EmptyOf<T>.List;
    }

    // A world gone: its blocks' attributes are not held on to
    public static void Clear() => Array.Clear(Children);

    private static class EmptyOf<T>
    {
        public static List<T> List { get; set; } = [];
    }

    private sealed class Kept(JsonObject attributes, string key, JsonObject child)
    {
        public JsonObject Attributes { get; } = attributes;
        public string Key { get; } = key;
        public JsonObject Value { get; } = child;
    }
}

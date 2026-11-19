using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Api;
using Komet.Core;

namespace Komet.World;

// A firepit's cooking pot tests every recipe twice a burn tick (CookingRecipe.Matches), allocating copies, counters and
// per GetMatchingStack a fresh IgnoredStackAttributes + "timeFrozen": 5.9 MB/s in the bench world, a tenth of all
// garbage.
// - Matches uses the thread's two lists and counter array, never lets them out and reads only the counters it zeroed. A
//   nested Matches (none exists) allocates as the engine; a prefix and finalizer count depth.
// - GetMatchingStack compares against one shared copy, rebuilt when the source changes; Equals only reads it
//   (EngineShape pins the body). Another mod's prefix there (A Culinary Artillery replaces it whole) leaves the rewrite
//   dead: it gives way until that prefix is gone, Matches stays rewritten.
internal static class CookingMatch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x473338848C356079UL;
    // EngineShape of Seams() in Vintage Story 1.23.0-pre.1: Matches indexes Ingredients and keeps a tuple per input
    internal const ulong Fingerprint123 = 0x64AEC42E39A1479FUL;

    private const string RecipeType = "Vintagestory.GameContent.CookingRecipe";
    private const string IngredientType = "Vintagestory.GameContent.CookingRecipeIngredient";
    private const string Frozen = "timeFrozen";
    private const int MaxInstructions = 1024, MaxCounts = 4096, Arrays = 2, MatchesBit = 1, StackBit = 2, AllBits = 3;

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static List<ItemStack>? _stacks;
    [ThreadStatic] private static object? _items; // the List<CookingRecipeIngredient> of this thread
    [ThreadStatic] private static int[]? _counts;
    [ThreadStatic] private static (int, float)[]? _tuples;
    private static Shared? _ignored;
    private static Harmony? _harmony;
    private static ILogger? _logger;
    private static int _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => (_rewritten | (Bypassed ? StackBit : 0)) == AllBits;

    // Another mod's prefix replaces GetMatchingStack: Komet's rewrite of it is unpatched
    public static bool Bypassed { get; private set; }
    internal static bool Matched { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_rewritten, Matched, Bypassed, _harmony, _logger) = (0, false, false, null, logger);
        var seams = Seams();
        if (seams[0] is not MethodInfo matches ||
            seams[1] is not MethodInfo matching)
        {
            return;
        }

        // Two engine bodies are known: the released list-copying one and 1.23's indexed one with a tuple per input.
        // The IL decides the path; an explicit 1.23 fingerprint narrows it, so a caller can pin that body.
        var detected = Shape(matches);
        var shape = detected == 0 ? 1 : detected;
        if (fingerprint == Fingerprint123) shape = 2;
        if (detected != 0 && detected != shape) return;

        Matched = EngineShape.Matches(seams, shape == 2 ? Fingerprint123 : Fingerprint, nameof(CookingMatch), logger);
        if (!Matched) return;

        var rewrite = AccessTools.Method(typeof(CookingMatch),
            shape == 2 ? nameof(RewriteMatches123) : nameof(RewriteMatches));
        if (!NotNull(rewrite)) return;

        _ = harmony.Patch(matches, new HarmonyMethod(Enter), transpiler: new HarmonyMethod(rewrite),
            finalizer: new HarmonyMethod(Leave));
        _ = harmony.Patch(matching, transpiler: new HarmonyMethod(RewriteMatching));
        _harmony = harmony;
        Recheck();
    }

    // At install and when the level is final (mods patch at their own start): the GetMatchingStack rewrite gives way to
    // another mod's prefix and comes back once it is gone
    internal static void Recheck()
    {
        if (!Matched || _harmony is not { } harmony || Seams()[1] is not MethodInfo matching) return;

        var foreign = EngineShape.Foreign([matching], KapiPatchKinds.Prefix, harmony.Id);
        if (foreign == Bypassed) return;

        Bypassed = foreign;
        var rewrite = AccessTools.Method(typeof(CookingMatch), nameof(RewriteMatching));
        if (foreign)
        {
            harmony.Unpatch(matching, rewrite);
            _rewritten &= ~StackBit;
            _logger?.Notification(
                "Komet CookingMatch: another mod replaces GetMatchingStack, only Matches is rewritten");
        }
        else
        {
            _ = harmony.Patch(matching, transpiler: new HarmonyMethod(rewrite));
            _logger?.Notification("Komet CookingMatch: GetMatchingStack is no longer replaced, rewritten again");
        }
    }

    internal static MethodBase?[] Seams()
    {
        var (recipe, ingredient) = (AccessTools.TypeByName(RecipeType), AccessTools.TypeByName(IngredientType));
        MethodBase?[] seams =
        [
            recipe is null
                ? null
                : AccessTools.DeclaredMethod(recipe, "Matches", [typeof(ItemStack[]), typeof(int).MakeByRefType()]),
            ingredient is null ? null : AccessTools.DeclaredMethod(ingredient, "GetMatchingStack", [typeof(ItemStack)])
        ];
        return seams;
    }

    // 1.22.7 copies both lists (List<ItemStack> ctor); 1.23 has no copies but a newarr of (int,float) per input stack.
    // 0 for any other body: no guess is made about how to rewrite it.
    private static int Shape(MethodBase? matches)
    {
        if (matches is null || !Il.Take(PatchProcessor.GetOriginalInstructions(matches), MaxInstructions, out var code))
            return 0;

        var (copies, tuples) = (0, 0);
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var c = code[i];
            if (c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ctor &&
                ctor.DeclaringType == typeof(List<ItemStack>))
            {
                copies++;
            }

            if (c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof((int, float)))) tuples++;
        }

        if (copies == 1 && tuples == 0) return 1;
        return copies == 0 && tuples == 1 ? 2 : 0;
    }

    private static void Enter() => _depth++;

    // The main thread outlives the world the stacks and ingredients belong to
    private static void Leave()
    {
        if (--_depth == 0)
        {
            _stacks?.Clear();
            (_items as IList)?.Clear();
        }
    }

    private static List<CodeInstruction> RewriteMatches(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~MatchesBit;
        var ingredient = AccessTools.TypeByName(IngredientType);
        var (copy, toList) = (AccessTools.Constructor(typeof(List<ItemStack>), [typeof(IEnumerable<ItemStack>)]),
            ingredient is null
                ? null
                : AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList))?.MakeGenericMethod(ingredient));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ingredient) || !NotNull(copy) ||
            !NotNull(toList))
        {
            return code;
        }

        var (stacks, items) = (Il.Single(code, c => c.Is(OpCodes.Newobj, copy)), Il.Single(code, c => c.Calls(toList)));
        var counts = Il.Single(code, c => c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof(int)));
        if (stacks < 0 || items < 0 || counts < 0) return code;

        var item = AccessTools.Method(typeof(CookingMatch), nameof(Items)).MakeGenericMethod(ingredient);
        var (stack, count) = (AccessTools.Method(typeof(CookingMatch), nameof(Stacks)),
            AccessTools.Method(typeof(CookingMatch), nameof(Counts)));
        (code[stacks].opcode, code[stacks].operand) = (OpCodes.Call, stack);
        (code[items].opcode, code[items].operand) = (OpCodes.Call, item);
        (code[counts].opcode, code[counts].operand) = (OpCodes.Call, count);
        _rewritten |= MatchesBit;
        return code;
    }

    // 1.23 keeps Ingredients and inputStacks as arrays already: only the per-input tuple array and the counters are
    // allocated there. GetMatchingStack is unchanged and rewritten as before.
    private static List<CodeInstruction> RewriteMatches123(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~MatchesBit;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;

        var (tuples, count) = (AccessTools.Method(typeof(CookingMatch), nameof(Tuples)),
            AccessTools.Method(typeof(CookingMatch), nameof(Counts)));
        if (!NotNull(tuples) || !NotNull(count)) return code;

        var tupleAt = Il.Single(code,
            c => c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof((int, float))));
        var countAt = Il.Single(code, c => c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof(int)));
        if (tupleAt < 0 || countAt < 0 || !Il.Substitute(code, tupleAt, tuples) ||
            !Il.Substitute(code, countAt, count))
        {
            return code;
        }

        _rewritten |= MatchesBit;
        return code;
    }

    internal static List<ItemStack> Stacks(IEnumerable<ItemStack> stacks)
    {
        if (!Enabled || _depth != 1) return [.. stacks];

        var list = _stacks ??= [];
        list.Clear();
        list.AddRange(stacks);
        return list;
    }

    internal static List<T> Items<T>(IEnumerable<T> items)
    {
        if (!Enabled || _depth != 1) return [.. items];

        if (_items is not List<T> list) _items = list = [];

        list.Clear();
        list.AddRange(items);
        return list;
    }

    // Stands in for new int[count]: at least count long, zeroed by the method itself
    internal static int[] Counts(int count)
    {
        // a negative count throws as newarr does
        if (!Enabled || _depth != 1 || !Index(count, MaxCounts + 1)) return new int[count];

        if (_counts is not { } counts || counts.Length < count) _counts = counts = new int[Math.Max(count, 16)];

        return counts;
    }

    // Stands in for new (int,float)[count]: the engine writes every tuple it reads, but a reused array must not carry
    // entries over from a longer run, so the part in use is cleared as the allocation would have been.
    internal static (int, float)[] Tuples(int count)
    {
        // a negative count throws as newarr does
        if (!Enabled || _depth != 1 || !Index(count, MaxCounts + 1)) return new (int, float)[count];

        if (_tuples is not { } tuples || tuples.Length < count)
            _tuples = tuples = new (int, float)[Math.Max(count, 16)];

        tuples.AsSpan(0, count).Clear();
        return tuples;
    }

    // Both IgnoredStackAttributes + "timeFrozen" copies removed, Equals reads Ignored(); any other shape keeps the IL
    internal static List<CodeInstruction> RewriteMatching(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~StackBit;
        var source = AccessTools.Field(typeof(GlobalConstants), nameof(GlobalConstants.IgnoredStackAttributes));
        var equals = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
            [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(source) || !NotNull(equals))
        {
            return code;
        }

        var starts = Enumerable.Range(0, code.Count).Where(i => code[i].LoadsField(source)).ToArray();
        if (!Assert(starts.Length == Arrays)) return code;

        var ends = starts.Select(s => Copy(code, s, equals)).ToArray();
        if (ends.Any(e => e < 0)) return code;

        var shared = AccessTools.Method(typeof(CookingMatch), nameof(Ignored));
        for (var k = 0; k < Arrays; k++)
        {
            for (var i = starts[k]; i < Math.Min(ends[k] + 1, MaxInstructions); i++)
            {
                (code[i].opcode, code[i].operand) = (OpCodes.Nop, null);
            }

            (code[ends[k] + 1].opcode, code[ends[k] + 1].operand) = (OpCodes.Call, shared);
        }

        _rewritten |= StackBit;
        return code;
    }

    // The "timeFrozen" store ending a jump-free copy from `start`, with the copy loaded for Equals right after; else -1
    private static int Copy(List<CodeInstruction> code, int start, MethodInfo equals)
    {
        var (arrays, end) = (0, -1);
        for (var i = start; i < Math.Min(code.Count - 2, MaxInstructions); i++)
        {
            var c = code[i];
            if ((i > start && c.labels.Count > 0) || c.blocks.Count > 0 || c.Branches(out _)) return -1;

            if (c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof(string))) arrays++;

            if (!c.Is(OpCodes.Ldstr, Frozen)) continue;

            end = code[i + 1].opcode == OpCodes.Stelem_Ref ? i + 1 : -1;
            break;
        }

        return end >= 0 && Index(end + 2, code.Count) && arrays == 1 && code[end + 1].IsLdloc() &&
               code[end + 2].Calls(equals) && code[end + 1].labels.Count == 0
            ? end
            : -1;
    }

    // IgnoredStackAttributes + "timeFrozen", shared while the source array and its entries are unchanged
    internal static string[] Ignored()
    {
        var source = GlobalConstants.IgnoredStackAttributes; // null throws as the engine's ldlen does
        if (_ignored is { } shared && Same(shared, source)) return shared.Joined;

        var copy = new string[source.Length + 1];
        source.AsSpan().CopyTo(copy);
        copy[^1] = Frozen;
        _ignored = new Shared(source, copy, [.. source]);
        return copy;
    }

    private static bool Same(Shared shared, string[] source)
    {
        var (entries, copy) = (shared.Entries, shared.Joined);
        if (!ReferenceEquals(shared.Source, source) || source.Length != entries.Length ||
            copy.Length != entries.Length + 1 || !ReferenceEquals(copy[^1], Frozen))
        {
            return false;
        }

        for (var i = 0; i < Math.Min(entries.Length, MaxCounts); i++)
        {
            if (!ReferenceEquals(source[i], entries[i]) || !ReferenceEquals(copy[i], entries[i])) return false;
        }

        return true;
    }

    private sealed record Shared(string[] Source, string[] Joined, string[] Entries);
}

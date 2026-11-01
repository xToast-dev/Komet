using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;

namespace Komet.World;

// A firepit with a crucible asks every alloy recipe whether its contents match, every burn tick, and each
// AlloyRecipe.Matches made two lists and a stack copy per ingredient (mergeAndCompareStacks): 2.6 % of all allocations
// in flight over a base. The same answer here on the stack: the inputs merged by class and id (smelted where the engine
// would smelt them, rounded to two places as it sums), each new one matched to the first unused ingredient of its class
// and id, every ingredient used, then the engine's ratio test on the merged sizes. EngineShape pins the three bodies; another
// mod's patch on the two helpers, or its prefix, transpiler or infix on Matches, leaves the engine's answer.
internal static class AlloyMatch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x71E4B9BAA545439CUL;

    private const string Recipe = "Vintagestory.GameContent.AlloyRecipe";
    private const int MaxIngredients = 16, MaxInputs = 32;

    private static bool _shaped;
    private static readonly SeamGuard Guard = new(nameof(AlloyMatch), PatchKinds.Replacing, [typeof(AlloyMatch)]);

    // Compiled once at Install: the recipe's Ingredients and an ingredient's ratios, typed (survival's types by name)
    private static System.Func<object, Array?>? _ingredients;
    private static System.Func<object, float>? _min, _max;

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => Guard.Matched;
    internal static bool Ready => _shaped;
    internal static bool Blocked => Guard.Blocked;

    internal static Action Recheck { get; } = static () => Guard.RecheckCallees(_shaped);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        _shaped = false;
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || seams[0] is not MethodInfo matches) return;
        _shaped = Guard.Match(seams, fingerprint) && Accessors(matches.DeclaringType!);
        if (_shaped) _ = harmony.Patch(matches, new HarmonyMethod(AccessTools.Method(typeof(AlloyMatch), nameof(Matches))));
        Recheck();
    }

    private static bool Accessors(Type recipe)
    {
        var ingredient = AccessTools.TypeByName("Vintagestory.GameContent.MetalAlloyIngredient");
        if (ingredient is null || AccessTools.DeclaredField(recipe, "Ingredients") is not { } list) return false;
        var o = Expression.Parameter(typeof(object));
        _ingredients = Expression.Lambda<System.Func<object, Array?>>(
            Expression.Convert(Expression.Field(Expression.Convert(o, recipe), list), typeof(Array)), o).Compile();
        _min = Ratio(ingredient, "MinRatio", o);
        _max = Ratio(ingredient, "MaxRatio", o);
        return _min is not null && _max is not null;
    }

    private static System.Func<object, float>? Ratio(Type ingredient, string name, ParameterExpression o) =>
        AccessTools.DeclaredField(ingredient, name) is { FieldType: var type } field && type == typeof(float)
            ? Expression.Lambda<System.Func<object, float>>(Expression.Field(Expression.Convert(o, ingredient), field), o).Compile()
            : null;

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Recipe);
        MethodBase?[] seams = type is null
            ? [null, null, null]
            :
            [
                AccessTools.DeclaredMethod(type, "Matches", [typeof(ItemStack[]), typeof(bool)]),
                AccessTools.DeclaredMethod(type, "mergeAndCompareStacks"), AccessTools.DeclaredMethod(type, "getIgrendientFor")
            ];
        return seams;
    }

    // Prefix on AlloyRecipe.Matches(inputStacks, useSmeltedWhereApplicable); its Ingredients by name (survival's type)
    internal static bool Matches(object __instance, ItemStack[] inputStacks, bool useSmeltedWhereApplicable, ref bool __result,
        bool __runOriginal)
    {
        if (!__runOriginal) return false; // another prefix answered
        if (!Enabled || Blocked || inputStacks is null || inputStacks.Length > MaxInputs || __instance is null ||
            _ingredients?.Invoke(__instance) is not { Length: <= MaxIngredients } ingredients) return true;
        __result = Answer(inputStacks, useSmeltedWhereApplicable, ingredients);
        return false;
    }

    private static bool Answer(ItemStack[] inputs, bool smelted, Array ingredients)
    {
        Span<bool> used = stackalloc bool[MaxIngredients];
        Span<(EnumItemClass Class, int Id, double Size, int Ingredient)> merged = stackalloc (EnumItemClass, int, double, int)[MaxInputs];
        var (count, left) = (0, ingredients.Length);
        foreach (var input in inputs.Bounded(MaxInputs))
        {
            if (input is null) continue;
            var (stack, size) = Smelted(input, smelted);
            var at = Merged(merged[..count], stack);
            if (at >= 0)
            {
                merged[at].Size = Math.Round(merged[at].Size + size, 2);
                continue;
            }

            var ingredient = stack is null ? -1 : FirstUnused(ingredients, used, stack);
            if (ingredient < 0) return false; // getIgrendientFor found none: no list, no match
            (used[ingredient], left) = (true, left - 1);
            merged[count++] = (stack!.Class, stack.Id, size, ingredient);
        }

        return left == 0 && Ratios(merged[..count], ingredients);
    }

    // The stack the engine compares and its size: the smelted stack where its combustible properties name one
    private static (ItemStack? Stack, float Size) Smelted(ItemStack input, bool smelted)
    {
        var size = (float)input.StackSize;
        var properties = input.Collectible.GetCombustibleProperties(null, input, null);
        return !smelted || properties?.SmeltedStack is not { } into ? (input, size) : (into.ResolvedItemstack, size / properties.SmeltedRatio);
    }

    private static int Merged(ReadOnlySpan<(EnumItemClass Class, int Id, double Size, int Ingredient)> merged, ItemStack? stack)
    {
        if (stack is null) return -1;
        for (var i = 0; i < Math.Min(merged.Length, MaxInputs); i++)
            if (merged[i].Class == stack.Class && merged[i].Id == stack.Id)
                return i;
        return -1;
    }

    private static int FirstUnused(Array ingredients, Span<bool> used, ItemStack stack)
    {
        for (var i = 0; i < Math.Min(ingredients.Length, MaxIngredients); i++)
            if (!used[i] && ingredients.GetValue(i) is JsonItemStack { ResolvedItemstack: { } resolved } &&
                resolved.Class == stack.Class && resolved.Id == stack.Id)
                return i;
        return -1;
    }

    // Matches' test: each merged size's share of the sum within its ingredient's ratios, in ten-thousandths
    private static bool Ratios(ReadOnlySpan<(EnumItemClass Class, int Id, double Size, int Ingredient)> merged, Array ingredients)
    {
        var total = 0.0;
        foreach (var entry in merged.Bounded(MaxInputs)) total += entry.Size;
        foreach (var entry in merged.Bounded(MaxInputs))
        {
            if (ingredients.GetValue(entry.Ingredient) is not { } ingredient || _min is null || _max is null) return false;
            var (min, max) = (_min(ingredient), _max(ingredient));
            var share = (int)Math.Round(entry.Size / total * 10000.0);
            if (share < (int)Math.Round(min * 10000f) || share > (int)Math.Round(max * 10000f)) return false;
        }

        return true;
    }
}

using Newtonsoft.Json.Linq;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// CookingMatchTests' and WorldSynth's recipes, items and pots: three recipes with exact, wildcard and cooked stacks over items
// made on demand, each with an api answering no world
internal sealed class CookingKit
{
    private readonly ICoreAPI _api = Answers.Of<ICoreAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        { ["get_World"] = _ => null });

    private readonly Dictionary<string, Item> _items = [];

    public CookingRecipe[] Recipes() =>
        [
            new()
            {
                Code = "soup",
                Ingredients =
                [
                    Ingredient("water", 1, 1, 1f, Exact("waterportion", 100)),
                    Ingredient("veg", 1, 2, 0, Wild("vegetable-*")),
                    Ingredient("meat", 0, 1, 0, Cooked("redmeat-raw", "redmeat-cooked"))
                ]
            },
            new()
            {
                Code = "porridge",
                Ingredients =
                [
                    Ingredient("grain", 1, 2, 0, Wild("grain-*"), Exact("flour", 2)),
                    Ingredient("fruit", 0, 2, 0, Wild("fruit-*"))
                ]
            },
            new()
            {
                Code = "stew",
                Ingredients =
                [
                    Ingredient("meat", 2, 3, 0, Cooked("redmeat-raw", "redmeat-cooked")),
                    Ingredient("veg", 0, 2, 0, Wild("vegetable-*"))
                ]
            }
        ];

    private static CookingRecipeIngredient Ingredient(string code, int min, int max, float litres,
        params CookingRecipeStack[] stacks) =>
        new()
            { Code = code, MinQuantity = min, MaxQuantity = max, PortionSizeLitres = litres, ValidStacks = stacks };

    private CookingRecipeStack Exact(string code, int size) =>
        new()
        {
            Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = size,
            ResolvedItemstack = new ItemStack(Item(code), size)
        };

    private static CookingRecipeStack Wild(string code) =>
        new()
            { Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = 1 };

    private CookingRecipeStack Cooked(string raw, string cooked)
    {
        var stack = Exact(raw, 1);
        stack.CookedStack = new JsonItemStack
        {
            Code = new AssetLocation("game", cooked), Type = EnumItemClass.Item,
            ResolvedItemstack = new ItemStack(Item(cooked))
        };
        return stack;
    }

    // A pot filled for one of the recipes, a serving count apart, now and then one stack a portion off; with warm, a stack's
    // temperature attribute now and then
    public ItemStack?[] Pot(Random r, bool warm = false)
    {
        var q = r.Next(1, 4);
        string[][] recipes =
        [
            ["waterportion", "vegetable-carrot", "vegetable-onion", "redmeat-cooked"],
            ["grain-spelt", "fruit-cherry", "fruit-cherry"], ["redmeat-raw", "redmeat-raw", "vegetable-onion"]
        ];
        var chosen = recipes[r.Next(recipes.Length)].Where((_, k) => k < 2 || r.Next(2) == 0).ToList();
        var pot = new ItemStack?[r.Next(Math.Max(4, chosen.Count), 7)];
        for (var k = 0; k < chosen.Count; k++)
        {
            var size = chosen[k] == "waterportion" ? 100 * q : q;
            pot[k] = new ItemStack(Item(chosen[k]), r.Next(8) == 0 ? size + 1 : size);
            if (warm && r.Next(4) == 0) pot[k]!.Attributes.SetFloat("temperature", 20);
        }

        return [.. pot.OrderBy(_ => r.Next())];
    }

    public Item Item(string code)
    {
        if (_items.TryGetValue(code, out var item)) return item;

        item = new Item(_items.Count + 1) { Code = new AssetLocation("game", code) };
        if (code == "waterportion")
        {
            item.Attributes = new JsonObject(JToken.Parse("{ waterTightContainerProps: { itemsPerLitre: 100 } }"));
        }

        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, _api);
        _items[code] = item;
        return item;
    }
}

using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// AlloyRecipe.Matches answers as the engine's own body on random recipes and crucible contents: metals by class and id,
// some smelted from ores first, sizes summed per metal, ratios at their edges
[NonParallelizable]
public sealed class AlloyMatchTests
{
    [TearDown]
    public void Restore() => AlloyMatch.Enabled = true;

    [Test]
    public void TheAnswerIsTheEngines()
    {
        using var harmony = new TestHarmony("komet-test-alloymatch");
        AlloyMatch.Install(harmony, new QuietLogger());
        Assert.That(AlloyMatch.Matched, Is.True, "AlloyRecipe's bodies are not the ones verified");
        var metals = Enumerable.Range(1, 5).Select(i => new Item(i)).ToArray();
        var ores = metals.Select((m, i) => new Item(100 + i)
        {
            CombustibleProps = new CombustibleProperties
            {
                SmeltedRatio = 5, SmeltedStack = new JsonItemStack { ResolvedItemstack = new ItemStack(m) }
            }
        }).ToArray();
        var random = new Random(3);
        var (matched, cases) = (0, 0);
        for (var r = 0; r < 300; r++)
        {
            var picked = metals.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToArray();
            var recipe = new AlloyRecipe
            {
                Ingredients = [.. picked.Select(m => new MetalAlloyIngredient
                {
                    ResolvedItemstack = new ItemStack(m), MinRatio = 0.1f * random.Next(0, 5), MaxRatio = 0.5f + 0.1f * random.Next(0, 6)
                })]
            };
            for (var c = 0; c < 20; c++)
            {
                var inputs = new ItemStack?[4];
                for (var s = 0; s < inputs.Length; s++)
                    if (random.Next(4) > 0)
                    {
                        var i = random.Next(metals.Length);
                        inputs[s] = new ItemStack(random.Next(3) == 0 ? ores[i] : metals[i], random.Next(1, 40));
                    }

                var smelted = random.Next(2) == 0;
                AlloyMatch.Enabled = false;
                var engine = recipe.Matches(inputs!, smelted);
                AlloyMatch.Enabled = true;
                Assert.That(recipe.Matches(inputs!, smelted), Is.EqualTo(engine), $"recipe {r}, case {c}");
                (matched, cases) = (matched + (engine ? 1 : 0), cases + 1);
            }
        }

        Assert.That(matched, Is.InRange(20, cases - 20), "both answers occur");
    }
}

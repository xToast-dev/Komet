using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// Golden: CookingRecipe.Matches and CookingRecipeIngredient.GetMatchingStack answer every input exactly as the engine's own bodies did
// before the rewrite - match, servings and the matched stack - with liquids, cooked stacks, wildcards and ignored attributes, also
// after IgnoredStackAttributes is replaced; and a match no longer allocates its copies
[NonParallelizable]
public sealed class CookingMatchTests
{
    private const int Inputs = 500;
    private static readonly string[] Ignored = GlobalConstants.IgnoredStackAttributes;

    private readonly CookingKit _kit = new();

    [TearDown]
    public void Restore() => (CookingMatch.Enabled, GlobalConstants.IgnoredStackAttributes) = (true, Ignored);

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(CookingMatch.Matched, Is.True, "Matches or GetMatchingStack is not the body verified");
            Assert.That(CookingMatch.Rewritten, Is.True);
        });
    }

    // A Culinary Artillery's prefix replaces GetMatchingStack: the rewrite there gives way and returns once it is gone
    [Test]
    public void GivesWayToAReplacingPrefix()
    {
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        var matching = (MethodInfo)CookingMatch.Seams()[1]!;
        using (var other = new TestHarmony("komet-test-aca"))
        {
            _ = other.Patch(matching, new HarmonyMethod(typeof(CookingMatchTests), nameof(Replace)));
            CookingMatch.Recheck();
            Assert.Multiple(() =>
            {
                Assert.That(CookingMatch.Bypassed, Is.True);
                Assert.That(CookingMatch.Rewritten, Is.True, "Matches stays rewritten");
                Assert.That(Harmony.GetPatchInfo(matching)!.Transpilers, Is.Empty);
            });
        }

        CookingMatch.Recheck();
        Assert.Multiple(() =>
        {
            Assert.That(CookingMatch.Bypassed, Is.False);
            Assert.That(Harmony.GetPatchInfo(matching)!.Transpilers, Has.Count.EqualTo(1));
        });
    }

    private static bool Replace() => true;

    [TestCase(true)]
    [TestCase(false)]
    public void EveryAnswerIsTheEnginesOwn(bool enabled)
    {
        var (recipes, inputs) = (_kit.Recipes(), Stacks(new Random(7), Inputs));
        var expected = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = [.. Ignored, "custom"];
        var widened = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = Ignored;
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        CookingMatch.Enabled = enabled;
        Assert.That(CookingMatch.Rewritten, Is.True);
        var actual = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = [.. Ignored, "custom"];
        var actualWidened = Replies(recipes, inputs);
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actualWidened, Is.EqualTo(widened), "a replaced ignore list is followed");
            Assert.That(expected.Count(a => a.StartsWith("match", StringComparison.Ordinal)), Is.GreaterThan(20),
                "the inputs reach matches too");
        });
    }

    [Test]
    public void AMatchNoLongerCopies()
    {
        var (recipes, inputs) = (_kit.Recipes(), Stacks(new Random(11), 50));
        _ = Allocated(recipes, inputs); // first calls allocate for the runtime, not the match
        var engine = Allocated(recipes, inputs);
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        _ = Allocated(recipes, inputs); // the thread's lists
        var komet = Allocated(recipes, inputs);
        var kept = typeof(CookingMatch).GetField("_stacks", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        Assert.Multiple(() =>
        {
            Assert.That(komet, Is.LessThan(engine / 2), $"engine {engine} bytes, Komet {komet}");
            Assert.That(kept, Is.Empty,
                "the thread's list keeps the stacks, and with them their world"); // the main thread outlives it
        });
    }

    private static long Allocated(CookingRecipe[] recipes, ItemStack?[][] inputs)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var input in inputs)
        {
            foreach (var recipe in recipes)
            {
                var servings = 0;
                _ = recipe.Matches(input, ref servings);
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static List<string> Replies(CookingRecipe[] recipes, ItemStack?[][] inputs)
    {
        var answers = new List<string>();
        foreach (var input in inputs)
        {
            foreach (var recipe in recipes)
            {
                var servings = -7;
                var match = recipe.Matches(input, ref servings);
                answers.Add($"{(match ? "match" : "no")} {servings}");
                foreach (var ingredient in recipe.Ingredients!)
                {
                    answers.Add(string.Join(",",
                        input.Select(s => Array.IndexOf(ingredient.ValidStacks, ingredient.GetMatchingStack(s)))));
                }
            }
        }

        return answers;
    }

    // Pot contents of 4 to 6 slots: some empty, a stack size that often fits a portion, and now and then an attribute - ignored by
    // default, ignored only after the list is widened, or never
    private ItemStack?[][] Stacks(Random r, int count)
    {
        string[] codes =
        [
            "waterportion", "vegetable-carrot", "vegetable-onion", "redmeat-raw", "redmeat-cooked", "grain-spelt",
            "flour", "fruit-cherry", "stone-granite"
        ];
        var inputs = new ItemStack?[count][];
        for (var i = 0; i < count; i++)
        {
            if (i % 2 == 0)
            {
                inputs[i] = _kit.Pot(r);
                continue;
            }

            inputs[i] = new ItemStack?[r.Next(4, 7)];
            for (var s = 0; s < inputs[i].Length; s++)
            {
                if (r.Next(10) < 4) continue;

                var code = codes[r.Next(codes.Length)];
                var portion = code == "flour" ? 2 : 1;
                var size = code == "waterportion" ? 100 * r.Next(1, 4) : r.Next(1, 4) * portion;
                var stack = new ItemStack(_kit.Item(code), size);
                var mark = r.Next(8);
                if (mark == 0) stack.Attributes.SetFloat("temperature", 20);

                if (mark == 1) stack.Attributes.SetString("custom", "x");

                if (mark == 2) stack.Attributes.SetString("other", "y");

                inputs[i][s] = stack;
            }
        }

        return inputs;
    }
}

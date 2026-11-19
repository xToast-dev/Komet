using Komet.Shapes;
using Vintagestory.GameContent;

namespace Komet.Test.Shapes;

// Creature items and wearables build their inventory mesh from shapes parsed without the animations: the same elements and
// textures, no animations, and both engine builders found and rewritten
[NonParallelizable]
public sealed class InventoryShapesTests
{

    // Read from the asset's bytes through the pooled buffer: the same shape as the text without the animations, BOM or not
    [TestCase(false)]
    [TestCase(true)]
    public void TheAssetReadsAsItsTextWouldWithoutAnimations(bool bom)
    {
        GameInstall.RequireAssets();
        var bytes = File.ReadAllBytes(Path.Combine(GameInstall.Assets,
            "survival/shapes/entity/animal/mammal/hooved/deer/moose/adult.json"));
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];

        var asset = new Asset(bytes, new AssetLocation("game:shapes/entity/animal/mammal/hooved/deer/moose/adult.json"),
            null);
        var lean = InventoryShapes.Lean(asset, null)!;
        var full = asset.ToObject<Shape>();
        Assert.Multiple(() =>
        {
            Assert.That(lean.Animations, Is.Null);
            Assert.That(Names(lean.Elements), Is.EqualTo(Names(full.Elements)));
            Assert.That(lean.Textures.Keys, Is.EquivalentTo(full.Textures.Keys));
        });
    }

    [Test]
    public void BothBuildersAreRewritten()
    {
        var harmony = new Harmony("komet-test-inventoryshapes");
        try
        {
            InventoryShapes.Install(harmony);
            Assert.That((InventoryShapes.Creatures, InventoryShapes.Wearables), Is.EqualTo((true, true)));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static List<string> Names(ShapeElement[]? elements) =>
        elements is null
            ? []
            :
            [
                .. elements.SelectMany(e => (List<string>)
                    [e.Name + ":" + e.FacesResolved?.OfType<ShapeElementFace>().Count(), .. Names(e.Children)])
            ];
}

// Only creature and wearable items (and classes learnt slow) wait for a later frame; plain items and blocks never
public sealed class IconBudgetTests
{

    private sealed class ModWearable(CollectibleObject collectible) : CollectibleBehaviorWearable(collectible);
}

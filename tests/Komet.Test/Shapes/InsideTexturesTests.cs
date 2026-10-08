namespace Komet.Test.Shapes;

// The rewrite finds its one concatenation in the engine's VoxelMaterial.FromBlock, and each inside name is the engine's string, made
// once
public sealed class InsideTexturesTests
{
    [Test]
    public void TheEnginesConcatenationGivesWayToSixNames()
    {
        var harmony = new Harmony("komet-test-insidetextures");
        try
        {
            InsideTextures.Install(harmony);
            Assert.That(InsideTextures.Rewritten, Is.True);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }

        var north = BlockFacing.NORTH.Code;
        var first = InsideTextures.Name("inside-", north);
        Assert.That(first, Is.EqualTo("inside-north"));
        Assert.That(InsideTextures.Name("inside-", north), Is.SameAs(first));
        Assert.That(InsideTextures.Name("other-", north), Is.EqualTo("other-north"));
    }
}

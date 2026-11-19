using Komet.World;

namespace Komet.Test.World;

// The four rewrites find their one site each; the pools hand back what a new object or array would be
[NonParallelizable]
public sealed class WorldGenScratchTests
{
    [Test]
    public void AllFourAreRewritten()
    {
        var harmony = new Harmony("komet-test-worldgenscratch");
        try
        {
            WorldGenScratch.Install(harmony);
            Assert.That(WorldGenScratch.Rewritten, Is.EqualTo(WorldGenScratch.AllBits));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void ACodesLastPartIsTheEnginesAnswer()
    {
        var block = new Block { Code = new AssetLocation("game:rock-granite") };
        var last = 0;
        Assert.Multiple(() =>
        {
            Assert.That(WorldGenScratch.LastCodePart(block, last), Is.EqualTo(block.LastCodePart(last)));
            Assert.That(WorldGenScratch.LastCodePart(block, last), Is.EqualTo("granite"));
            Assert.That(WorldGenScratch.LastCodePart(block, 1), Is.EqualTo("rock"));
        });
    }
}

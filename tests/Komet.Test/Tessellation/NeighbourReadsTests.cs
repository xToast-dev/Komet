using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// BuildExtendedChunkData's one Unpack becomes the read-only one, its Data reads views, TesselateChunk's unpack Center
[NonParallelizable]
public sealed class NeighbourReadsTests
{
    [Test]
    public void TheNeighboursAreReadOnly()
    {
        using var harmony = new TestHarmony("komet-test-neighbourreads");
        NeighbourReads.Install(harmony);
        Assert.Multiple(() =>
        {
            Assert.That(NeighbourReads.Rewritten, Is.True, "BuildExtendedChunkData no longer calls Unpack once");
            Assert.That(NeighbourReads.Viewed, Is.True, "the build reads views and the pass leaves the chunk packed");
        });
    }
}

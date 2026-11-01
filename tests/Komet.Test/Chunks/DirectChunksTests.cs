using Komet.Chunks;

namespace Komet.Test.Chunks;

// The server's send and the client's read are both found and patched, and the single block reads
[NonParallelizable]
public sealed class DirectChunksTests
{
    [Test]
    public void SendAndReadArePatched()
    {
        using var harmony = new TestHarmony("komet-test-directchunks");
        DirectChunks.Install(harmony);
        PackedReads.Install(harmony);
        Assert.Multiple(() =>
        {
            Assert.That(DirectChunks.Installed, Is.True);
            Assert.That(PackedReads.Installed, Is.True, "single block reads of packed chunks go through views");
        });
    }
}

using Komet.World;

namespace Komet.Test.World;

// Both cloud texture uploads are found and rewritten
[NonParallelizable]
public sealed class CloudUploadsTests
{
    [Test]
    public void BothUploadsAreRewritten()
    {
        using var harmony = new TestHarmony("komet-test-clouduploads");
        CloudUploads.Install(harmony);
        Assert.That(CloudUploads.Rewritten, Is.True);
    }
}

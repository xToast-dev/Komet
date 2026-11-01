using Komet.Rendering;

namespace Komet.Test.Rendering;

// Every seam is rewritten; a join or lowered key made once is handed out again for the same instances, equal to the
// engine's
[NonParallelizable]
public sealed class StringScratchTests
{
    [Test]
    public void JoinsAndKeysAreKeptPerInstance()
    {
        using var harmony = new TestHarmony("komet-test-stringscratch");
        StringScratch.Install(harmony);
        var uid = new string(['u', 'i', 'd']);
        var (joined, lowered) = (StringScratch.Joined("hotbar-", uid), StringScratch.Lowered("vsyncMode"));
        Assert.Multiple(() =>
        {
            Assert.That(StringScratch.Rewritten, Is.EqualTo(StringScratch.Seams().Length));
            Assert.That(joined, Is.EqualTo("hotbar-uid"));
            Assert.That(StringScratch.Joined("hotbar-", uid), Is.SameAs(joined));
            Assert.That(StringScratch.Joined3("character", "-", uid), Is.EqualTo("character-uid"));
            Assert.That(StringScratch.Joined3("a", "b", null), Is.EqualTo("ab"));
            Assert.That(lowered, Is.EqualTo("vsyncmode"));
            Assert.That(StringScratch.Lowered("vsyncMode"), Is.SameAs(lowered));
            Assert.That(StringScratch.Lowered("bloom"), Is.SameAs("bloom"));
        });
    }
}

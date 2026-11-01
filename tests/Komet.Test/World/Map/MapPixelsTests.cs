using Komet.World;

namespace Komet.Test.World.Map;

// A piece goes back only after both holders (save and upload) are done, comes back cleared, and the rewrite takes the
// engine's one pixel array
[NonParallelizable]
public sealed class MapPixelsTests
{
    [TearDown]
    public void Restore() => MapPixels.Clear();

    [Test]
    public void APieceIsReusedOnlyAfterBothHolders()
    {
        MapPixels.Clear();
        var first = MapPixels.Rent(MapPixels.Length);
        first[7] = 42;
        MapPixels.Release(first); // saved, not yet uploaded
        var second = MapPixels.Rent(MapPixels.Length);
        Assert.That(second, Is.Not.SameAs(first), "handed out while the upload still holds it");
        MapPixels.Release(first); // uploaded
        var third = MapPixels.Rent(MapPixels.Length);
        Assert.Multiple(() =>
        {
            Assert.That(third, Is.SameAs(first), "back after both holders");
            Assert.That(third[7], Is.Zero, "cleared");
        });
    }

    [Test]
    public void TheMapRewriteTakesItsPixelArray()
    {
        using var harmony = new TestHarmony("komet-test-mappixels");
        MapPixels.Install(harmony);
        Assert.That(MapPixels.Rewritten, Is.True);
    }
}

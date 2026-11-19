using Komet.World;

namespace Komet.Test.Rendering;

// MultiChunkMapComponent.FinishSetChunks is taken over only for pieces the engine's draw copies texel for texel: 32x32 and every
// pixel opaque; anything else, and a switched off or unverified feature, is the engine's
[NonParallelizable]
public sealed class MapTileUploadTests
{
    [TearDown]
    public void Restore() => MapTileUpload.Enabled = true;

    private static int[] Piece(int alpha = 0xFF) =>
        [.. Enumerable.Range(0, 1024).Select(i => (alpha << 24) | ((i * 2713) & 0xFFFFFF))];

    [Test]
    public void TakesOverOnlyOpaqueWholePieces()
    {
        var translucent = Piece();
        translucent[517] &= 0x7FFFFFFF;
        Assert.Multiple(() =>
        {
            Assert.That(MapTileUpload.Uploadable([Piece(), null, null, null, Piece(), null, null, null, Piece()]),
                Is.True);
            Assert.That(MapTileUpload.Uploadable(new int[]?[9]), Is.True, "nothing to set");
            Assert.That(MapTileUpload.Uploadable([Piece(), translucent, null, null, null, null, null, null, null]),
                Is.False,
                "one pixel not opaque");
            Assert.That(MapTileUpload.Uploadable([Piece(0), null, null, null, null, null, null, null, null]), Is.False,
                "transparent");
            Assert.That(MapTileUpload.Uploadable([new int[512], null, null, null, null, null, null, null, null]),
                Is.False,
                "not 32x32");
        });
    }

}

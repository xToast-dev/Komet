using Komet.Options;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Komet.Test.Options;

// The globe's atlas from a map database: pieces near the player are laid in their colour, the rest of the square stays
// clear, and a piece no texel centre falls in is never read
public sealed class WorldAtlasTests
{

    // The atlas' own reader gives what the game's serializer wrote, negative (opaque ARGB) colours included
    [Test]
    public void ColoursAreReadAsTheSerializerWroteThem()
    {
        var pixels = new int[32 * 32];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = unchecked((int)(0xFF000000u | (uint)(i * 40503 % 0xFFFFFF)));
        }

        pixels[0] = 0;
        pixels[1] = 1;
        var data = SerializerUtil.Serialize(new MapPieceDB { Pixels = pixels });
        var read = new int[32 * 32];
        Assert.That(WorldAtlas.Colours(data, read), Is.True);
        Assert.That(read, Is.EqualTo(pixels));
    }
}

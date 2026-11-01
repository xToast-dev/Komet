using Komet.World;

namespace Komet.Test.World;

// Komet's encoder writes the engine's text for any bytes: zero groups ('z'), short last groups, empty input
public sealed class Ascii85EncodeTests
{
    [Test]
    public void TheTextIsTheEngines()
    {
        var random = new Random(85);
        for (var n = 0; n < 400; n++)
        {
            var bytes = new byte[n % 67];
            random.NextBytes(bytes);
            if (n % 3 == 0) Array.Clear(bytes, 0, Math.Min(bytes.Length, 8)); // zero groups
            Assert.That(Ascii85Encode.Encode(bytes), Is.EqualTo(Ascii85.Encode(bytes)), $"{bytes.Length} bytes");
        }
    }
}

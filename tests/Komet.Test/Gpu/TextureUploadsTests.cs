using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class TextureUploadsTests
{
    [Test]
    public void EveryShortIsNormalizedAsOpenGlDoes()
    {
        var shorts = Enumerable.Range(short.MinValue, 65536).Select(v => (short)v).ToArray();
        var into = new ushort[shorts.Length];
        TextureUploads.Normalized(shorts, into);
        var worst = shorts.Select((s, i) =>
            Math.Abs(into[i] - (int)Math.Round(Math.Clamp(s / 32767.0, 0, 1) * 65535, MidpointRounding.ToEven))).Max();
        Assert.Multiple(() =>
        {
            Assert.That(worst, Is.Zero);
            Assert.That(into[Array.IndexOf(shorts, (short)32767)], Is.EqualTo(65535));
            Assert.That(into[Array.IndexOf(shorts, (short)-5)], Is.Zero);
            for (var i = 0; i < shorts.Length; i++) Assert.That(into[i], Is.EqualTo(TextureUploads.Unorm(shorts[i])));
        });
    }

    [Test]
    public void BgraIsSwappedIntoRgba()
    {
        foreach (var n in new[] { 1, 7, 8, 13, 100 })
        {
            var bgra = Enumerable.Range(0, n).Select(i => (uint)(0x11223344 * (i + 1))).ToArray();
            var rgba = new uint[n];
            TextureUploads.Swapped(bgra, rgba);
            for (var i = 0; i < n; i++)
            {
                var p = bgra[i];
                Assert.That(rgba[i], Is.EqualTo((p & 0xFF00FF00) | ((p >> 16) & 0xFF) | ((p & 0xFF) << 16)), $"{n}: {i}");
            }
        }
    }
}

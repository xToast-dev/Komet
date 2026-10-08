using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class SamplersTests
{
    private static readonly int[] Filters = [0x2600, 0x2601, 0x2700, 0x2701, 0x2702, 0x2703];
    private static readonly int[] Wraps = [0x2901, 0x812F, 0x812D, 0x8370];

    [Test]
    public void OneParameterSetIsTheWholeSetReadAgain()
    {
        foreach (var min in Filters)
            foreach (var mag in new[] { 0x2600, 0x2601 })
                foreach (var wrap in Wraps)
                    foreach (var compare in new[] { false, true })
                    {
                        var state = Samplers.FromGl(min, mag, wrap, compare);
                        foreach (var to in Filters)
                            Assert.That(Samplers.Tuned(state, 0x2801, to), Is.EqualTo(Samplers.FromGl(to, mag, wrap, compare)));
                        foreach (var to in new[] { 0x2600, 0x2601 })
                            Assert.That(Samplers.Tuned(state, 0x2800, to), Is.EqualTo(Samplers.FromGl(min, to, wrap, compare)));
                        foreach (var to in Wraps)
                            Assert.That(Samplers.Tuned(state, 0x2802, to), Is.EqualTo(Samplers.FromGl(min, mag, to, compare)));
                        Assert.That(Samplers.Tuned(state, 0x884C, compare ? 0 : 0x884E),
                            Is.EqualTo(Samplers.FromGl(min, mag, wrap, !compare)));
                        Assert.That(Samplers.Tuned(state, 0x2803, 0x2901), Is.EqualTo(state), "wrap T is not read");
                        Assert.That(Samplers.Tuned(state, 0x813D, 4), Is.Null, "the max level: not followed");
                    }
    }
}

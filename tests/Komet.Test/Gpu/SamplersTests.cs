using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class SamplersTests
{
    [Test]
    public void EachWrapIsVulkansOwnAddressModeAlongItsAxis()
    {
        var state = Samplers.FromGl(0x2601, 0x2601, 0x8370, false, 0x812D); // mirrored along S, border along T
        Assert.That((state.U, state.V, state.W), Is.EqualTo((1u, 3u, 1u)));
        Assert.That(Samplers.FromGl(0x2601, 0x2601, 0x2900, false).U, Is.EqualTo(2u), "GL_CLAMP: to the edge");
    }
}

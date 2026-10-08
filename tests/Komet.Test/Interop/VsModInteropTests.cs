using Komet.Interop;

namespace Komet.Test.Interop;

public sealed class VsModInteropTests
{
    [Test]
    public void ProviderReportsNoAdaptiveChunkInflow()
    {
        Assert.That(VsModInterop.ProtocolId, Is.EqualTo("vsmod-interop/1"));
        Assert.That(VsModInterop.ProviderId, Is.EqualTo("komet"));
        Assert.That(VsModInterop.ProtocolVersion, Is.EqualTo(1));
        Assert.That(VsModInterop.TryGetFeatureState(VsModInterop.FeatureAdaptiveChunkInflow, out bool active), Is.True);
        Assert.That(active, Is.False);
        Assert.That(VsModInterop.Negotiate("optimum", [VsModInterop.FeatureAdaptiveChunkInflow]), Is.Empty);
    }

    [Test]
    public void ProviderRefusesUnknownFeature()
    {
        Assert.That(VsModInterop.TryGetFeatureState("komet.unknown", out bool active), Is.False);
        Assert.That(active, Is.False);
    }
}

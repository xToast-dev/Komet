using Komet.Interop;

namespace Komet.Test;

public sealed class InteropTests
{
    [Test]
    public void NightlyProviderExplicitlyReportsOldInflowFeatureInactive()
    {
        Assert.That(VsModInterop.ProtocolId, Is.EqualTo("vsmod-interop/1"));
        Assert.That(VsModInterop.ProviderId, Is.EqualTo("komet"));
        Assert.That(VsModInterop.TryGetFeatureState(VsModInterop.FeatureAdaptiveChunkInflow, out var active), Is.True);
        Assert.That(active, Is.False);
        Assert.That(VsModInterop.CoordinatedFeatureIds(), Is.Empty);
    }

    [Test]
    public void NightlyProviderDoesNotPretendToYieldAnUnimplementedFeature()
    {
        Assert.That(VsModInterop.TryYield(VsModInterop.FeatureAdaptiveChunkInflow, "optimum", "test", out var detail), Is.False);
        Assert.That(detail, Does.Contain("unsupported feature"));
        Assert.That(VsModInterop.Negotiate("optimum", [VsModInterop.FeatureAdaptiveChunkInflow]), Is.Empty);
    }

    [Test]
    public void ConsumerFindsAnOptimumProviderByProtocolMetadata()
    {
        Assert.That(ModInterop.TryGetFeatureState(ModInterop.OptimumProviderId, ModInterop.OptimumAdaptiveRadius, out var active), Is.True);
        Assert.That(active, Is.True);
    }

    [Test]
    public void NightlyProviderDoesNotAdvertiseUnsupportedOptimumRenderFeatures()
    {
        Assert.That(VsModInterop.TryYield("optimum.indirect-draw", "komet", "unsupported", out _), Is.False);
        Assert.That(VsModInterop.TryYield("optimum.simd-culling", "komet", "unsupported", out _), Is.False);
        Assert.That(VsModInterop.CoordinatedFeatureIds(), Is.Empty);
    }

    [Test]
    public void ConsumerAcceptsAnEmptyNegotiationFromAProtocolProvider()
    {
        Assert.That(ModInterop.TryNegotiate(ModInterop.OptimumProviderId, "komet", [], out var yielded), Is.True);
        Assert.That(yielded, Is.Empty);
    }
}

public static class OptimumProviderStub
{
    public const string ProtocolId = "vsmod-interop/1";
    public const string ProviderId = "optimum";

    public static bool TryGetFeatureState(string featureId, out bool active)
    {
        active = false;
        if (featureId != ModInterop.OptimumAdaptiveRadius) return false;
        active = true;
        return true;
    }

    public static string[] Negotiate(string requesterId, string[] requesterActiveFeatures)
        => [];

}

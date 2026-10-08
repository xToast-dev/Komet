namespace Komet.Interop;

/// <summary>Komet's provider for the duck-typed VS mod interop protocol.</summary>
public static class VsModInterop
{
    public const string ProtocolId = "vsmod-interop/1";
    public const string ProviderId = "komet";
    public const string FeatureAdaptiveChunkInflow = "komet.adaptive-chunk-inflow";

    public static int ProtocolVersion => 1;

    /// <summary>Komet 2.0 has no adaptive chunk-inflow brake; it reports that feature as known and inactive.</summary>
    public static bool TryGetFeatureState(string featureId, out bool active)
    {
        active = false;
        if (!Assert(!string.IsNullOrEmpty(featureId)) || !Assert(ProtocolVersion == 1)) return false;
        return featureId == FeatureAdaptiveChunkInflow;
    }

    /// <summary>Komet currently owns no feature that a peer needs it to yield.</summary>
    public static string[] Negotiate(string requesterId, string[] requesterActiveFeatures)
    {
        if (!Assert(!string.IsNullOrEmpty(requesterId)) || !Assert(requesterActiveFeatures is not null)) return [];
        return [];
    }
}

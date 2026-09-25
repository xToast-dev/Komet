namespace Komet.Interop;

/// <summary>
/// Komet nightly's provider for the duck-typed VS mod interop protocol.
///
/// Nightly does not contain the old adaptive chunk inflow brake. It reports that fact
/// explicitly so a peer can keep its own adaptive controller enabled instead of treating
/// the missing old field as an active brake.
/// </summary>
public static class VsModInterop
{
    public const string ProtocolId = "vsmod-interop/1";
    public const string ProviderId = "komet";
    public const string FeatureAdaptiveChunkInflow = "komet.adaptive-chunk-inflow";

    public static int ProtocolVersion => 1;

    public static bool TryGetFeatureState(string featureId, out bool active)
    {
        active = false;
        if (!Assert(!string.IsNullOrEmpty(featureId)) || !Assert(ProtocolVersion == 1)) return false;
        return featureId == FeatureAdaptiveChunkInflow;
    }

    public static string[] Negotiate(string requesterId, string[] requesterActiveFeatures)
    {
        if (!Assert(!string.IsNullOrEmpty(requesterId)) || !Assert(requesterActiveFeatures is not null)) return Array.Empty<string>();
        return Array.Empty<string>();
    }

    public static bool TryYield(string featureId, string requesterId, string reason, out string detail)
    {
        if (!Assert(!string.IsNullOrEmpty(featureId)) || !Assert(!string.IsNullOrEmpty(requesterId)))
        {
            detail = "invalid feature or requester id";
            return false;
        }
        detail = "unsupported feature: " + featureId;
        return false;
    }

    public static void Release(string featureId, string requesterId)
    {
        // Nightly owns no yieldable feature, so every release is intentionally a no-op.
        _ = Assert(!string.IsNullOrEmpty(featureId));
        _ = Assert(!string.IsNullOrEmpty(requesterId));
    }

    public static string[] CoordinatedFeatureIds()
    {
        _ = Assert(ProtocolId.Length > 0);
        _ = Assert(ProviderId.Length > 0);
        return Array.Empty<string>();
    }
}

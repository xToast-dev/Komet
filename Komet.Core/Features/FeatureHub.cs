using System.Reflection;
using Komet.Api;

namespace Komet.Core;

// Every feature behind KAPI's KapiFeatures: Komet's (by name, as Snapshot lists them) and other mods' (modid:name), their
// state, the holds that keep one at the game's behaviour, and other mods' registrations. Holds, registrations and
// subscriptions end when the world closes. Main thread only.
internal static class FeatureHub
{
    // Komet's options pages a KapiFeature.Page may name; any other id is one of the mod's own pages
    public const string RenderPage = "komet-render", ChunksPage = "komet-chunks", MiscPage = "komet-misc";

    private const string StateChangedName = "komet.Features.StateChanged";

    // Its subscriptions belong to the world they were made in
    static FeatureHub() => WorldEnd.On(nameof(FeatureHub), Forget);

    // A feature was installed, switched, held or released, stood down or back; a handler that throws goes
    public static event EventHandler<KapiStateChangedEventArgs>? StateChanged;

    // Every feature in install order, Komet's first; a copy
    public static KapiFeatureInfo[] Snapshot() => FeatureStates.Snapshot();

    // A feature by id or by the name of one of its switches (UploadCap finds ChunkBudget)
    public static KapiFeatureState StateOf(string id)
    {
        if (!NotNull(id)) return KapiFeatureState.Unknown;

        var feature = Features.Find(id);
        return feature >= 0 ? Features.Evaluate(feature) : KapiFeatureState.Unknown;
    }

    // Keeps a feature at the game's own behaviour until released or the world closes; a refused hold holds nothing
    public static KapiHold HoldOff(string id, string modId, string reason) =>
        NotNull(id) && NotNull(modId) && NotNull(reason)
            ? Features.HoldOff(id, modId, reason)
            : new KapiHold("", "", "", -1);

    // Another mod's feature: false when refused, the log says why
    public static bool Register(string modId, KapiFeature feature) =>
        NotNull(modId) && NotNull(feature) && ExternalFeatures.Register(modId, feature);

    // What KapiFeature.Fingerprint compares; 0 for a missing or bodiless method
    public static ulong Fingerprint(IReadOnlyList<MethodBase?> methods) =>
        NotNull(methods) && Assert(methods.Count <= EngineShape.MaxMethods) ? EngineShape.Of([.. methods]) : 0;

    internal static void Raise(string id, KapiFeatureState previous, KapiFeatureState state)
    {
        if (StateChanged is null || !NotNull(id) || !Assert(previous != state)) return;

        var args = new KapiStateChangedEventArgs(id, previous, state);
        ModEvents.Raise(StateChanged, h => h(null, args), h => StateChanged -= h, StateChangedName);
    }

    private static void Forget() => StateChanged = null;
}

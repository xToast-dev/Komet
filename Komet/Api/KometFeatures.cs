using System.Reflection;

namespace Komet.Api;

// What a feature does right now; the first that applies wins. Unknown: no feature has the id.
public enum FeatureState
{
    Unknown,
    Pending, // not installed (yet)
    Active,
    Off, // every knob at its engine value: the player switched it off
    HeldOff, // a mod holds it at the game's own behaviour (KometFeatures.HoldOff)
    StoodDown, // another mod patches what it relies on
    EngineChanged, // the game's bodies are not the ones it was written against: not installed
    NotApplicable, // server code on a remote server, hardware or a driver without what it needs
    Failed // it threw, or its own guard gave up
}

// Komet's features and other mods' own, by id: Komet's as the README names them (FrustumSweep, ChunkBudget, ...), another mod's
// modid:name. A mod asks for their state, holds one at the game's own behaviour while it needs that (HoldOff), follows them
// (StateChanged) and registers features of its own, whose switch Komet then shows, holds, benchmarks and stands down for other mods'
// patches (Register). Same rules as KometOptions: call from a class of your own, only when api.ModLoader.IsModEnabled("komet"); main
// thread only. Komet ends every hold and drops every registration and subscription when the world closes: register again in the next.
public static class KometFeatures
{
    // Komet's options pages a FeatureDefinition.Page may name; any other id is one of the mod's own KometOptions pages
    public const string RenderPage = "komet-render", ChunksPage = "komet-chunks", MiscPage = "komet-misc";
    public const int MaxFeatures = Features.MaxFeatures, MaxExternal = Features.MaxExternal;

    // Installed, switched, held or released, stood down or back. A handler that throws is logged once and unsubscribed.
    public static event EventHandler<FeatureStateChangedEventArgs>? StateChanged;

    // Every feature in install order, Komet's first: a copy that does not follow later changes
    public static IReadOnlyList<FeatureInfo> Snapshot()
    {
        var features = Features.Snapshot();
        return Assert(features.Length <= MaxFeatures) && Assert(features.Length > 0) ? features : [];
    }

    // By feature id, or by the name of one of its switches (UploadCap: ChunkBudget); Unknown for neither
    public static FeatureState StateOf(string id)
    {
        if (!NotNull(id)) return FeatureState.Unknown;
        var feature = Features.Find(id);
        return feature >= 0 && Assert(feature < MaxFeatures) ? Features.Evaluate(feature) : FeatureState.Unknown;
    }

    // Keeps the feature at the game's own behaviour, whatever the player chose, until the handle is released or the world closes.
    // The player's choice applies again once the last hold goes. Works before Komet started too. A feature without a switch cannot
    // be held: the handle is then not holding, and the log says why.
    public static FeatureHold HoldOff(string id, string modId, string reason) =>
        NotNull(id) && NotNull(modId) && NotNull(reason)
            ? Features.HoldOff(id, modId, reason)
            : new FeatureHold("", "", "", -1);

    // Before Komet started (another mod's Start) it is installed after Komet's own features, while Komet runs (StartClientSide) at
    // once. false: refused, the log says why (an invalid definition, an id taken, too many features, after the world loaded).
    public static bool Register(FeatureDefinition definition) => NotNull(definition) && Features.Register(definition);

    // The fingerprint FeatureDefinition.Fingerprint compares, to pin in the mod's own test against the installed game; 0 when a
    // method is missing or has no body
    public static ulong Fingerprint(IReadOnlyList<MethodBase?> methods) =>
        NotNull(methods) && Assert(methods.Count <= EngineShape.MaxMethods) ? EngineShape.Of([.. methods]) : 0;

    internal static void Raise(string id, FeatureState previous, FeatureState state)
    {
        if (StateChanged is null || !NotNull(id) || !Assert(previous != state)) return;
        var args = new FeatureStateChangedEventArgs(id, previous, state);
        ApiEvents.Raise(StateChanged, h => h(null, args), h => StateChanged -= h, "KometFeatures.StateChanged");
    }

    // The world closes: no subscriber hears of it
    internal static void Forget() => StateChanged = null;
}

// One feature as Snapshot found it. Title: a registered feature's, Komet's id for its own. Owner: the mod id ("komet" for Komet's).
// HeldBy and Reason: the first hold still in place, null without one.
public sealed class FeatureInfo
{
    internal FeatureInfo(string id, string title, string owner, FeatureState state, bool canHoldOff)
    {
        (Id, Title, Owner, State, CanHoldOff) = (id, title, owner, state, canHoldOff);
        _ = Assert(id.Length > 0) && Assert(owner.Length > 0);
    }

    public string Id { get; }
    public string Title { get; }
    public string Owner { get; }
    public FeatureState State { get; }
    public bool CanHoldOff { get; } // it has a switch
    public string? HeldBy { get; internal init; }
    public string? Reason { get; internal init; }
}

// A mod's hold on a feature (KometFeatures.HoldOff). Release, or Dispose, lets go once; after that, and after the world closed, both
// do nothing. IsHolding is false for a refused hold too. FeatureId: the feature held (ChunkBudget for UploadCap), else as given.
public sealed class FeatureHold : IDisposable
{
    internal FeatureHold(string featureId, string modId, string reason, int feature)
    {
        (FeatureId, ModId, Reason, Feature) = (featureId, modId, reason, feature);
        _ = Assert(feature >= -1) && Assert(feature < Features.MaxFeatures);
    }

    public string FeatureId { get; }
    public string ModId { get; }
    public string Reason { get; }
    public bool IsHolding => Features.Holding(this);
    internal int Feature { get; }

    public void Release()
    {
        if (!Assert(Feature >= -1) || !NotNull(ModId)) return;
        _ = Features.Release(this);
    }

    public void Dispose()
    {
        Release();
        _ = Assert(!IsHolding) && NotNull(FeatureId);
    }
}

public sealed class FeatureStateChangedEventArgs(string id, FeatureState previous, FeatureState state) : EventArgs
{
    public string Id { get; } = id;
    public FeatureState Previous { get; } = previous;
    public FeatureState State { get; } = state;
}

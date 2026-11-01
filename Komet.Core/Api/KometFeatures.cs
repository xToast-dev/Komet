using System.Reflection;
using Komet.Core;

namespace Komet.Api;

/// <summary>What a feature does right now. When several apply, the first in this list wins.</summary>
public enum FeatureState
{
    /// <summary>No feature has this id.</summary>
    Unknown,
    /// <summary>Not installed yet.</summary>
    Pending,
    /// <summary>Installed and doing its work.</summary>
    Active,
    /// <summary>Every knob at its engine value: the player switched it off.</summary>
    Off,
    /// <summary>A mod holds it at the game's own behaviour (<see cref="KometFeatures.HoldOff"/>).</summary>
    HeldOff,
    /// <summary>Another mod patches what it relies on, so it keeps out of the way.</summary>
    StoodDown,
    /// <summary>The game's bodies are not the ones it was written against: not installed.</summary>
    EngineChanged,
    /// <summary>Server code on a remote server, or hardware or a driver without what it needs.</summary>
    NotApplicable,
    /// <summary>It threw, or its own guard gave up.</summary>
    Failed
}

/// <summary>
/// Komet's features (by their README names) and other mods' (<c>modid:name</c>): their state, holds that keep one at
/// the game's behaviour, and registration of a mod's own.
/// </summary>
/// <remarks>
/// Call from a class of your own only when <c>api.ModLoader.IsModEnabled("komet")</c>, on the main thread. Holds,
/// registrations and subscriptions end when the world closes; register again on the next join.
/// </remarks>
public static class KometFeatures
{
    /// <summary>Komet's options pages a <see cref="FeatureDefinition.Page"/> may name. Any other id is one of the mod's own <see cref="KometOptions"/> pages.</summary>
    public const string RenderPage = "komet-render", ChunksPage = "komet-chunks", MiscPage = "komet-misc";
    /// <summary>How many features Komet keeps in all (<c>MaxFeatures</c>) and how many of them may come from other mods (<c>MaxExternal</c>).</summary>
    public const int MaxFeatures = Features.MaxFeatures, MaxExternal = Features.MaxExternal;

    /// <summary>A feature was installed, switched, held or released, stood down or back. A handler that throws is logged once and unsubscribed.</summary>
    public static event EventHandler<FeatureStateChangedEventArgs>? StateChanged;

    /// <summary>Every feature in install order, Komet's first.</summary>
    /// <returns>A copy; it does not change later.</returns>
    public static IReadOnlyList<FeatureInfo> Snapshot() => Features.Snapshot();

    /// <summary>A feature's state.</summary>
    /// <param name="id">A feature id, or the name of one of its switches (<c>UploadCap</c> finds <c>ChunkBudget</c>).</param>
    /// <returns>The state; <see cref="FeatureState.Unknown"/> when no feature matches.</returns>
    public static FeatureState StateOf(string id)
    {
        if (!NotNull(id)) return FeatureState.Unknown;
        var feature = Features.Find(id);
        return feature >= 0 ? Features.Evaluate(feature) : FeatureState.Unknown;
    }

    /// <summary>
    /// Keeps a feature at the game's own behaviour until the hold is released or the world closes. Works before Komet
    /// started too. A feature without a switch cannot be held.
    /// </summary>
    /// <example>
    /// <code language="csharp">
    /// // our shadow pass needs the game's own culling
    /// hold = KometFeatures.HoldOff("FrustumSweep", "mymod", "Our shadow pass needs the game's culling");
    /// </code>
    /// </example>
    /// <param name="id">The feature, as for <see cref="StateOf"/>.</param>
    /// <param name="modId">The holding mod; shown to the player.</param>
    /// <param name="reason">Why; shown to the player.</param>
    /// <returns>The hold; check <see cref="FeatureHold.IsHolding"/>, a refused hold holds nothing.</returns>
    public static FeatureHold HoldOff(string id, string modId, string reason) =>
        NotNull(id) && NotNull(modId) && NotNull(reason)
            ? Features.HoldOff(id, modId, reason)
            : new FeatureHold("", "", "", -1);

    /// <summary>
    /// Adds another mod's feature. Before Komet started it is installed after Komet's own; while Komet runs, at once;
    /// after LevelFinalize it is refused.
    /// </summary>
    /// <param name="definition">The feature.</param>
    /// <returns>False when refused; the log says why.</returns>
    public static bool Register(FeatureDefinition definition) => NotNull(definition) && Features.Register(definition);

    /// <summary>
    /// What <see cref="FeatureDefinition.Fingerprint"/> compares. Pin it in the mod's own test, so a game update that
    /// changes the bodies fails there first.
    /// </summary>
    /// <param name="methods">The engine methods, as <see cref="FeatureDefinition.Shaped"/> gives them.</param>
    /// <returns>The fingerprint; 0 for a missing or bodiless method.</returns>
    public static ulong Fingerprint(IReadOnlyList<MethodBase?> methods) =>
        NotNull(methods) && Assert(methods.Count <= EngineShape.MaxMethods) ? EngineShape.Of([.. methods]) : 0;

    internal static void Raise(string id, FeatureState previous, FeatureState state)
    {
        if (StateChanged is null || !NotNull(id) || !Assert(previous != state)) return;
        var args = new FeatureStateChangedEventArgs(id, previous, state);
        ApiEvents.Raise(StateChanged, h => h(null, args), h => StateChanged -= h, "KometFeatures.StateChanged");
    }

    internal static void Forget() => StateChanged = null;
}

/// <summary>One feature as <see cref="KometFeatures.Snapshot"/> found it.</summary>
public sealed class FeatureInfo
{
    internal FeatureInfo(string id, string title, string owner, FeatureState state, bool canHoldOff)
    {
        (Id, Title, Owner, State, CanHoldOff) = (id, title, owner, state, canHoldOff);
    }

    /// <summary>The feature id: a README name for Komet's, <c>modid:name</c> for other mods'.</summary>
    public string Id { get; }
    /// <summary>The row title.</summary>
    public string Title { get; }
    /// <summary>The id of the mod the feature belongs to (<c>komet</c> for Komet's).</summary>
    public string Owner { get; }
    /// <summary>The state when the snapshot was taken.</summary>
    public FeatureState State { get; }
    /// <summary>It has a switch, so <see cref="KometFeatures.HoldOff"/> can hold it.</summary>
    public bool CanHoldOff { get; }
    /// <summary>The mod of the first hold in place, else null.</summary>
    public string? HeldBy { get; internal init; }
    /// <summary>The reason of the first hold in place, else null.</summary>
    public string? Reason { get; internal init; }
}

/// <summary>A hold from <see cref="KometFeatures.HoldOff"/>. <see cref="Release"/> or <see cref="Dispose"/> let go once; later calls do nothing.</summary>
public sealed class FeatureHold : IDisposable
{
    internal FeatureHold(string featureId, string modId, string reason, int feature)
    {
        (FeatureId, ModId, Reason, Feature) = (featureId, modId, reason, feature);
    }

    /// <summary>The held feature's id.</summary>
    public string FeatureId { get; }
    /// <summary>The holding mod.</summary>
    public string ModId { get; }
    /// <summary>Why it holds; shown to the player.</summary>
    public string Reason { get; }
    /// <summary>The hold is in place: false once released, and for a refused hold.</summary>
    public bool IsHolding => Features.Holding(this);
    internal int Feature { get; }

    /// <summary>Lets the feature go back to the player's value.</summary>
    public void Release() => _ = Features.Release(this);

    /// <summary>The same as <see cref="Release"/>.</summary>
    public void Dispose() => Release();
}

/// <summary>The change <see cref="KometFeatures.StateChanged"/> reports.</summary>
/// <param name="id">The feature.</param>
/// <param name="previous">Its state before.</param>
/// <param name="state">Its state now.</param>
public sealed class FeatureStateChangedEventArgs(string id, FeatureState previous, FeatureState state) : EventArgs
{
    /// <summary>The feature.</summary>
    public string Id { get; } = id;
    /// <summary>Its state before.</summary>
    public FeatureState Previous { get; } = previous;
    /// <summary>Its state now.</summary>
    public FeatureState State { get; } = state;
}

// The public API's events (KometFeatures, KometOptions, KometStyles) and refusal log; the logger is set at StartPre, so
// refusals before Komet's Start log too
internal static class ApiEvents
{
    public const int MaxHandlers = 64;

    public static ILogger? Logger { get; set; }

    /// <summary>
    /// Each handler separately, at most MaxHandlers; one that throws is logged once with its assembly and removed
    /// </summary>
    public static void Raise<T>(T? handlers, Action<T> invoke, Action<T> remove, string name) where T : Delegate
    {
        if (handlers is null || !NotNull(invoke) || !NotNull(remove)) return;
        foreach (var handler in handlers.GetInvocationList().Bounded(MaxHandlers))
            try
            {
                invoke((T)handler);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                remove((T)handler);
                Logger?.Error("Komet: a {0} handler of {1} threw and is unsubscribed: {2}", name,
                    handler.Method.DeclaringType?.Assembly.GetName().Name ?? "?", e);
            }
    }
}

using System.Reflection;
using HarmonyLib;
using Komet.Core;

namespace Komet.Api;

/// <summary>What a feature does right now. When several apply, the first in this list wins.</summary>
public enum KapiFeatureState
{
    /// <summary>No feature has this id.</summary>
    Unknown,

    /// <summary>Not installed yet.</summary>
    Pending,

    /// <summary>Installed and doing its work.</summary>
    Active,

    /// <summary>Every knob at its engine value: the player switched it off.</summary>
    Off,

    /// <summary>A mod holds it at the game's own behaviour (<see cref="KapiFeatures.HoldOff" />).</summary>
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

/// <summary>Kinds of Harmony patch, as flags, for <see cref="KapiFeature.StandDownFor" />.</summary>
[Flags]
public enum KapiPatchKinds
{
    /// <summary>No patch kind.</summary>
    None = 0,

    /// <summary>A prefix: it may skip the original or change its arguments.</summary>
    Prefix = 1,

    /// <summary>A postfix: it runs after the original and may change the result.</summary>
    Postfix = 2,

    /// <summary>A transpiler: it rewrites the original's IL.</summary>
    Transpiler = 4,

    /// <summary>A finalizer: it runs after the original and sees its exception.</summary>
    Finalizer = 8,

    /// <summary>An inner prefix, on a call inside the original.</summary>
    InnerPrefix = 16,

    /// <summary>An inner postfix, on a call inside the original.</summary>
    InnerPostfix = 32,

    /// <summary>Every kind that changes what the original's body does.</summary>
    Body = Transpiler | InnerPrefix | InnerPostfix,

    /// <summary>Every kind that can replace the original: a prefix or a body change. The default.</summary>
    Replacing = Prefix | Body,

    /// <summary>Every patch kind.</summary>
    All = Replacing | Postfix | Finalizer
}

/// <summary>
///     A feature's switch or slider. <c>get</c> and <c>set</c> carry the player's value, which the mod saves;
///     <c>apply</c> gets what the patches should read: the player's value, or the engine value while the feature is held
///     off or stood down. Main thread only.
/// </summary>
public sealed class KapiKnob
{
    private KapiKnob(int min, int max, int engine, string unit, Func<int> get, Action<int> set, Action<int> apply) =>
        (Min, Max, Engine, Unit, Get, Set, Apply) = (min, max, engine, unit, get, set, apply);

    // The lowest and highest value, the one that leaves the game's own behaviour, and the unit after a slider's value
    internal int Min { get; }
    internal int Max { get; }
    internal int Engine { get; }
    internal string Unit { get; }

    // A switch: 0 and 1, engine 0
    internal bool IsSwitch => Min == 0 && Max == 1;

    internal Func<int> Get { get; }
    internal Action<int> Set { get; }
    internal Action<int> Apply { get; }
    internal bool Valid => Min < Max && Engine >= Min && Engine <= Max;

    /// <summary>A switch: on (1) or off (0, the engine value).</summary>
    /// <param name="get">The player's value.</param>
    /// <param name="set">Stores the player's value; the mod saves it.</param>
    /// <param name="apply">What the patches read; false while held off or stood down.</param>
    /// <returns>The knob.</returns>
    public static KapiKnob Switch(Func<bool> get, Action<bool> set, Action<bool> apply) =>
        NotNull(get) && NotNull(set) && NotNull(apply)
            ? new KapiKnob(0, 1, 0, "", () => get() ? 1 : 0, v => set(v != 0), v => apply(v != 0))
            : Refused();

    /// <summary>A slider from <paramref name="min" /> to <paramref name="max" />.</summary>
    /// <param name="min">The lowest value.</param>
    /// <param name="max">The highest value.</param>
    /// <param name="engine">The value that leaves the game's behaviour; within min..max.</param>
    /// <param name="get">The player's value.</param>
    /// <param name="set">Stores the player's value; the mod saves it.</param>
    /// <param name="apply">What the patches read; <paramref name="engine" /> while held off or stood down.</param>
    /// <param name="unit">Shown after the value, e.g. <c>ms</c>.</param>
    /// <returns>The knob.</returns>
    public static KapiKnob Range(int min, int max, int engine, Func<int> get, Action<int> set, Action<int> apply,
        string unit = "") =>
        NotNull(get) && NotNull(set) && NotNull(apply) && NotNull(unit)
            ? new KapiKnob(min, max, engine, unit, get, set, apply)
            : Refused();

    // A knob with a null argument: its empty range makes the registration refuse it
    private static KapiKnob Refused() => new(0, 0, 0, "", static () => 0, static _ => { }, static _ => { });
}

/// <summary>
///     A feature of the mod's own, for <see cref="KapiFeatures.Register" />. Komet shows it in its options, installs it
///     in order with its own, lets other mods hold it off and stands it down when another mod patches what it relies on.
/// </summary>
/// <remarks>
///     <para>
///         Its id is <c>modid:name</c>, also its Harmony id. Komet unpatches it at world close, after
///         <see cref="Uninstall" />, or when its code throws.
///     </para>
///     <para>
///         <see cref="Shaped" /> and <see cref="Fingerprint" /> pin the engine bodies the feature replaces: on any
///         difference it is not installed (<see cref="KapiFeatureState.EngineChanged" />). A patch of a kind in
///         <see cref="StandDownFor" /> that another mod puts on a <see cref="Watched" /> method stands it down.
///     </para>
/// </remarks>
/// <example>
///     <code language="csharp">
/// komet.Features.Register(new KapiFeature("FastLeaves", "Fast leaves")
/// {
///     Hint = "Draws leaves with fewer faces.",
///     Page = KapiFeatures.RenderPage,
///     Knob = KapiKnob.Switch(() =&gt; config.FastLeaves, v =&gt; config.FastLeaves = v, v =&gt; Patches.On = v),
///     Install = (harmony, log) =&gt; harmony.CreateClassProcessor(typeof(LeafPatches)).Patch(),
///     Watched = () =&gt; [AccessTools.Method(typeof(ChunkTesselator), "Tesselate")]
/// });
/// </code>
/// </example>
/// <param name="name">The feature's name, unique within the mod.</param>
/// <param name="title">Shown in Komet's options; may be a lang key.</param>
public sealed class KapiFeature(string name, string title)
{
    /// <summary>The feature's name, unique within the mod.</summary>
    public string Name { get; } = NotNull(name) ? name : "";

    /// <summary>The row title in Komet's options.</summary>
    public string Title { get; } = NotNull(title) ? title : "";

    /// <summary>Shown in the detail panel of the feature's row.</summary>
    public string? Hint { get; init; }

    /// <summary>
    ///     Where the row goes: <see cref="KapiFeatures.RenderPage" />, <see cref="KapiFeatures.ChunksPage" />,
    ///     <see cref="KapiFeatures.MiscPage" />, the <see cref="KapiPage.Id" /> of one of the mod's pages, or null for
    ///     no row.
    /// </summary>
    public string? Page { get; init; }

    /// <summary>A group title on the page the row joins; null adds it at the end.</summary>
    public string? Group { get; init; }

    /// <summary>The switch or slider; null for a feature that is always on while installed.</summary>
    public KapiKnob? Knob { get; init; }

    /// <summary>Puts the feature's patches on, with a Harmony instance under its id; main thread.</summary>
    public Action<Harmony, ILogger>? Install { get; init; }

    /// <summary>Undoes what <see cref="Install" /> did besides patching; Komet removes the patches itself.</summary>
    public Action? Uninstall { get; init; }

    /// <summary>The engine methods whose bodies <see cref="Fingerprint" /> pins.</summary>
    public Func<IReadOnlyList<MethodBase?>>? Shaped { get; init; }

    /// <summary>What <see cref="KapiFeatures.FingerprintOf" /> gave for <see cref="Shaped" />; 0 skips the check.</summary>
    public ulong Fingerprint { get; init; }

    /// <summary>Methods another mod must not patch (by <see cref="StandDownFor" />) while the feature runs.</summary>
    public Func<IReadOnlyList<MethodBase?>>? Watched { get; init; }

    /// <summary>The patch kinds on <see cref="Watched" /> that stand the feature down.</summary>
    public KapiPatchKinds StandDownFor { get; init; } = KapiPatchKinds.Replacing;

    /// <summary>It patches server code: single player only, <see cref="KapiFeatureState.NotApplicable" /> elsewhere.</summary>
    public bool ServerOnly { get; init; }
}

/// <summary>One feature as <see cref="KapiFeatures.Snapshot" /> found it.</summary>
public sealed class KapiFeatureInfo
{
    internal KapiFeatureInfo(string id, string title, string owner, KapiFeatureState state, bool canHoldOff) =>
        (Id, Title, Owner, State, CanHoldOff) = (id, title, owner, state, canHoldOff);

    /// <summary>The id: the name for Komet's features, <c>modid:name</c> for other mods'.</summary>
    public string Id { get; }

    /// <summary>The row title.</summary>
    public string Title { get; }

    /// <summary>The id of the mod the feature belongs to (<c>komet</c> for Komet's).</summary>
    public string Owner { get; }

    /// <summary>The state when the snapshot was taken.</summary>
    public KapiFeatureState State { get; }

    /// <summary>It has a switch, so <see cref="KapiFeatures.HoldOff" /> can hold it.</summary>
    public bool CanHoldOff { get; }

    /// <summary>The mod of the first hold in place, else null.</summary>
    public string? HeldBy { get; internal init; }

    /// <summary>The reason of the first hold in place, else null.</summary>
    public string? Reason { get; internal init; }
}

/// <summary>A change of a feature's state, from <see cref="KapiFeatures.StateChanged" />.</summary>
public sealed class KapiStateChangedEventArgs : EventArgs
{
    internal KapiStateChangedEventArgs(string id, KapiFeatureState previous, KapiFeatureState state) =>
        (Id, Previous, State) = (id, previous, state);

    /// <summary>The feature.</summary>
    public string Id { get; }

    /// <summary>Its state before.</summary>
    public KapiFeatureState Previous { get; }

    /// <summary>Its state now.</summary>
    public KapiFeatureState State { get; }
}

/// <summary>
///     A hold from <see cref="KapiFeatures.HoldOff" />: it keeps a feature at the game's own behaviour until released.
/// </summary>
public sealed class KapiHold : IDisposable
{
    internal KapiHold(string featureId, string modId, string reason, int feature) =>
        (FeatureId, ModId, Reason, Feature) = (featureId, modId, reason, feature);

    /// <summary>The held feature's id.</summary>
    public string FeatureId { get; }

    /// <summary>Why it holds; shown to the player.</summary>
    public string Reason { get; }

    /// <summary>The hold is in place: false once released, and for a refused hold.</summary>
    public bool IsHolding => FeatureHolds.Holding(this);

    // The holding mod, and the held feature's index (-1 for a refused hold)
    internal string ModId { get; }
    internal int Feature { get; }

    /// <summary>Lets the feature go back to the player's value. Later calls do nothing.</summary>
    public void Dispose() => Release();

    internal void Release() => _ = FeatureHolds.Release(this);
}

/// <summary>
///     Features, through <see cref="KapiMod.Features" />: the mod's own, holds on Komet's (and other mods'), and every
///     feature's state.
/// </summary>
public sealed class KapiFeatures
{
    /// <summary>Komet's options pages a <see cref="KapiFeature.Page" /> may name.</summary>
    public const string RenderPage = FeatureHub.RenderPage,
        ChunksPage = FeatureHub.ChunksPage,
        MiscPage = FeatureHub.MiscPage;

    private readonly HashSet<EventHandler<KapiStateChangedEventArgs>> _handlers = [];

    private readonly KapiMod _mod;

    internal KapiFeatures(KapiMod mod) => _mod = mod;

    /// <summary>
    ///     A feature was installed, switched, held or released, stood down or back. A handler that throws is logged once
    ///     and unsubscribed.
    /// </summary>
    public event EventHandler<KapiStateChangedEventArgs>? StateChanged
    {
        add
        {
            if (value is null || _handlers.Contains(value) || !_mod.Keep(() => Unsubscribe(value))) return;

            _ = _handlers.Add(value);
            FeatureHub.StateChanged += value;
        }
        remove => Unsubscribe(value);
    }

    /// <summary>
    ///     What <see cref="KapiFeature.Fingerprint" /> compares. Pin it in the mod's own test, so a game update that
    ///     changes the bodies fails there first. Needs no connection.
    /// </summary>
    /// <param name="methods">The engine methods, as <see cref="KapiFeature.Shaped" /> gives them.</param>
    /// <returns>The fingerprint; 0 for a missing or bodiless method.</returns>
    public static ulong FingerprintOf(IReadOnlyList<MethodBase?> methods) => FeatureHub.Fingerprint(methods);

    /// <summary>
    ///     Adds the mod's feature. Before Komet started it is installed after Komet's own; while Komet runs, at once;
    ///     after the world finished loading it is refused. It lasts until the world closes.
    /// </summary>
    /// <param name="feature">The feature.</param>
    /// <returns>False when refused; the log says why.</returns>
    public bool Register(KapiFeature feature)
    {
        if (!NotNull(feature) || !_mod.IsConnected || !FeatureHub.Register(_mod.ModId, feature)) return false;

        _mod.FeatureCount++;
        return true;
    }

    /// <summary>A feature's state.</summary>
    /// <param name="id">A Komet feature's name (or one of its switches), or <c>modid:name</c>.</param>
    /// <returns>The state; <see cref="KapiFeatureState.Unknown" /> when no feature matches.</returns>
    public static KapiFeatureState StateOf(string id) => FeatureHub.StateOf(id);

    /// <summary>Every feature in install order, Komet's first.</summary>
    /// <returns>A copy; it does not change later.</returns>
    public static IReadOnlyList<KapiFeatureInfo> Snapshot() => FeatureHub.Snapshot();

    /// <summary>
    ///     Keeps a feature at the game's own behaviour until the hold is disposed, the connection ends or the world
    ///     closes. A feature without a switch cannot be held.
    /// </summary>
    /// <example>
    ///     <code language="csharp">
    /// var hold = komet.Features.HoldOff("FrustumSweep", "Our shadow pass needs the game's culling");
    /// </code>
    /// </example>
    /// <param name="id">The feature, as for <see cref="StateOf" />.</param>
    /// <param name="reason">Why; shown to the player.</param>
    /// <returns>The hold; check <see cref="KapiHold.IsHolding" />.</returns>
    public KapiHold HoldOff(string id, string reason)
    {
        var made = FeatureHub.HoldOff(id, _mod.ModId, reason);
        return Owned.Kept(made, _mod, static (hold, mod) => mod.Keep(hold.Dispose, KapiKind.Hold) ? null : "closed",
                   out _) ??
               new KapiHold(id, _mod.ModId, reason, -1);
    }

    private void Unsubscribe(EventHandler<KapiStateChangedEventArgs>? value)
    {
        if (value is null || !_handlers.Remove(value)) return;

        FeatureHub.StateChanged -= value;
    }
}

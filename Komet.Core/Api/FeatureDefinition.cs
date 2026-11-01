using System.Reflection;
using HarmonyLib;

namespace Komet.Api;

/// <summary>
/// Kinds of Harmony patch, as flags. <see cref="FeatureDefinition.StandDownFor"/> names the kinds that make a
/// feature stand down when another mod puts one on a method the feature watches.
/// </summary>
[Flags]
public enum PatchKinds
{
    /// <summary>No patch kind.</summary>
    None = 0,
    /// <summary>A Harmony prefix: it may skip the original or change its arguments.</summary>
    Prefix = 1,
    /// <summary>A Harmony postfix: it runs after the original and may change the result.</summary>
    Postfix = 2,
    /// <summary>A Harmony transpiler: it rewrites the original's IL.</summary>
    Transpiler = 4,
    /// <summary>A Harmony finalizer: it runs after the original and sees its exception.</summary>
    Finalizer = 8,
    /// <summary>A Harmony inner prefix, on a call inside the original.</summary>
    InnerPrefix = 16,
    /// <summary>A Harmony inner postfix, on a call inside the original.</summary>
    InnerPostfix = 32,
    /// <summary>Every kind that changes what the original's body does.</summary>
    Body = Transpiler | InnerPrefix | InnerPostfix,
    /// <summary>Every kind that can replace the original: a prefix or a body change. The default of <see cref="FeatureDefinition.StandDownFor"/>.</summary>
    Replacing = Prefix | Body,
    /// <summary>Every patch kind.</summary>
    All = Replacing | Postfix | Finalizer
}

/// <summary>
/// Another mod's feature, for <see cref="KometFeatures.Register"/>. Komet shows it in its options, installs it in
/// order with its own, holds it off for other mods and stands it down when another mod patches what it relies on.
/// </summary>
/// <remarks>
/// <para><see cref="Id"/> (<c>modid:name</c>) is also the feature's Harmony id. Komet unpatches it at world close,
/// after <see cref="Uninstall"/>, or when its code throws.</para>
/// <para><see cref="Shaped"/> and <see cref="Fingerprint"/> pin the engine bodies the feature replaces: on any
/// difference it is not installed (<see cref="FeatureState.EngineChanged"/>).</para>
/// <para>A patch of a kind in <see cref="StandDownFor"/> that another mod puts on a <see cref="Watched"/> method
/// stands the feature down: its knob is held at the engine value until an install or LevelFinalize finds the
/// methods free again. <see cref="Watched"/> needs a <see cref="Knob"/>.</para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// KometFeatures.Register(new FeatureDefinition("mymod", "FastLeaves", "Fast leaves")
/// {
///     Hint = "Draws leaves with fewer faces.",
///     Page = KometFeatures.RenderPage,
///     Knob = FeatureKnob.Switch(() =&gt; config.FastLeaves, v =&gt; config.FastLeaves = v, v =&gt; Patches.FastLeaves = v),
///     Install = (harmony, log) =&gt; harmony.PatchAll(typeof(LeafPatches)),
///     Watched = () =&gt; [AccessTools.Method(typeof(ChunkTesselator), "Tesselate")]
/// });
/// </code>
/// </example>
/// <param name="modId">The registering mod's id.</param>
/// <param name="name">The feature's name, unique within the mod.</param>
/// <param name="title">Shown in Komet's options; may be a lang key.</param>
public sealed class FeatureDefinition(string modId, string name, string title)
{
    /// <summary>The registering mod's id.</summary>
    public string ModId { get; } = NotNull(modId) ? modId : "";
    /// <summary>The feature's name, unique within the mod.</summary>
    public string Name { get; } = NotNull(name) ? name : "";
    /// <summary>The row title in Komet's options.</summary>
    public string Title { get; } = NotNull(title) ? title : "";
    /// <summary><c>modid:name</c>: the id for <see cref="KometFeatures.StateOf"/> and <see cref="KometFeatures.HoldOff"/>, and the Harmony id.</summary>
    public string Id { get; } = modId + ":" + name;
    /// <summary>Shown in the detail panel of the feature's row.</summary>
    public string? Hint { get; init; }
    /// <summary>
    /// Where the row goes: <see cref="KometFeatures.RenderPage"/>, <see cref="KometFeatures.ChunksPage"/>,
    /// <see cref="KometFeatures.MiscPage"/>, the id of one of the mod's own <see cref="KometOptions"/> pages, or null
    /// for no row.
    /// </summary>
    public string? Page { get; init; }
    /// <summary>A group title on the page the row joins; null adds it at the end.</summary>
    public string? Group { get; init; }
    /// <summary>The switch or slider; null for a feature that is always on while installed.</summary>
    public FeatureKnob? Knob { get; init; }
    /// <summary>Puts the feature's patches on, with a Harmony instance under <see cref="Id"/>; run on the main thread.</summary>
    public Action<Harmony, ILogger>? Install { get; init; }
    /// <summary>Undoes what <see cref="Install"/> did besides patching; Komet removes the patches itself.</summary>
    public Action? Uninstall { get; init; }
    /// <summary>The engine methods whose bodies <see cref="Fingerprint"/> pins.</summary>
    public Func<IReadOnlyList<MethodBase?>>? Shaped { get; init; }
    /// <summary>What <see cref="KometFeatures.Fingerprint"/> gave for <see cref="Shaped"/> when the feature was written; 0 skips the check.</summary>
    public ulong Fingerprint { get; init; }
    /// <summary>Methods another mod must not patch (by <see cref="StandDownFor"/>) while the feature runs.</summary>
    public Func<IReadOnlyList<MethodBase?>>? Watched { get; init; }
    /// <summary>The patch kinds on <see cref="Watched"/> that stand the feature down; <see cref="PatchKinds.Replacing"/> by default.</summary>
    public PatchKinds StandDownFor { get; init; } = PatchKinds.Replacing;
    /// <summary>The feature patches server code: it needs the in-process server (single player) and is <see cref="FeatureState.NotApplicable"/> on a remote one.</summary>
    public bool ServerOnly { get; init; }
}

/// <summary>
/// A feature's switch or slider. <c>get</c> and <c>set</c> carry the player's value, which the mod saves.
/// <c>apply</c> gets what the patches should read: the player's value, or <see cref="Engine"/> while the feature is
/// held off or stood down; it is never saved. Main thread only.
/// </summary>
public sealed class FeatureKnob
{
    private FeatureKnob(int min, int max, int engine, string unit, Func<int> get, Action<int> set, Action<int> apply)
    {
        (Min, Max, Engine, Unit, Get, Set, Apply) = (min, max, engine, unit, get, set, apply);
    }

    /// <summary>The lowest value.</summary>
    public int Min { get; }
    /// <summary>The highest value.</summary>
    public int Max { get; }
    /// <summary>The value that leaves the game's own behaviour.</summary>
    public int Engine { get; }
    /// <summary>Shown after a slider's value, e.g. <c>ms</c>.</summary>
    public string Unit { get; }
    /// <summary>True for a switch: 0 and 1, engine 0.</summary>
    public bool IsSwitch => Min == 0 && Max == 1;
    internal Func<int> Get { get; }
    internal Action<int> Set { get; }
    internal Action<int> Apply { get; }
    internal bool Valid => Min < Max && Engine >= Min && Engine <= Max;

    /// <summary>A switch: on (1) or off (0, the engine value).</summary>
    /// <param name="get">The player's value.</param>
    /// <param name="set">Stores the player's value; the mod saves it.</param>
    /// <param name="apply">What the patches read; false while held off or stood down.</param>
    /// <returns>The knob; a refused one when an argument is null.</returns>
    public static FeatureKnob Switch(Func<bool> get, Action<bool> set, Action<bool> apply) =>
        NotNull(get) && NotNull(set) && NotNull(apply)
            ? new FeatureKnob(0, 1, 0, "", () => get() ? 1 : 0, v => set(v != 0), v => apply(v != 0))
            : Refused();

    /// <summary>A slider from <paramref name="min"/> to <paramref name="max"/>.</summary>
    /// <param name="min">The lowest value.</param>
    /// <param name="max">The highest value.</param>
    /// <param name="engine">The value that leaves the game's behaviour; within min..max.</param>
    /// <param name="get">The player's value.</param>
    /// <param name="set">Stores the player's value; the mod saves it.</param>
    /// <param name="apply">What the patches read; <paramref name="engine"/> while held off or stood down.</param>
    /// <param name="unit">Shown after the value, e.g. <c>ms</c>.</param>
    /// <returns>The knob; a refused one when an argument is null.</returns>
    public static FeatureKnob Range(int min, int max, int engine, Func<int> get, Action<int> set, Action<int> apply,
        string unit = "") =>
        NotNull(get) && NotNull(set) && NotNull(apply) && NotNull(unit)
            ? new FeatureKnob(min, max, engine, unit, get, set, apply)
            : Refused();

    private static FeatureKnob Refused() => new(0, 0, 0, "", static () => 0, static _ => { }, static _ => { });
}

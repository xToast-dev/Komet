using System.Reflection;
using HarmonyLib;

namespace Komet.Api;

// Harmony's patch kinds, for FeatureDefinition.StandDownFor. Body changes what the original computes; Replacing adds the prefix that
// may skip it or change its arguments: what a feature that replaces a method has to stand down for.
[Flags]
public enum PatchKinds
{
    None = 0,
    Prefix = 1,
    Postfix = 2,
    Transpiler = 4,
    Finalizer = 8,
    InnerPrefix = 16,
    InnerPostfix = 32,
    Body = Transpiler | InnerPrefix | InnerPostfix,
    Replacing = Prefix | Body,
    All = Replacing | Postfix | Finalizer
}

// Another mod's feature, for KometFeatures.Register. Id is modid:name (a lower-case mod id; letters, digits, _ and - in the name), also
// its Harmony id: Install patches through the Harmony it is handed, and Komet unpatches that id when the world closes (after Uninstall)
// or when anything of the mod's throws (the feature then fails, Komet carries on). Shaped and Fingerprint pin the engine bodies the
// feature replaces (KometFeatures.Fingerprint): on a difference it is not installed. Watched are the methods it skips or bypasses:
// another mod's patch of a StandDownFor kind there stands it down (its knob held at the engine value) until a later check, at install
// and LevelFinalize, finds them free. Page places the knob's row: RenderPage, ChunksPage or MiscPage of KometFeatures, the id of one of
// the mod's own KometOptions pages, or null for none; Group heads the row, Hint describes it; both are shown as given. ServerOnly:
// installed only when the server runs in this process.
public sealed class FeatureDefinition(string modId, string name, string title)
{
    public string ModId { get; } = NotNull(modId) ? modId : "";
    public string Name { get; } = NotNull(name) ? name : "";
    public string Title { get; } = NotNull(title) ? title : "";
    public string Id { get; } = modId + ":" + name;
    public string? Hint { get; init; }
    public string? Page { get; init; }
    public string? Group { get; init; }
    public FeatureKnob? Knob { get; init; }
    public Action<Harmony, ILogger>? Install { get; init; }
    public Action? Uninstall { get; init; }
    public Func<IReadOnlyList<MethodBase?>>? Shaped { get; init; }
    public ulong Fingerprint { get; init; }
    public Func<IReadOnlyList<MethodBase?>>? Watched { get; init; }
    public PatchKinds StandDownFor { get; init; } = PatchKinds.Replacing;
    public bool ServerOnly { get; init; }
}

// A feature's switch or slider. get and set are the player's value, which the mod saves (Komet's row calls set); apply is what the
// mod's patches read: the player's value, or the engine value while the feature is held off or stood down; never saved. A switch is
// 1 or 0, its engine value 0. A bench arm sets the knob as modid:name. Main thread only.
public sealed class FeatureKnob
{
    private FeatureKnob(int min, int max, int engine, string unit, Func<int> get, Action<int> set, Action<int> apply)
    {
        (Min, Max, Engine, Unit, Get, Set, Apply) = (min, max, engine, unit, get, set, apply);
        _ = NotNull(unit) && NotNull(apply);
    }

    public int Min { get; }
    public int Max { get; }
    public int Engine { get; } // the value that leaves the game's own behaviour
    public string Unit { get; }
    public bool IsSwitch => Min == 0 && Max == 1;
    internal Func<int> Get { get; }
    internal Action<int> Set { get; }
    internal Action<int> Apply { get; }
    // Register refuses a feature with an invalid knob
    internal bool Valid => Min < Max && Engine >= Min && Engine <= Max;

    public static FeatureKnob Switch(Func<bool> get, Action<bool> set, Action<bool> apply)
    {
        if (!NotNull(get) || !NotNull(set) || !NotNull(apply)) return Refused();
        return new FeatureKnob(0, 1, 0, "", () => get() ? 1 : 0, v => set(v != 0), v => apply(v != 0));
    }

    // engine lies in min..max; unit follows the value in the row (ms)
    public static FeatureKnob Range(int min, int max, int engine, Func<int> get, Action<int> set, Action<int> apply,
        string unit = "")
    {
        if (!NotNull(get) || !NotNull(set) || !NotNull(apply) || !NotNull(unit)) return Refused();
        return new FeatureKnob(min, max, engine, unit, get, set, apply);
    }

    private static FeatureKnob Refused()
    {
        var knob = new FeatureKnob(0, 0, 0, "", static () => 0, static _ => { }, static _ => { });
        _ = Assert(!knob.Valid) && Assert(!knob.IsSwitch);
        return knob;
    }
}

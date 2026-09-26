using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// The table's lifecycle, the features' states and the holds, on the main thread; other mods' features follow Komet's in the same
// index space (Features.External). A hold keeps a feature's knobs at their engine values whatever the player or a bench arm writes
// (Knobs.Write), counted per feature; the last release writes back what was wanted meanwhile. Poll re-evaluates every state after each
// install stage, the recheck, a hold, a knob write and on the 250 ms tick, and tells KometFeatures.StateChanged what changed; the
// HUD's rows are translated when first read after a change, so drawing them allocates nothing otherwise.
internal static partial class Features
{
    public const int MaxFeatures = 64, MaxHolds = 64, MaxShown = 16;
    private const int MaxId = 128;

    private static readonly bool[] Installed = new bool[MaxFeatures];
    private static readonly int[] Holds = new int[MaxFeatures];
    private static readonly FeatureState[] States = new FeatureState[MaxFeatures];
    private static readonly string?[] ShownText = new string?[MaxShown];
    private static readonly int[] ShownFeatures = [.. Enumerable.Repeat(-1, MaxShown)]; // -1: an empty row
    private static readonly FeatureState[] ShownStates = new FeatureState[MaxShown];
    private static bool _remote; // the server runs elsewhere: server features stay out
    private static bool _stale = true; // the rows' text is not that of ShownStates
    private static string? _locale; // the language the rows were translated to

    // Komet's own features, in install order
    public static ReadOnlySpan<Feature> All => Table;

    // Features not active, what the HUD's features section counts
    public static int NotActive { get; private set; }

    // Every built-in feature of the stage in table order. One that throws propagates: Komet stands down as a whole.
    public static void Install(FeatureContext context, FeatureStage stage)
    {
        if (!NotNull(context) || !Assert(Table.Length <= MaxFeatures)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[i].Stage == stage)
                InstallAt(context, i);
        Poll();
    }

    // A server feature on a remote server is marked installed without its patches: not applicable from then on
    internal static void InstallAt(FeatureContext context, int feature)
    {
        if (!NotNull(context) || !Index(feature, Count)) return;
        _remote = !context.LocalServer;
        if (!At(feature).ServerOnly || context.LocalServer) At(feature).Install?.Invoke(context);
        Installed[feature] = true;
    }

    // LevelFinalize: every mod has started and patched, so a patch applied after Komet installed is seen now; registrations end
    public static void Recheck()
    {
        if (!Assert(Count <= MaxFeatures) || !Assert(Installed.Length == MaxFeatures)) return;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Installed[i])
                At(i).Recheck?.Invoke();
        _sealed = true;
        Poll();
    }

    // Before UnpatchAll, in reverse install order: the late stages with PreJit first, other mods' features, then Komet's main stage.
    // Every feature, installed or not: a stand-down after a throw clears them all too.
    public static void Stop(FeatureContext? context)
    {
        if (!Assert(Table.Length <= MaxFeatures) || !Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage != FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Added.Count, MaxExternal); i++) Added[^(i + 1)].Feature.Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage == FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
    }

    // After UnpatchAll; the registry then forgets the world
    public static void Unpatched()
    {
        if (!Assert(Table.Length <= MaxFeatures) || !Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++) Table[i].Unpatched?.Invoke();
        Close();
    }

    // The world closes, silently: every hold ends and writes its wanted values back, other mods' features go, nothing is installed
    internal static void Close()
    {
        if (!Assert(Holds.Length == MaxFeatures) || !Assert(Count <= MaxFeatures)) return;
        KometFeatures.Forget();
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
            {
                Holds[i] = 1;
                _ = Release(i);
            }

        Drop();
        Array.Clear(Installed);
        _remote = false;
        Poll();
    }

    public static void Poll()
    {
        if (!Assert(Count <= MaxFeatures) || !Assert(States.Length == MaxFeatures)) return;
        var (changed, off) = (false, 0);
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            var (state, previous) = (Evaluate(i), States[i]);
            if (state != Active) off++;
            if (state == previous) continue;
            (States[i], changed) = (state, true);
            KometFeatures.Raise(At(i).Id, previous, state);
        }

        NotActive = off;
        if (changed) Rows();
    }

    // The first that applies: not installed, server code on a remote server, what the feature's probe finds, a hold, every knob at its
    // engine value
    internal static FeatureState Evaluate(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] >= 0)) return Unknown;
        var f = At(feature);
        if (!Installed[feature]) return Pending;
        if (f.ServerOnly && _remote) return NotApplicable;
        if (f.Probe?.Invoke() is { } probed && probed != Active) return probed;
        if (Holds[feature] > 0) return HeldOff;
        if (f.Knobs.Length == 0) return Active;
        foreach (var knob in f.Knobs.Bounded(Knobs.MaxKnobs))
            if (knob.Get() != knob.Engine)
                return Active;
        return Off;
    }

    // The HUD's rows: the features that are not active, in table order
    private static void Rows()
    {
        var shown = 0;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (States[i] != Active && shown < MaxShown)
            {
                (ShownFeatures[shown], ShownStates[shown]) = (i, States[i]);
                shown++;
            }

        for (var i = shown; i < MaxShown; i++) (ShownFeatures[i], ShownStates[i]) = (-1, Active);
        _stale = true;
        _ = Assert(shown <= MaxShown) && Assert(ShownFeatures.Length == MaxShown);
    }

    // Their text, when first read after a change or a change of language
    private static void Translate()
    {
        for (var i = 0; i < MaxShown; i++)
            ShownText[i] = ShownFeatures[i] is var f and >= 0 && Assert(f < Count)
                ? At(f).Id + ": " + StateText(ShownStates[i])
                : null;
        (_stale, _locale) = (false, Lang.CurrentLocale);
        _ = Assert(ShownText.Length == MaxShown);
    }

    private static string StateText(FeatureState state)
    {
        var key = state switch
        {
            Pending => "hud-feature-pending",
            Active => "hud-feature-active",
            Off => "hud-feature-off",
            HeldOff => "hud-feature-held",
            StoodDown => "hud-feature-stooddown",
            EngineChanged => "hud-feature-changed",
            NotApplicable => "hud-feature-notapplicable",
            _ => "hud-feature-failed"
        };
        return Assert(state != Unknown) && Assert(key.Length > 0) ? HudText.Translate(key) : "";
    }

    // Row i of the HUD's features section, empty past the last
    public static string Shown(int row)
    {
        if (_stale || !ReferenceEquals(_locale, Lang.CurrentLocale)) Translate();
        return Index(row, MaxShown) && Assert(ShownText.Length == MaxShown) ? ShownText[row] ?? "" : "";
    }

    public static FeatureState ShownState(int row)
    {
        return Index(row, MaxShown) && Assert(ShownStates.Length == MaxShown) ? ShownStates[row] : Active;
    }

    // The feature id, or the key of one of its knobs (UploadCap: ChunkBudget); -1 for neither
    public static int Find(string id)
    {
        if (!NotNull(id) || !Assert(Count <= MaxFeatures)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (string.Equals(At(i).Id, id, StringComparison.Ordinal))
                return i;
        var knob = Knobs.Find(id);
        return knob >= 0 ? Knobs.At(knob).Owner : -1;
    }

    // false: the feature has no knob to hold, or is held too often already
    internal static bool Hold(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] < MaxHolds)) return false;
        var knobs = At(feature).Knobs;
        if (knobs.Length == 0) return false;
        if (Holds[feature]++ == 0)
            foreach (var knob in knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Park(knob.Order);
        Poll();
        return true;
    }

    // false: the feature was not held
    internal static bool Release(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] >= 0) || Holds[feature] == 0) return false;
        if (--Holds[feature] == 0)
            foreach (var knob in At(feature).Knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Restore(knob.Order);
        Poll();
        return true;
    }

    public static bool Held(int knob)
    {
        return Index(knob, Knobs.Count) && Index(Knobs.At(knob).Owner, Count) && Holds[Knobs.At(knob).Owner] > 0;
    }

    // Shown under a held knob's option, which takes no input meanwhile, with the first holder and its reason; null while it is free
    public static string? LockText(int knob)
    {
        if (!Index(knob, Knobs.Count) || !Held(knob)) return null;
        var text = HudText.Translate("settings-held");
        if (Holder(Knobs.At(knob).Owner) is { } hold) text += "\n" + hold.ModId + ": " + hold.Reason;
        return Assert(text.Length > 0) ? text : null;
    }

    // The bench result's komet.holds: the held features, "none" without
    public static string HoldsText()
    {
        var text = new StringBuilder();
        if (!Assert(Count <= MaxFeatures) || !Assert(Holds.Length == MaxFeatures)) return "";
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
                _ = text.Append(text.Length > 0 ? ", " : "").Append(At(i).Id)
                    .Append(CultureInfo.InvariantCulture, $" ({Holds[i]})");
        return text.Length > 0 ? text.ToString() : "none";
    }

    // modid:name, how another mod's feature and knob are named (a bench arm may set them)
    public static bool IsExternalId(string id)
    {
        if (!NotNull(id) || id.Length > MaxId) return false;
        var match = ExternalId().IsMatch(id);
        return Assert(!match || id.IndexOf(':', StringComparison.Ordinal) > 0) && match;
    }

    [GeneratedRegex("^[a-z0-9]+:[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExternalId();
}

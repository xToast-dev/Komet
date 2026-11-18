using Komet.Api;
using static Komet.Api.KapiFeatureState;

namespace Komet.Core;

// Each feature's state from the lifecycle, the guards, the holds and its knobs; Poll compares with the last and tells the
// public API's listeners. Main thread only.
internal static class FeatureStates
{
    private static readonly KapiFeatureState[] States = new KapiFeatureState[FeatureTable.MaxFeatures];
    private static string? _firstTroubled;

    public static int Troubled { get; private set; }

    public static void Poll()
    {
        var (changed, troubled) = (false, 0);
        for (var i = 0; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            var (state, previous) = (Evaluate(i), States[i]);
            if (IsTrouble(state)) troubled++;

            if (state == previous) continue;

            (States[i], changed) = (state, true);
            FeatureHub.Raise(FeatureTable.At(i).Id, previous, state);
        }

        Troubled = troubled;
        if (changed) _firstTroubled = null;
    }

    private static bool IsTrouble(KapiFeatureState state) => state is StoodDown or EngineChanged or Failed or Unknown;

    public static KapiFeatureState Evaluate(int feature)
    {
        if (!Index(feature, FeatureTable.Count)) return Unknown;

        var f = FeatureTable.At(feature);
        if (!FeatureLifecycle.IsInstalled(feature)) return Pending;

        if (f.ServerOnly && FeatureLifecycle.Remote) return NotApplicable;

        if (f.Guard.Probe?.Invoke() is { } probed && probed != Active) return probed;

        if (FeatureGuards.Rivalled(feature)) return StoodDown;

        if (FeatureHolds.Count(feature) > 0) return HeldOff;

        return f.Knobs.Length == 0 || Moved(f.Knobs) ? Active : Off;
    }

    // A knob away from the engine's value: the feature acts
    private static bool Moved(Knob[] knobs)
    {
        foreach (var knob in knobs.Bounded(Knobs.MaxKnobs))
        {
            if (knob.Get() != knob.Engine) return true;
        }

        return false;
    }

    // For the HUD's hint, kept until a state changes; "" when nothing is troubled
    public static string FirstTroubled()
    {
        if (_firstTroubled is { } text) return text;

        _firstTroubled = "";
        for (var i = 0; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            if (!IsTrouble(States[i])) continue;

            var key = States[i] switch
            {
                StoodDown => "hud-feature-stooddown",
                EngineChanged => "hud-feature-changed",
                _ => "hud-feature-failed"
            };
            _firstTroubled = FeatureTable.At(i).Id + ": " + KometText.Translate(key);
            break;
        }

        return _firstTroubled;
    }

    // A slot another mod's feature takes starts unknown
    internal static void Reset(int feature)
    {
        if (Index(feature, FeatureTable.MaxFeatures)) States[feature] = Unknown;
    }

    // The other mods' slots go with their world
    internal static void ClearExternal() =>
        Array.Clear(States, FeatureTable.BuiltInCount, FeatureTable.MaxFeatures - FeatureTable.BuiltInCount);

    internal static KapiFeatureInfo[] Snapshot()
    {
        var infos = new KapiFeatureInfo[Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures)];
        for (var i = 0; i < Math.Min(infos.Length, FeatureTable.MaxFeatures); i++)
        {
            infos[i] = Info(i);
        }

        return infos;
    }

    private static KapiFeatureInfo Info(int feature)
    {
        if (!Index(feature, FeatureTable.Count)) return new KapiFeatureInfo("?", "?", "?", Unknown, false);

        var external = ExternalFeatures.Of(feature);
        var (f, hold) = (FeatureTable.At(feature), FeatureHolds.Holder(feature));
        var (title, owner) = (external?.Definition.Title ?? f.Id, external?.ModId ?? KometMod.Id);
        return new KapiFeatureInfo(f.Id, title, owner, Evaluate(feature), f.Knobs.Length > 0)
            { HeldBy = hold?.ModId, Reason = hold?.Reason };
    }
}

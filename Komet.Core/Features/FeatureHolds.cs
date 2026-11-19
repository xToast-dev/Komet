using System.Globalization;
using System.Text;
using Komet.Api;

namespace Komet.Core;

// Counted holds that keep a feature's knobs at the engine's value: other mods' (KapiHold handles), the rival guards'
// stand-downs and the mod adapters'. The first hold parks the knobs, the last release restores the wanted values.
// Main thread only.
internal static class FeatureHolds
{
    private const int MaxHolds = 96;

    private static readonly int[] Holds = new int[FeatureTable.MaxFeatures];
    private static readonly List<KapiHold> Handles = [];

    public static int Count(int feature) => Index(feature, FeatureTable.MaxFeatures) ? Holds[feature] : 0;

    public static bool Hold(int feature)
    {
        if (!Index(feature, FeatureTable.Count) || !Assert(Holds[feature] < MaxHolds)) return false;

        var knobs = FeatureTable.At(feature).Knobs;
        if (knobs.Length == 0) return false;

        if (Holds[feature]++ == 0)
        {
            foreach (var knob in knobs.Bounded(Knobs.MaxKnobs))
            {
                Knobs.Park(Knobs.Find(knob.Key));
            }
        }

        FeatureStates.Poll();
        return true;
    }

    public static bool Release(int feature)
    {
        if (!Index(feature, FeatureTable.Count) || Holds[feature] == 0) return false;

        if (--Holds[feature] == 0)
        {
            foreach (var knob in FeatureTable.At(feature).Knobs.Bounded(Knobs.MaxKnobs))
            {
                Knobs.Restore(Knobs.Find(knob.Key));
            }
        }

        FeatureStates.Poll();
        return true;
    }

    // A refused hold (feature -1, e.g. the server thread's) holds nothing; off the main thread Handles is not ours to change
    public static bool Release(KapiHold hold) =>
        NotNull(hold) && hold.Feature >= 0 && !FeatureLog.OffMain("releasing", hold.FeatureId) &&
        Handles.Remove(hold) && Release(hold.Feature);

    public static bool Held(int knob) =>
        Index(knob, Knobs.Count) && Index(Knobs.At(knob).Owner, FeatureTable.Count) &&
        Holds[Knobs.At(knob).Owner] > 0;

    public static string? LockText(int knob)
    {
        if (!Index(knob, Knobs.Count) || !Held(knob)) return null;

        var text = KometText.Translate("settings-held");
        if (Holder(Knobs.At(knob).Owner) is { } hold) text += "\n" + hold.ModId + ": " + hold.Reason;

        return text;
    }

    public static string HoldsText()
    {
        var text = new StringBuilder();
        for (var i = 0; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            if (Holds[i] > 0)
            {
                _ = text.Append(text.Length > 0 ? ", " : "").Append(FeatureTable.At(i).Id)
                    .Append(CultureInfo.InvariantCulture, $" ({Holds[i]})");
            }
        }

        return text.Length > 0 ? text.ToString() : "none";
    }

    // Knob features only; one of MaxHolds is reserved for a stand-down
    public static KapiHold HoldOff(string id, string modId, string reason)
    {
        if (!NotNull(id) || FeatureLog.OffMain("holding off", id)) return new KapiHold("", modId, reason, -1);

        var feature = FeatureTable.Find(id);
        var hold = new KapiHold(feature >= 0 ? FeatureTable.At(feature).Id : id, modId, reason, feature);
        string? refused = null;
        if (feature < 0)
        {
            refused = "it is no feature";
        }
        else if (FeatureTable.At(feature).Knobs.Length == 0)
        {
            refused = "it has no switch";
        }
        else if (Handles.Count >= MaxHolds - 1)
        {
            refused = "it is held too often";
        }

        if (refused is not null)
        {
            FeatureLog.Log?.Warning("Komet: {0} cannot hold off {1}, {2}", modId, id, refused);
        }
        else
        {
            Handles.Add(hold);
            if (!Hold(feature)) _ = Handles.Remove(hold);
        }

        return hold;
    }

    public static bool Holding(KapiHold hold) => NotNull(hold) && Handles.Contains(hold);

    public static KapiHold? Holder(int feature)
    {
        foreach (var hold in Handles.Bounded(MaxHolds))
        {
            if (hold.Feature == feature) return hold;
        }

        return null;
    }

    // A slot another mod's feature takes starts free
    internal static void Reset(int feature)
    {
        if (Index(feature, FeatureTable.MaxFeatures)) Holds[feature] = 0;
    }

    // Silently at world close: every hold ends at once and its knobs get their wanted values back
    internal static void EndAll()
    {
        for (var i = 0; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            if (Holds[i] > 0)
            {
                Holds[i] = 1;
                _ = Release(i);
            }
        }
    }

    // The other mods' slots and every handle go with their world
    internal static void ClearExternal()
    {
        Array.Clear(Holds, FeatureTable.BuiltInCount, FeatureTable.MaxFeatures - FeatureTable.BuiltInCount);
        Handles.Clear();
    }
}

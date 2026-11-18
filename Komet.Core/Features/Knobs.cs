namespace Komet.Core;

// Komet's knobs in dialog order, then other mods' (per world). Write is the one path to a feature static: a held knob
// stays at engine value and writes become the wanted value.
internal static class Knobs
{
    public const int MaxKnobs = 160;

    private static Knob[] _table = []; // collected by FeatureTable.Use
    private static readonly List<Knob> Added = [];
    private static readonly int[] Wanted = new int[MaxKnobs];

    public static int BuiltInCount => _table.Length; // What komet.json saves and the settings pages show

    public static int Count => _table.Length + Added.Count;

    public static ReadOnlySpan<Knob> BuiltIn => _table;

    internal static void Collect(ReadOnlySpan<Feature> features)
    {
        var table = new List<Knob>(MaxKnobs);
        for (var f = 0; f < Math.Min(features.Length, FeatureTable.MaxFeatures); f++)
        {
            foreach (var knob in features[f].Knobs.Bounded(MaxKnobs))
            {
                if (Assert(table.Count < MaxKnobs) && Assert(table.TrueForAll(k => k.Key != knob.Key)))
                {
                    table.Add(knob with { Owner = f });
                }
            }
        }

        _table = [.. table];
    }

    public static Knob At(int knob)
    {
        if (!Index(knob, Count)) return _table[0];

        return knob < _table.Length ? _table[knob] : Added[knob - _table.Length];
    }

    public static string Name(int knob) => Index(knob, Count) ? At(knob).Key : "";

    public static int Find(string key)
    {
        if (!NotNull(key)) return -1;

        for (var i = 0; i < Math.Min(Count, MaxKnobs); i++)
        {
            if (string.Equals(At(i).Key, key, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    public static bool InRange(int knob, int value) =>
        Index(knob, Count) && value >= At(knob).Min && value <= At(knob).Max;

    // held: wanted value; engine: Engine values; builtIn: Komet's knobs alone
    public static int[] Snapshot(bool engine = false, bool builtIn = false)
    {
        var values = new int[builtIn ? _table.Length : Count];
        if (engine)
        {
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
            {
                values[i] = At(i).Engine;
            }
        }
        else
        {
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
            {
                values[i] = FeatureHolds.Held(i) ? Wanted[i] : At(i).Get();
            }
        }

        return values;
    }

    // Only differences: ShaderUseCache.Enabled = true empties its cache even when already true.
    public static int Apply(ReadOnlySpan<int> values)
    {
        if (!Assert(values.Length == Count)) return 0;

        var changed = 0;
        for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
        {
            if (InRange(i, values[i]) && Write(i, values[i])) changed++;
        }

        FeatureStates.Poll();
        return changed;
    }

    public static bool Write(int knob, int value)
    {
        if (!Index(knob, Count)) return false;

        if (FeatureHolds.Held(knob))
        {
            Wanted[knob] = value; // until the last hold goes
            return false;
        }

        if (At(knob).Get() == value) return false;

        At(knob).Set(value);
        return true;
    }

    internal static void Park(int knob)
    {
        if (!Index(knob, Count) || !Assert(At(knob).Engine >= At(knob).Min)) return;

        Wanted[knob] = At(knob).Get();
        if (Wanted[knob] != At(knob).Engine) At(knob).Set(At(knob).Engine);
    }

    internal static void Restore(int knob)
    {
        if (!Index(knob, Count) || !Assert(InRange(knob, Wanted[knob]))) return;

        if (At(knob).Get() != Wanted[knob]) At(knob).Set(Wanted[knob]);
    }

    internal static int Add(Knob knob)
    {
        if (!NotNull(knob) || Count >= MaxKnobs) return -1;

        Added.Add(knob);
        return Count - 1;
    }

    internal static void Truncate() => Added.Clear();

    // Player value wanted; the mod's apply gets it once (engine value while held)
    internal static void Seed(int knob, int wanted)
    {
        if (!Index(knob, Count) || !Assert(knob >= _table.Length) || !InRange(knob, wanted)) return;

        Wanted[knob] = wanted;
        At(knob).Set(FeatureHolds.Held(knob) ? At(knob).Engine : wanted);
    }
}

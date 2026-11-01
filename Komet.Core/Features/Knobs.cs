namespace Komet.Core;

// A preset's value for a knob it governs (Options.Presets; "auto" is balanced or speed)
internal readonly record struct PresetValues(int Quality, int Balanced, int Speed);

// Page/Group: settings placement (null: bench only); label "settings-" + lower-case Key. Engine: the game's behaviour.
// Json: legacy key. Owner: feature index. Needs: the knob that must be on for this one to act (its row greys
// otherwise). Other mods' knobs: modid:name.
// Weighed: a trade-off (quality, memory or loading against speed) with a row of its own on its area's page; every other
// switch of a page only makes the engine's own result faster and is exact (listed under one master switch with
// "advanced"). General: a preference on the Overview beside the preset. Presets: the values the presets give it (the
// player's own choice wins). Suggest: the value this computer needs (HardwareProfile), null for Komet's default.
internal sealed record Knob(
    string Key, string? Page, string? Group, int Min, int Max, int Engine, Func<int> Get, Action<int> Set,
    string? Json = null)
{
    public string Persisted => Json ?? Key;
    public bool IsSwitch => Min == 0 && Max == 1;
    public string Unit { get; init; } = "";
    public int Owner { get; init; }
    public bool Weighed { get; init; }
    public bool General { get; init; }
    public string? Needs { get; init; }
    public PresetValues? Presets { get; init; }
    public System.Func<HardwareProfile.Facts, int?>? Suggest { get; init; }

    public string Label => Key.ToLowerInvariant();
}

// A settings range for the HUD's and the menu's sliders: Factor and Unit for the shown text (opacity 0..1 as 0..100 %)
internal readonly record struct ValueRange(double Min, double Max, double Step, double Factor, string Unit)
{
    public bool Contains(double value) => Finite(value) && value >= Min && value <= Max;

    public string Text(double value) => (KometText.Format(value * Factor, "N0") + " " + Unit).TrimEnd();
}

// Komet's knobs in dialog order, then other mods' (per world). Write is the one path to a feature static: a held knob
// stays at engine value and writes become the wanted value.
internal static class Knobs
{
    public const int MaxKnobs = 160;

    private static Knob[] Table = []; // collected by Features.Use
    private static readonly List<Knob> Added = [];
    private static readonly int[] Wanted = new int[MaxKnobs];

    public static int BuiltInCount => Table.Length; // What komet.json saves and the settings pages show

    public static int Count => Table.Length + Added.Count;

    public static ReadOnlySpan<Knob> BuiltIn => Table;

    internal static void Collect(ReadOnlySpan<Feature> features)
    {
        var table = new List<Knob>(MaxKnobs);
        for (var f = 0; f < Math.Min(features.Length, Features.MaxFeatures); f++)
            foreach (var knob in features[f].Knobs.Bounded(MaxKnobs))
                if (Assert(table.Count < MaxKnobs) && Assert(table.TrueForAll(k => k.Key != knob.Key)))
                    table.Add(knob with { Owner = f });
        Table = [.. table];
    }

    public static Knob At(int knob)
    {
        if (!Index(knob, Count)) return Table[0];
        return knob < Table.Length ? Table[knob] : Added[knob - Table.Length];
    }

    public static string Name(int knob) => Index(knob, Count) ? At(knob).Key : "";

    public static int Find(string key)
    {
        if (!NotNull(key)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxKnobs); i++)
            if (string.Equals(At(i).Key, key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public static bool InRange(int knob, int value) =>
        Index(knob, Count) && value >= At(knob).Min && value <= At(knob).Max;

    // held: wanted value; engine: Engine values; builtIn: Komet's knobs alone
    public static int[] Snapshot(bool engine = false, bool builtIn = false)
    {
        var values = new int[builtIn ? Table.Length : Count];
        if (engine)
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++) values[i] = At(i).Engine;
        else
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
                values[i] = Features.Held(i) ? Wanted[i] : At(i).Get();

        return values;
    }

    // Only differences: ShaderUseCache.Enabled = true empties its cache even when already true.
    public static int Apply(ReadOnlySpan<int> values)
    {
        if (!Assert(values.Length == Count)) return 0;
        var changed = 0;
        for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
            if (InRange(i, values[i]) && Write(i, values[i]))
                changed++;
        Features.Poll();
        return changed;
    }

    public static bool Write(int knob, int value)
    {
        if (!Index(knob, Count)) return false;
        if (Features.Held(knob))
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

    internal static void Truncate()
    {
        Added.Clear();
    }

    // Player value wanted; the mod's apply gets it once (engine value while held)
    internal static void Seed(int knob, int wanted)
    {
        if (!Index(knob, Count) || !Assert(knob >= Table.Length) || !InRange(knob, wanted)) return;
        Wanted[knob] = wanted;
        At(knob).Set(Features.Held(knob) ? At(knob).Engine : wanted);
    }
}

namespace Komet.Core;

// Page and Group place the knob in the settings dialog (null: bench only), its label is the lang key "settings-" + lower-case Key.
// Engine is the value that leaves the game's own behaviour. Json is the komet-hud.json key when it differs from Key (kept from before
// a rename, so saved settings survive). Order is the knob's index in the table, Owner that of its feature in Features. Another mod's
// knob is keyed modid:name and carries its definition's page id and group as given (Features.AddRows).
internal sealed record Knob(
    string Key, string? Page, string? Group, int Min, int Max, int Engine, Func<int> Get, Action<int> Set,
    string? Json = null)
{
    public string Persisted => Json ?? Key;
    public bool IsSwitch => Min == 0 && Max == 1;
    public string Unit { get; init; } = "";
    public int Order { get; init; }
    public int Owner { get; init; }

    public string Label => Key.ToLowerInvariant();
}

// The features' knobs: Komet's in dialog order, then other mods' in registration order (Add; gone again with the world, Truncate). Write
// is the one path to a feature static: a held knob stays at its engine value, and what is written meanwhile is kept as the wanted value
// that the last release writes.
internal static class Knobs
{
    public const int MaxKnobs = 64;

    private static readonly Knob[] Table = Collect();
    private static readonly List<Knob> Added = [];
    private static readonly int[] Wanted = new int[MaxKnobs];

    // What komet-hud.json saves and the settings pages show
    public static int BuiltInCount => Table.Length;

    // What the benchmark arms set and report: other mods' knobs too
    public static int Count => Table.Length + Added.Count;

    public static ReadOnlySpan<Knob> BuiltIn => Table;

    // Each feature's knobs at their Order, which runs 0..N-1 without a gap or a twin
    private static Knob[] Collect()
    {
        var features = Features.All;
        var table = new Knob?[MaxKnobs];
        for (var f = 0; f < Math.Min(features.Length, Features.MaxFeatures); f++)
            foreach (var knob in features[f].Knobs.Bounded(MaxKnobs))
                if (Index(knob.Order, MaxKnobs) && Assert(table[knob.Order] is null))
                    table[knob.Order] = knob with { Owner = f };
        var count = Array.FindLastIndex(table, k => k is not null) + 1;
        _ = Assert(Array.IndexOf(table, null, 0, count) < 0);
        return [.. table.OfType<Knob>()];
    }

    public static Knob At(int knob)
    {
        if (!Index(knob, Count) || !Assert(Count <= MaxKnobs)) return Table[0];
        return knob < Table.Length ? Table[knob] : Added[knob - Table.Length];
    }

    public static string Name(int knob) => Index(knob, Count) && Assert(Count <= MaxKnobs) ? At(knob).Key : "";

    public static int Find(string key)
    {
        if (!NotNull(key) || !Assert(Count <= MaxKnobs)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxKnobs); i++)
            if (string.Equals(At(i).Key, key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public static bool InRange(int knob, int value) =>
        Index(knob, Count) && Assert(At(knob).Min <= At(knob).Max) && value >= At(knob).Min && value <= At(knob).Max;

    // Every knob's current value (a held one's wanted value), or with engine its Engine value; builtIn: Komet's knobs alone
    public static int[] Snapshot(bool engine = false, bool builtIn = false)
    {
        var values = new int[builtIn ? Table.Length : Count];
        if (!Assert(values.Length <= MaxKnobs)) return values;
        if (engine)
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++) values[i] = At(i).Engine;
        else
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
                values[i] = Features.Held(i) ? Wanted[i] : At(i).Get();

        return values;
    }

    // Only what differs is written: ShaderUseCache.Enabled = true empties its cache even when it already was true, and a lap that
    // starts with a cold cache in one arm only would measure the toggle instead of the feature.
    public static int Apply(ReadOnlySpan<int> values)
    {
        if (!Assert(values.Length == Count) || !Assert(values.Length <= MaxKnobs)) return 0;
        var changed = 0;
        for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
            if (InRange(i, values[i]) && Write(i, values[i]))
                changed++;
        Features.Poll();
        return changed;
    }

    // Whether the static changed; only a difference is written (see Apply)
    public static bool Write(int knob, int value)
    {
        if (!Index(knob, Count) || !Assert(Count <= MaxKnobs)) return false;
        if (Features.Held(knob))
        {
            Wanted[knob] = value; // until the last hold goes
            return false;
        }

        if (At(knob).Get() == value) return false;
        At(knob).Set(value);
        return true;
    }

    // A feature's first hold: the value in place becomes the wanted one, the engine's is written
    internal static void Park(int knob)
    {
        if (!Index(knob, Count) || !Assert(At(knob).Engine >= At(knob).Min)) return;
        Wanted[knob] = At(knob).Get();
        if (Wanted[knob] != At(knob).Engine) At(knob).Set(At(knob).Engine);
    }

    // Its last release: the wanted value comes back
    internal static void Restore(int knob)
    {
        if (!Index(knob, Count) || !Assert(InRange(knob, Wanted[knob]))) return;
        if (At(knob).Get() != Wanted[knob]) At(knob).Set(Wanted[knob]);
    }

    // Another mod's knob, placed after the others; its index, -1 when the table is full
    internal static int Add(Knob knob)
    {
        if (!NotNull(knob) || !Assert(Added.Count < MaxKnobs) || Count >= MaxKnobs) return -1;
        Added.Add(knob with { Order = Count });
        return Count - 1;
    }

    // The world closes: other mods' knobs go
    internal static void Truncate()
    {
        Added.Clear();
        _ = Assert(Count == Table.Length) && Assert(Added.Count == 0);
    }

    // Another mod's knob at its install: the player's value is the wanted one, and the mod's apply is told once what to use, the
    // engine value while its feature is held
    internal static void Seed(int knob, int wanted)
    {
        if (!Index(knob, Count) || !Assert(knob >= Table.Length) || !InRange(knob, wanted)) return;
        Wanted[knob] = wanted;
        At(knob).Set(Features.Held(knob) ? At(knob).Engine : wanted);
    }
}

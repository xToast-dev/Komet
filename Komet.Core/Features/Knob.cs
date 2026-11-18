namespace Komet.Core;

// A preset's value for a knob it governs (Options.Presets; "auto" is balanced or speed)
internal readonly record struct PresetValues(int Quality, int Balanced, int Speed);

// Where a knob shows on the settings pages. Page/Group: its page and heading (Page null: bench only); its label is
// "settings-" + the lower-case key. Unit: the slider's unit. Weighed: a trade-off (quality, memory or loading against
// speed) with a row of its own on its area's page; every other switch of a page only makes the engine's own result
// faster and is exact (listed under one master switch with "advanced"). General: a preference on the Overview beside
// the preset. Needs: the knob that must be on for this one to act (its row greys otherwise).
internal sealed record KnobPlace(string? Page, string? Group)
{
    public string Unit { get; init; } = "";
    public bool Weighed { get; init; }
    public bool General { get; init; }
    public string? Needs { get; init; }
}

// What a knob the player never set follows. Presets are the values the presets give it, the player's own choice wins.
// Suggest names the value this computer needs (HardwareProfile), null for Komet's default.
internal readonly record struct KnobFollows(
    PresetValues? Presets,
    System.Func<HardwareProfile.Facts, int?>? Suggest);

// One value a feature exposes: its range, the game's own behaviour (Engine) and the feature static it reads and writes.
// Owner: the feature's index. Persisted: its key in komet.json (Json: a legacy key, else Key). Other mods' knobs are
// modid:name. Place and Follows describe the settings row and the defaults; the init properties below set their parts.
internal sealed record Knob
{
    public Knob(string key, string? page, string? group, int min, int max, int engine, Func<int> get, Action<int> set,
        string? json = null)
    {
        (Key, Min, Max, Engine, Get, Set, Persisted) = (key, min, max, engine, get, set, json ?? key);
        Place = new KnobPlace(page, group);
    }

    public string Key { get; }
    public int Min { get; }
    public int Max { get; }
    public int Engine { get; }
    public Func<int> Get { get; }
    public Action<int> Set { get; }
    public string Persisted { get; }
    public int Owner { get; init; }
    private KnobPlace Place { get; init; }
    private KnobFollows Follows { get; init; }

    public bool IsSwitch => Min == 0 && Max == 1;
    public string Label => Key.ToLowerInvariant();
    public string? Page => Place.Page;
    public string? Group => Place.Group;

    public string Unit
    {
        get => Place.Unit;
        init => Place = Place with { Unit = value };
    }

    public bool Weighed
    {
        get => Place.Weighed;
        init => Place = Place with { Weighed = value };
    }

    public bool General
    {
        get => Place.General;
        init => Place = Place with { General = value };
    }

    public string? Needs
    {
        get => Place.Needs;
        init => Place = Place with { Needs = value };
    }

    public PresetValues? Presets
    {
        get => Follows.Presets;
        init => Follows = Follows with { Presets = value };
    }

    public System.Func<HardwareProfile.Facts, int?>? Suggest
    {
        get => Follows.Suggest;
        init => Follows = Follows with { Suggest = value };
    }
}

// A settings range for the HUD's and the menu's sliders: Factor and Unit for the shown text (opacity 0..1 as 0..100 %)
internal readonly record struct ValueRange(double Min, double Max, double Step, double Factor, string Unit)
{
    public bool Contains(double value) => Finite(value) && value >= Min && value <= Max;

    public string Text(double value) => (KometText.Format(value * Factor, "N0") + " " + Unit).TrimEnd();
}

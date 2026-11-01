using System.Globalization;
using Komet.Core;

namespace Komet.Api;

/// <summary>
/// Komet's options window, which replaces the game's graphics settings. Mods add pages of their own.
/// </summary>
/// <remarks>
/// Add pages in <c>StartClientSide</c>, from a class of your own called only when
/// <c>api.ModLoader.IsModEnabled("komet")</c>: <see cref="Page"/>, then rows in order. Getters, setters, saving and
/// translation are the mod's. Pages go when the world closes. Main thread only.
/// </remarks>
/// <example>
/// <code language="csharp">
/// KometOptions.Page("mymod", Lang.Get("mymod:title"))
///     .Group("Look")
///     .Switch("Warm picture", () =&gt; config.Warm, v =&gt; config.Warm = v, hint: "Turns the warm pass on.")
///     .Slider("Warmth", 0, 1, 0.05, () =&gt; config.Warmth, v =&gt; config.Warmth = v).Default(0.7)
///     .Choice("Quality", ["Low", "High"], () =&gt; config.Quality, v =&gt; config.Quality = v)
///     .Button("Settings file", "Open", () =&gt; OpenFolder());
/// KometOptions.Applied += (_, _) =&gt; config.Save();
/// </code>
/// </example>
public static class KometOptions
{
    /// <summary>How many pages all mods together may add.</summary>
    public const int MaxPages = 32;
    private static readonly List<OptionPage> Registered = [];

    internal static int Count => Registered.Count;

    /// <summary>Raised after the player applied changes (Apply or Done): the moment to save or to act on several options at once. A handler that throws is logged once and unsubscribed.</summary>
    public static event EventHandler? Applied;

    /// <summary>A new, empty page. Reusing an id starts it over, so a rejoin does not double the rows.</summary>
    /// <param name="id">The page id, unique among all mods.</param>
    /// <param name="title">The page title; already translated.</param>
    /// <param name="section">The section (a tab) the page joins; null: a group named like the title.</param>
    /// <returns>The page, to add rows to.</returns>
    public static OptionPage Page(string id, string title, string? section = null)
    {
        if (!NotNull(id) || !NotNull(title) || !Assert(id.Length > 0) || !Assert(title.Length > 0))
            return new OptionPage("invalid", "?", "?");
        var page = new OptionPage(id, title, section ?? title);
        var at = Registered.FindIndex(p => p.Id == id);
        if (at >= 0) Registered[at] = page;
        else if (Assert(Registered.Count < MaxPages)) Registered.Add(page);
        return page;
    }

    internal static OptionPage At(int index) =>
        Index(index, Registered.Count) ? Registered[index] : new OptionPage("invalid", "?", "?");

    internal static void RaiseApplied() =>
        ApiEvents.Raise(Applied, h => h(null, EventArgs.Empty), h => Applied -= h, "KometOptions.Applied");

    internal static void Clear()
    {
        Registered.Clear();
        Applied = null;
    }
}

/// <summary>What a row is.</summary>
public enum OptionKind
{
    /// <summary>A heading over the rows after it.</summary>
    Group,
    /// <summary>On or off; its value is 0 or 1.</summary>
    Switch,
    /// <summary>A number between a minimum and a maximum, in steps.</summary>
    Slider,
    /// <summary>One of several names; its value is the index.</summary>
    Choice,
    /// <summary>A button that runs an action.</summary>
    Button,
    /// <summary>One of the game's hotkeys; only on Komet's own controls page.</summary>
    Key
}

// A row marker's look: a plain chip, a warning, or a badge for who drives the mod (it talks to Komet; Komet takes over)
internal enum TagKind
{
    Plain,
    Warn,
    Speaks,
    TakesOver,
    Heading, // a legend's group title: no symbol, its text set apart
    VulkanOk, // on Vulkan, every shader translated
    VulkanPartial, // on Vulkan, some shader did not translate
    OpenGl
}

// A row marker's symbol, drawn by the screen (no font glyphs: the menu fonts lack most symbols)
internal enum TagIcon
{
    Link, // talks to Komet
    Comet, // Komet takes over
    Eye, // only detected
    File, // komet.json
    Folder, // a pack from the player's folder
    Code, // the C# API
    Versions, // Komet versions of its files
    NoVulkan, // OpenGL only
    Pause, // held back
    Warning, // a problem
    Vulkan, // runs on Komet's Vulkan renderer
    OpenGl, // runs on OpenGL
    Shader, // its shaders
    Replaces // files it replaces
}

/// <summary>One row of a page. Values travel as doubles: a switch is 0 or 1, a choice its index.</summary>
public sealed class OptionRow
{
    internal OptionRow(OptionKind kind, string label, string? hint) => (Kind, Label, Hint) = (kind, label, hint);

    /// <summary>What the row is.</summary>
    public OptionKind Kind { get; }
    /// <summary>The row's title.</summary>
    public string Label { get; }
    /// <summary>Shown in the detail panel; null for none.</summary>
    public string? Hint { get; }
    internal Func<double> Get { get; init; } = static () => 0;
    internal Action<double> Set { get; init; } = static _ => { };
    internal double Min { get; init; }
    internal double Max { get; set; } = 1; // a choice's last offered index (Offering)
    internal double Step { get; init; } = 1;
    internal string Unit { get; init; } = "";
    internal string[] Names { get; init; } = [];
    internal Func<string>? Shows { get; init; } // what a key's row or a button shows, asked each time it is drawn
    internal string Code { get; init; } = ""; // a key's hotkey code
    internal Func<bool>? Warn { get; init; } // a key bound like another: its row says so in the warning colour
    internal System.Func<double, string>? Format { get; set; }
    internal Func<bool>? Enabled { get; set; }
    internal Func<string?>? Locked { get; set; } // why the row takes no input, shown above its hint; null: it does
    internal Func<double>? Default { get; set; } // a slider's default, asked as it is drawn: marked on its track, taken back by Delete
    internal bool Live { get; set; } // takes effect as it changes, not with Apply (the screen's own look)
    internal Func<IReadOnlyList<(TagIcon Icon, TagKind Kind)>>? Tags { get; set; } // small symbols after the label, asked as drawn
    internal Func<IReadOnlyList<(TagIcon Icon, TagKind Kind, string Text)>>? Facts { get; set; } // its detail's card: symbol and line

    // The default now, NaN without one
    internal double Standard => Default?.Invoke() is { } value && Finite(value) ? value : double.NaN;

    internal bool IsEnabled => (Enabled?.Invoke() ?? true) && Locked?.Invoke() is null;

    internal string ValueText(double value) => Finite(value) && Assert(Unit.Length < 64)
        ? Format?.Invoke(value) ?? value.ToString("0.##", CultureInfo.InvariantCulture) + Unit
        : "";

    internal double At(double fraction)
    {
        if (!Assert(Max > Min) || !Assert(Step > 0)) return Min;
        var value = Min + Math.Round(Math.Clamp(fraction, 0, 1) * (Max - Min) / Step) * Step;
        return Math.Round(Math.Clamp(value, Min, Max), 6);
    }
}

/// <summary>A page in Komet's options, from <see cref="KometOptions.Page"/>. Each method adds a row and returns the page, so calls chain.</summary>
public sealed class OptionPage
{
    /// <summary>How many rows a page holds; more are refused.</summary>
    public const int MaxOptions = 40;
    internal const int MaxRows = 256; // Komet's own long pages (the controls)
    private readonly List<OptionRow> _options = [];

    internal OptionPage(string id, string title, string section) => (Id, Title, Section) = (id, title, section);

    /// <summary>The page id.</summary>
    public string Id { get; }
    /// <summary>The page title.</summary>
    public string Title { get; }
    /// <summary>The section (a tab) the page belongs to.</summary>
    public string Section { get; }
    internal int Count => _options.Count;
    internal int Capacity { get; init; } = MaxOptions;

    // An in-game doc page (KometDocs): the reader draws these blocks instead of rows
    internal List<DocText.Block>? Doc { get; init; }

    internal OptionRow this[int index] =>
        Index(index, _options.Count) ? _options[index] : new OptionRow(OptionKind.Group, "?", null);

    /// <summary>A heading over the rows that follow.</summary>
    /// <param name="title">The heading.</param>
    /// <returns>The page.</returns>
    public OptionPage Group(string title) =>
        NotNull(title) && Assert(title.Length > 0) ? Add(new OptionRow(OptionKind.Group, title, null)) : this;

    // A heading whose text changes (the renderer drawing now), asked each time the page is drawn
    internal OptionPage Group(Func<string> title) =>
        NotNull(title) && title() is { Length: > 0 } now ? Add(new OptionRow(OptionKind.Group, now, null) { Shows = title }) : this;

    /// <summary>An on/off row.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="get">The current value.</param>
    /// <param name="set">Stores a new value; saving is the mod's (<see cref="KometOptions.Applied"/>).</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public OptionPage Switch(string label, Func<bool> get, Action<bool> set, string? hint = null) =>
        NotNull(label) && NotNull(get) && NotNull(set)
            ? Add(new OptionRow(OptionKind.Switch, label, hint) { Get = () => get() ? 1 : 0, Set = v => set(v >= 0.5) })
            : this;

    /// <summary>A slider from <paramref name="min"/> to <paramref name="max"/> in steps of <paramref name="step"/>.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="min">The lowest value.</param>
    /// <param name="max">The highest value; above min.</param>
    /// <param name="step">The step; above 0.</param>
    /// <param name="get">The current value.</param>
    /// <param name="set">Stores a new value.</param>
    /// <param name="unit">Shown after the value.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public OptionPage Slider(string label, double min, double max, double step, Func<double> get, Action<double> set,
        string unit = "", string? hint = null) =>
        NotNull(get) && NotNull(set) && Assert(max > min) && Assert(step > 0)
            ? Add(new OptionRow(OptionKind.Slider, label, hint)
                { Get = get, Set = set, Min = min, Max = max, Step = step, Unit = unit ?? "" })
            : this;

    internal OptionPage Slider(string label, ValueRange range, Func<double> get, Action<double> set) =>
        Slider(label, range.Min, range.Max, range.Step, get, set).Format(range.Text);

    /// <summary>One of several names.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="names">The names, 1 to <see cref="MaxOptions"/>; already translated.</param>
    /// <param name="get">The index chosen now.</param>
    /// <param name="set">Stores a new index.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public OptionPage Choice(string label, IReadOnlyList<string> names, Func<int> get, Action<int> set,
        string? hint = null) =>
        NotNull(names) && NotNull(get) && NotNull(set) && Assert(names.Count is > 0 and <= MaxOptions)
            ? Add(new OptionRow(OptionKind.Choice, label, hint)
                { Get = () => get(), Set = v => set((int)v), Max = names.Count - 1, Names = [.. names] })
            : this;

    /// <summary>A button that runs an action at once (no Apply needed).</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="text">The button's caption.</param>
    /// <param name="click">What it does; main thread.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public OptionPage Button(string label, string text, Action click, string? hint = null) =>
        NotNull(label) && NotNull(text) && NotNull(click)
            ? Add(new OptionRow(OptionKind.Button, label, hint) { Shows = () => text, Set = _ => click() })
            : this;

    // A button whose caption changes (a reset that asks first)
    internal OptionPage Button(string label, Func<string> text, Action click, string? hint = null) =>
        NotNull(label) && NotNull(text) && NotNull(click)
            ? Add(new OptionRow(OptionKind.Button, label, hint) { Shows = text, Set = _ => click() })
            : this;

    // A game hotkey by code: the row shows its binding, warn tells whether another key shares it
    internal OptionPage Key(string label, string code, Func<string> shows, Func<bool> warn, string? hint = null) =>
        NotNull(label) && NotNull(code) && NotNull(shows) && NotNull(warn) && Assert(code.Length > 0)
            ? Add(new OptionRow(OptionKind.Key, label, hint) { Code = code, Shows = shows, Warn = warn })
            : this;

    /// <summary>How the row added last shows its value, for example a slider's "unlimited" at its end.</summary>
    /// <param name="format">Turns the value into the text shown.</param>
    /// <returns>The page.</returns>
    public OptionPage Format(System.Func<double, string> format)
    {
        if (NotNull(format) && Assert(_options.Count > 0)) _options[^1].Format = format;
        return this;
    }

    /// <summary>The row added last takes input only while <paramref name="enabled"/> holds; otherwise it is drawn dimmed.</summary>
    /// <param name="enabled">Asked each time the page is drawn.</param>
    /// <returns>The page.</returns>
    public OptionPage EnabledWhen(Func<bool> enabled)
    {
        if (NotNull(enabled) && Assert(_options.Count > 0)) _options[^1].Enabled = enabled;
        return this;
    }

    /// <summary>The default of the row added last: [R] Standard and Delete take it back, and a slider marks it on its track.</summary>
    /// <param name="value">The default; a switch 0 or 1, a choice its index.</param>
    /// <returns>The page.</returns>
    public OptionPage Default(double value) => Finite(value) ? Standard(() => value) : this;

    // The slider added last marks its default, asked live (a preset moves it); Delete and its ↺ take it back
    internal OptionPage Standard(Func<double> value)
    {
        if (NotNull(value) && Assert(_options.Count > 0)) _options[^1].Default = value;
        return this;
    }

    // The option added last shows these markers after its label (how a shader mod is bound in, say)
    internal OptionPage Tagged(Func<IReadOnlyList<(TagIcon Icon, TagKind Kind)>> tags)
    {
        if (NotNull(tags) && Assert(_options.Count > 0)) _options[^1].Tags = tags;
        return this;
    }

    // The option added last shows these facts at the top of its detail, each a symbol and a line
    internal OptionPage Card(Func<IReadOnlyList<(TagIcon Icon, TagKind Kind, string Text)>> facts)
    {
        if (NotNull(facts) && Assert(_options.Count > 0)) _options[^1].Facts = facts;
        return this;
    }

    // What the page's symbols mean, shown in the detail while no row is chosen
    internal Func<IReadOnlyList<(TagIcon Icon, TagKind Kind, string Text)>>? Legend { get; set; }

    // The option added last takes effect at once instead of waiting for Apply
    internal OptionPage Live()
    {
        if (Assert(_options.Count > 0)) _options[^1].Live = true;
        return this;
    }

    // The choice added last offers only its first count names; the others only name a state it shows (a preset "Adjusted")
    internal OptionPage Offering(int count)
    {
        if (Assert(_options.Count > 0) && Index(count - 1, _options[^1].Names.Length)) _options[^1].Max = count - 1;
        return this;
    }

    // Last option locked while locked() gives a reason (held feature knob): dimmed, reason above its hint
    internal OptionPage LockedWhen(Func<string?> locked)
    {
        if (NotNull(locked) && Assert(_options.Count > 0)) _options[^1].Locked = locked;
        return this;
    }

    // The page with other mods' feature rows added (Features.Placed): a copy, so the page stays as its mod built it
    internal OptionPage Copy()
    {
        var copy = new OptionPage(Id, Title, Section) { Capacity = MaxRows };
        copy._options.AddRange(_options);
        return copy;
    }

    private OptionPage Add(OptionRow option)
    {
        if (NotNull(option.Label) && Assert(option.Label.Length > 0) &&
            Assert(_options.Count < Math.Min(Capacity, MaxRows))) _options.Add(option);
        return this;
    }
}

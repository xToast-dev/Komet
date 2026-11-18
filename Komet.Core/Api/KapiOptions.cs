using Komet.Core;

namespace Komet.Api;

/// <summary>
///     The mod's pages in Komet's options window, through <see cref="KapiMod.Options" />. Getters, setters, saving and
///     translation are the mod's.
/// </summary>
/// <example>
///     <code language="csharp">
/// komet.Options.Page("My mod")
///     .Group("Look")
///     .Switch("Warm picture", () =&gt; config.Warm, v =&gt; config.Warm = v, hint: "Turns the warm pass on.")
///     .Slider("Warmth", 0, 1, 0.05, () =&gt; config.Warmth, v =&gt; config.Warmth = v).Default(0.7)
///     .Choice("Quality", ["Low", "High"], () =&gt; config.Quality, v =&gt; config.Quality = v)
///     .Button("Settings file", "Open", OpenFolder);
/// komet.Options.Applied += (_, _) =&gt; config.Save();
/// </code>
/// </example>
public sealed class KapiOptions
{
    /// <summary>How many pages all mods together may add.</summary>
    public const int MaxPages = OptionPages.MaxPages;

    private readonly KapiMod _mod;

    internal KapiOptions(KapiMod mod) => _mod = mod;

    /// <summary>
    ///     Raised after the player applied changes (Apply or Done): the moment to save. A handler that throws is logged
    ///     once and unsubscribed.
    /// </summary>
    public event EventHandler? Applied
    {
        add
        {
            if (value is null || !_mod.Keep(() => OptionPages.Applied -= value)) return;

            OptionPages.Applied += value;
        }
        remove => OptionPages.Applied -= value;
    }

    /// <summary>The mod's page in the Mods tab, beside every other mod's. Asking again starts it over.</summary>
    /// <param name="title">The page title; already translated.</param>
    /// <returns>The page, to add rows to.</returns>
    public KapiPage Page(string title) => Page("", title, null);

    /// <summary>One of several pages of the mod. Asking again for the same id starts that page over.</summary>
    /// <param name="id">The page's name, unique within the mod; its full id is <c>modid:id</c>.</param>
    /// <param name="title">The page title; already translated.</param>
    /// <param name="section">The section (a tab) the page joins; null: the Mods tab.</param>
    /// <returns>The page, to add rows to.</returns>
    public KapiPage Page(string id, string title, string? section)
    {
        var full = string.IsNullOrEmpty(id) ? _mod.ModId : _mod.ModId + ":" + id;
        var page = OptionPages.Page(full, title, section);
        if (!_mod.Keep(() => OptionPages.Remove(full), KapiKind.Page)) OptionPages.Remove(full);
        return new KapiPage(page);
    }
}

/// <summary>A page of Komet's options window. Each method adds a row and returns the page, so calls chain.</summary>
public sealed class KapiPage
{
    /// <summary>How many rows a page holds; more are refused.</summary>
    public const int MaxRows = OptionPage.MaxOptions;

    private readonly OptionPage _page;

    internal KapiPage(OptionPage page) => _page = page;

    /// <summary>The page's full id, for <see cref="KapiFeature.Page" />.</summary>
    public string Id => _page.Id;

    /// <summary>The page title.</summary>
    public string Title => _page.Title;

    /// <summary>A heading over the rows that follow.</summary>
    /// <param name="title">The heading.</param>
    /// <returns>The page.</returns>
    public KapiPage Group(string title) => Chain(_page.Group(title));

    /// <summary>An on/off row.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="get">The current value.</param>
    /// <param name="set">Stores a new value; saving is the mod's (<see cref="KapiOptions.Applied" />).</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public KapiPage Switch(string label, Func<bool> get, Action<bool> set, string? hint = null) =>
        Chain(_page.Switch(label, get, set, hint));

    /// <summary>A slider from <paramref name="min" /> to <paramref name="max" /> in steps of <paramref name="step" />.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="min">The lowest value.</param>
    /// <param name="max">The highest value; above min.</param>
    /// <param name="step">The step; above 0.</param>
    /// <param name="get">The current value.</param>
    /// <param name="set">Stores a new value.</param>
    /// <param name="unit">Shown after the value.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public KapiPage Slider(string label, double min, double max, double step, Func<double> get, Action<double> set,
        string? unit = "", string? hint = null) =>
        Chain(_page.Slider(label, min, max, step, get, set, unit, hint));

    /// <summary>One of several names.</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="names">The names, 1 to <see cref="MaxRows" />; already translated.</param>
    /// <param name="get">The index chosen now.</param>
    /// <param name="set">Stores a new index.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public KapiPage Choice(string label, IReadOnlyList<string> names, Func<int> get, Action<int> set,
        string? hint = null) =>
        Chain(_page.Choice(label, names, get, set, hint));

    /// <summary>A button that runs an action at once (no Apply needed).</summary>
    /// <param name="label">The row's title.</param>
    /// <param name="text">The button's caption.</param>
    /// <param name="click">What it does; main thread.</param>
    /// <param name="hint">Shown in the detail panel.</param>
    /// <returns>The page.</returns>
    public KapiPage Button(string label, string text, Action click, string? hint = null) =>
        Chain(_page.Button(label, text, click, hint));

    /// <summary>How the row added last shows its value, for example a slider's "unlimited" at its end.</summary>
    /// <param name="format">Turns the value into the text shown.</param>
    /// <returns>The page.</returns>
    public KapiPage Format(System.Func<double, string> format) => Chain(_page.Format(format));

    /// <summary>The row added last takes input only while <paramref name="enabled" /> holds; otherwise it is dimmed.</summary>
    /// <param name="enabled">Asked each time the page is drawn.</param>
    /// <returns>The page.</returns>
    public KapiPage EnabledWhen(Func<bool> enabled) => Chain(_page.EnabledWhen(enabled));

    /// <summary>The default of the row added last: Standard and Delete take it back, a slider marks it on its track.</summary>
    /// <param name="value">The default; a switch 0 or 1, a choice its index.</param>
    /// <returns>The page.</returns>
    public KapiPage Default(double value) => Chain(_page.Default(value));

    // The page's methods return the page itself; KAPI hands out its own
    private KapiPage Chain(OptionPage page)
    {
        _ = page;
        return this;
    }
}

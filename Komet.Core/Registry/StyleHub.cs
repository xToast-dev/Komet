using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace Komet.Core;

// How Komet's options screen looks: styles registered from code (KapiStyles); StyleLibrary adds the folder and asset
// styles. Styles registered from code go when the world closes.
internal static class StyleHub
{
    /// <summary>How many styles code may register.</summary>
    public const int MaxStyles = 32;

    private static readonly List<StyleData> Registered = [];

    // Its styles and subscriptions belong to the world they were registered in
    static StyleHub() => WorldEnd.On(nameof(StyleHub), Clear);

    internal static int Count => Registered.Count;

    /// <summary>Raised on the main thread after <see cref="Register" /> changed the list.</summary>
    public static event EventHandler? Changed;

    // Adds a style from code; reusing an id replaces it. Relative file names resolve against StyleData.Folder when set,
    // else against the assets of StyleData.Domain
    public static bool Register(StyleData style)
    {
        if (!NotNull(style) || !Assert(style.Id.Length > 0)) return false;

        var at = Registered.FindIndex(s => s.Id == style.Id);
        if (at >= 0)
        {
            Registered[at] = style;
        }
        else if (Registered.Count < MaxStyles)
        {
            Registered.Add(style);
        }
        else
        {
            return false;
        }

        ModEvents.Raise(Changed, h => h(null, EventArgs.Empty), h => Changed -= h, "komet.Styles.Changed");
        return true;
    }

    // A KAPI connection's style goes with it
    internal static void Remove(string id)
    {
        if (Registered.RemoveAll(s => s.Id == id) > 0)
            ModEvents.Raise(Changed, h => h(null, EventArgs.Empty), h => Changed -= h, "komet.Styles.Changed");
    }

    internal static StyleData At(int index) => Index(index, Registered.Count) ? Registered[index] : new StyleData();

    private static void Clear()
    {
        Registered.Clear();
        Changed = null;
    }
}

/// <summary>
///     One style, as <c>style.json</c> holds it. Every member is optional except <see cref="Id" />; what is missing comes
///     from <see cref="Base" />.
/// </summary>
/// <remarks>
///     Colours are <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. The known keys of <see cref="Colors" />, <see cref="Fonts" /> and
///     <see cref="Sizes" /> are in the article on styles and in <c>docs/schema/komet-style.schema.json</c>; an unknown key
///     is logged once so a typo shows.
/// </remarks>
internal sealed class StyleData
{
    /// <summary>The style's id; a folder style takes its folder's name when empty.</summary>
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    /// <summary>Shown in the style choice; the id when empty. May be a lang key (<c>domain:key</c>).</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>Free text.</summary>
    [JsonProperty("author")]
    public string? Author { get; set; }

    /// <summary>The style whose values this one starts from: <c>pragmatic</c> (when empty) or any other id.</summary>
    [JsonProperty("base")]
    public string? Base { get; set; }

    /// <summary>Colours by key, such as <c>accent</c>, <c>select</c>, <c>title</c>, <c>changed</c>.</summary>
    [JsonProperty("colors")]
    public Dictionary<string, string> Colors { get; } = [];

    /// <summary>
    ///     Font family names for <c>text</c>, <c>heading</c>, <c>title</c> and <c>numbers</c>; empty takes the game's
    ///     font.
    /// </summary>
    [JsonProperty("fonts")]
    public Dictionary<string, string> Fonts { get; } = [];

    /// <summary>
    ///     <c>.ttf</c> or <c>.otf</c> files registered before the style draws, so <see cref="Fonts" /> may name the
    ///     families inside them.
    /// </summary>
    [JsonProperty("fontFiles")]
    public Collection<string> FontFiles { get; } = [];

    /// <summary>
    ///     Sizes in GUI pixels before scaling, and switches as 0 or 1 (<c>ticks</c>, <c>logo</c>, <c>pan</c>,
    ///     <c>slide</c>, <c>decode</c> ...).
    /// </summary>
    [JsonProperty("sizes")]
    public Dictionary<string, double> Sizes { get; } = [];

    /// <summary>
    ///     A PNG, GIF or WebP drawn in the header; pixel art stays sharp. Empty: <c>ModConfig/komet/logo.png</c>, else
    ///     Komet's icon.
    /// </summary>
    [JsonProperty("logo")]
    public string? Logo { get; set; }

    /// <summary><c>blur</c> (the world, blurred), <c>none</c>, or a GLSL 330 fragment shader file.</summary>
    [JsonProperty("background")]
    public string? Background { get; set; }

    /// <summary>A background shader's source given inline (styles from code); wins over <see cref="Background" />.</summary>
    [JsonIgnore]
    public string? BackgroundSource { get; set; }

    /// <summary>Where relative file names resolve: a folder on disk; when null, the assets of <see cref="Domain" />.</summary>
    [JsonIgnore]
    public string? Folder { get; set; }

    /// <summary>The asset domain relative file names resolve in when <see cref="Folder" /> is null.</summary>
    [JsonProperty("domain")]
    public string? Domain { get; set; }
}

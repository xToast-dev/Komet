using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace Komet.Api;

/// <summary>
/// How Komet's options screen looks. A style is data: colours, fonts, sizes, a logo and a background shader; every
/// value it leaves out comes from its <see cref="KometStyle.Base"/>.
/// </summary>
/// <remarks>
/// Styles come from a folder under <c>ModConfig/komet/styles</c> (no mod needed, reloads in game), from any mod's
/// assets under <c>config/komet-styles</c>, or from code through <see cref="Register"/> in <c>StartClientSide</c>.
/// Komet's own Pragmatic lives in its assets and serves as the example.
/// </remarks>
public static class KometStyles
{
    /// <summary>How many styles code may register.</summary>
    public const int MaxStyles = 32;
    private static readonly List<KometStyle> Registered = [];

    /// <summary>Raised on the main thread after <see cref="Register"/> or a reload changed the list.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Adds a style from code; reusing an id replaces it. Relative file names (<see cref="KometStyle.Logo"/>,
    /// <see cref="KometStyle.Background"/>, <see cref="KometStyle.FontFiles"/>) resolve against
    /// <see cref="KometStyle.Folder"/> when set, else against the assets of <see cref="KometStyle.Domain"/>.
    /// </summary>
    /// <example>
    /// <code language="csharp">
    /// KometStyles.Register(new KometStyle
    /// {
    ///     Id = "mymod-night", Name = "Night", Base = "pragmatic", Domain = "mymod",
    ///     Colors = { ["accent"] = "#7A5CFF", ["select"] = "#4B33B8" },
    ///     Background = "config/night.fsh"
    /// });
    /// </code>
    /// </example>
    /// <param name="style">The style; its <see cref="KometStyle.Id"/> must not be empty.</param>
    public static void Register(KometStyle style)
    {
        if (!NotNull(style) || !Assert(style.Id.Length > 0)) return;
        var at = Registered.FindIndex(s => s.Id == style.Id);
        if (at >= 0) Registered[at] = style;
        else if (Assert(Registered.Count < MaxStyles)) Registered.Add(style);
        ApiEvents.Raise(Changed, h => h(null, EventArgs.Empty), h => Changed -= h, "KometStyles.Changed");
    }

    internal static int Count => Registered.Count;

    internal static KometStyle At(int index) => Index(index, Registered.Count) ? Registered[index] : new KometStyle();

    internal static void Clear()
    {
        Registered.Clear();
        Changed = null;
    }
}

/// <summary>
/// One style, as <c>style.json</c> holds it. Every member is optional except <see cref="Id"/>; what is missing comes
/// from <see cref="Base"/>.
/// </summary>
/// <remarks>
/// Colours are <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. The known keys of <see cref="Colors"/>, <see cref="Fonts"/> and
/// <see cref="Sizes"/> are in the article on styles and in <c>docs/schema/komet-style.schema.json</c>; an unknown key
/// is logged once so a typo shows.
/// </remarks>
public sealed class KometStyle
{
    /// <summary>The style's id; a folder style takes its folder's name when empty.</summary>
    [JsonProperty("id")] public string Id { get; set; } = "";

    /// <summary>Shown in the style choice; the id when empty. May be a lang key (<c>domain:key</c>).</summary>
    [JsonProperty("name")] public string Name { get; set; } = "";

    /// <summary>Free text.</summary>
    [JsonProperty("author")] public string? Author { get; set; }

    /// <summary>The style whose values this one starts from: <c>pragmatic</c> (when empty) or any other id.</summary>
    [JsonProperty("base")] public string? Base { get; set; }

    /// <summary>Colours by key, such as <c>accent</c>, <c>select</c>, <c>title</c>, <c>changed</c>.</summary>
    [JsonProperty("colors")] public Dictionary<string, string> Colors { get; } = [];

    /// <summary>Font family names for <c>text</c>, <c>heading</c>, <c>title</c> and <c>numbers</c>; empty takes the game's font.</summary>
    [JsonProperty("fonts")] public Dictionary<string, string> Fonts { get; } = [];

    /// <summary><c>.ttf</c> or <c>.otf</c> files registered before the style draws, so <see cref="Fonts"/> may name the families inside them.</summary>
    [JsonProperty("fontFiles")] public Collection<string> FontFiles { get; } = [];

    /// <summary>Sizes in GUI pixels before scaling, and switches as 0 or 1 (<c>ticks</c>, <c>logo</c>, <c>pan</c>, <c>slide</c>, <c>decode</c> ...).</summary>
    [JsonProperty("sizes")] public Dictionary<string, double> Sizes { get; } = [];

    /// <summary>A PNG, GIF or WebP drawn in the header; pixel art stays sharp. Empty: <c>ModConfig/komet/logo.png</c>, else Komet's icon.</summary>
    [JsonProperty("logo")] public string? Logo { get; set; }

    /// <summary><c>blur</c> (the world, blurred), <c>none</c>, or a GLSL 330 fragment shader file.</summary>
    [JsonProperty("background")] public string? Background { get; set; }

    /// <summary>A background shader's source given inline (styles from code); wins over <see cref="Background"/>.</summary>
    [JsonIgnore] public string? BackgroundSource { get; set; }

    /// <summary>Where relative file names resolve: a folder on disk; when null, the assets of <see cref="Domain"/>.</summary>
    [JsonIgnore] public string? Folder { get; set; }

    /// <summary>The asset domain relative file names resolve in when <see cref="Folder"/> is null.</summary>
    [JsonProperty("domain")] public string? Domain { get; set; }
}

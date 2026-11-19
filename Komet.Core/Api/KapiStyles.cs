using System.Collections.ObjectModel;
using Komet.Core;

namespace Komet.Api;

/// <summary>
///     One look of Komet's options window: colours, fonts, sizes, a logo and a background shader. Every value it leaves
///     out comes from its <see cref="Base" />.
/// </summary>
/// <remarks>
///     Colours are <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. The known keys of <see cref="Colors" />, <see cref="Fonts" /> and
///     <see cref="Sizes" /> are in the article on styles; an unknown key is logged once so a typo shows.
/// </remarks>
/// <param name="id">The style's id, unique among all styles.</param>
/// <param name="name">Shown in the style list.</param>
public sealed class KapiStyle(string id, string name)
{
    /// <summary>How many entries each of the colour, font, size and font file lists may have.</summary>
    public const int MaxKeys = 256;

    /// <summary>The style's id, unique among all styles.</summary>
    public string Id { get; } = NotNull(id) ? id : "";

    /// <summary>Shown in the style list.</summary>
    public string Name { get; } = NotNull(name) ? name : "";

    /// <summary>Shown under the name.</summary>
    public string? Author { get; init; }

    /// <summary>The style that fills what this one leaves out; null: Komet's default.</summary>
    public string? Base { get; init; }

    /// <summary>Colours by key, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public Dictionary<string, string> Colors { get; } = [];

    /// <summary>Font names by key.</summary>
    public Dictionary<string, string> Fonts { get; } = [];

    /// <summary>Font files to load, relative to the mod's assets.</summary>
    public Collection<string> FontFiles { get; } = [];

    /// <summary>Sizes by key, in pixels at GUI scale 1.</summary>
    public Dictionary<string, double> Sizes { get; } = [];

    /// <summary>The logo's file, relative to the mod's assets.</summary>
    public string? Logo { get; init; }

    /// <summary>The background fragment shader's file, relative to the mod's assets.</summary>
    public string? Background { get; init; }

    internal StyleData For(string modId)
    {
        var style = new StyleData
        {
            Id = Id, Name = Name, Author = Author, Base = Base, Logo = Logo, Background = Background, Domain = modId
        };
        foreach (var (key, value) in Colors.Bounded(MaxKeys)) style.Colors[key] = value;
        foreach (var (key, value) in Fonts.Bounded(MaxKeys)) style.Fonts[key] = value;
        foreach (var (key, value) in Sizes.Bounded(MaxKeys)) style.Sizes[key] = value;
        foreach (var file in FontFiles.Bounded(MaxKeys)) style.FontFiles.Add(file);
        return style;
    }
}

/// <summary>Styles for Komet's options window, through <see cref="KapiMod.Styles" />.</summary>
/// <remarks>
///     Styles also come without code: a folder under <c>ModConfig/komet/styles</c>, or <c>config/komet-styles</c> in any
///     mod's assets.
/// </remarks>
/// <example>
///     <code language="csharp">
/// komet.Styles.Register(new KapiStyle("mymod-night", "Night")
/// {
///     Base = "pragmatic", Background = "config/night.fsh",
///     Colors = { ["accent"] = "#7A5CFF", ["select"] = "#4B33B8" }
/// });
/// </code>
/// </example>
public sealed class KapiStyles
{
    /// <summary>How many styles code may register, all mods together.</summary>
    public const int MaxStyles = StyleHub.MaxStyles;

    private readonly KapiMod _mod;

    internal KapiStyles(KapiMod mod) => _mod = mod;

    /// <summary>The list of styles changed. A handler that throws is logged once and unsubscribed.</summary>
    public event EventHandler? Changed
    {
        add
        {
            if (value is null || !_mod.Keep(() => StyleHub.Changed -= value)) return;

            StyleHub.Changed += value;
        }
        remove => StyleHub.Changed -= value;
    }

    /// <summary>Adds a style; the same id replaces it. Its files are the mod's assets.</summary>
    /// <param name="style">The style.</param>
    /// <returns>False when refused: an empty id, or too many styles.</returns>
    public bool Register(KapiStyle style)
    {
        if (!NotNull(style) || style.Id.Length == 0 || !_mod.IsConnected) return false;

        var id = style.Id;
        if (!StyleHub.Register(style.For(_mod.ModId))) return false;

        if (!_mod.Keep(() => StyleHub.Remove(id), KapiKind.Style)) StyleHub.Remove(id);
        return true;
    }
}

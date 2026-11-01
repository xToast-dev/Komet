using System.Globalization;
using Cairo;
using Komet.Api;
using Komet.Hud;

namespace Komet.Options;

// A style resolved for drawing: its Base chain merged, colours parsed, the logo loaded. Every key has a value even when
// no file gives one (Pragmatic's look, below), so a style may name only what it changes.
internal sealed class StyleSheet
{
    public const string Pragmatic = "pragmatic";

    // Pragmatic's earlier ids (pre-releases of 3.0): settings and styles naming them get Pragmatic
    private static readonly string[] Former = ["prag" + "mata", "lu" + "na"];

    public static string Current(string id) =>
        NotNull(id) && Array.Exists(Former, old => string.Equals(id.Trim(), old, StringComparison.OrdinalIgnoreCase)) ? Pragmatic : id;

    // The keys a style may set; docs/site/articles/styles.md says what each one colours or sizes
    internal static readonly string[] ColorKeys =
    [
        "backdrop", "backdropPlain", "panel", "band", "row", "line", "text", "soft", "faint", "accent", "select", "selectText",
        "title", "changed", "good", "bad", "glow", "cell", "chip", "bubble"
    ];

    internal static readonly string[] SizeKeys =
        ["corner", "ticks", "logo", "header", "footer", "row", "gap", "pad", "tracking", "scene", "veil", "pan", "slide", "decode"];

    internal static readonly string[] FontKeys = ["text", "heading", "title", "numbers"];

    private static readonly Rgba Sky = new(0x8D / 255.0, 0xBB / 255.0, 1, 1); // Pragmatic's title blue

    // Pragmatic's look (its style.json's values): what every key falls back to
    private static readonly Dictionary<string, Rgba> ColorDefaults = new()
    {
        ["backdrop"] = new(0, 0, 0, 0x33 / 255.0), ["backdropPlain"] = new(1 / 255.0, 2 / 255.0, 8 / 255.0, 0xB0 / 255.0),
        ["panel"] = new(0, 0, 0, 0x8C / 255.0), ["band"] = new(3 / 255.0, 6 / 255.0, 12 / 255.0, 0xD9 / 255.0), ["row"] = new(0, 0, 0, 0),
        ["line"] = Ink(0x1A), ["text"] = Ink(0xFF), ["soft"] = Ink(0x99), ["faint"] = Ink(0x5C), ["accent"] = new(0x2F / 255.0, 0x72 / 255.0, 0xE2 / 255.0, 1),
        ["select"] = new(0x1F / 255.0, 0x56 / 255.0, 0xB6 / 255.0, 1), ["selectText"] = Rgba.White(1), ["title"] = Sky, ["changed"] = Sky,
        ["good"] = new(0x8F / 255.0, 0xE3 / 255.0, 0xB4 / 255.0, 1), ["bad"] = new(1, 0x9A / 255.0, 0x8A / 255.0, 1), ["glow"] = Sky,
        ["cell"] = Ink(0x0A), ["chip"] = Ink(0x0F), ["bubble"] = new(3 / 255.0, 6 / 255.0, 12 / 255.0, 0xF5 / 255.0)
    };

    private static readonly Dictionary<string, double> SizeDefaults = new()
    {
        ["corner"] = 0, ["ticks"] = 0, ["logo"] = 96, ["header"] = 136, ["footer"] = 44, ["row"] = 38, ["gap"] = 14, ["pad"] = 18,
        ["tracking"] = 6, ["scene"] = 1, ["veil"] = 0, ["pan"] = 1, ["slide"] = 1, ["decode"] = 1
    };

    // Pragmatic's text colour (#EEF1F4) at an alpha
    private static Rgba Ink(int alpha) => new(0xEE / 255.0, 0xF1 / 255.0, 0xF4 / 255.0, alpha / 255.0);

    private readonly Dictionary<string, Rgba> _colors;
    private readonly Dictionary<string, double> _sizes;
    private readonly Dictionary<string, string> _fonts;

    private StyleSheet(string id, string name, Dictionary<string, Rgba> colors, Dictionary<string, double> sizes,
        Dictionary<string, string> fonts)
    {
        (Id, Name, _colors, _sizes, _fonts) = (id, name, colors, sizes, fonts);
    }

    public string Id { get; }
    public string Name { get; }
    public ImageSurface? Logo { get; private set; }
    public string? Shader { get; private init; } // the background's fragment source; null: Blur says what is under the screen
    public bool Blur { get; private init; } = true;

    public Rgba this[string color] => _colors.TryGetValue(color, out var c) ? c : Rgba.White(1);

    public double Size(string key) => _sizes.TryGetValue(key, out var v) ? v : 0;

    public bool On(string key) => Size(key) > 0;

    // A family name, or null for the game's
    public string? Font(string key) => _fonts.TryGetValue(key, out var f) && f.Length > 0 ? f : null;

    // Pragmatic's colours and sizes without its files (no shader, no logo, the game's font): the world blurred under it
    public static StyleSheet Fallback() => new(Pragmatic, "Pragmatic", new(ColorDefaults), new(SizeDefaults), []);

    // merged: the style with its Base chain already applied; shader and logo as resolved by the library
    public static StyleSheet From(KometStyle merged, string? shader, ImageSurface? logo, ILogger? logger)
    {
        if (!NotNull(merged) || !Assert(merged.Id.Length > 0)) return Fallback();
        var colors = new Dictionary<string, Rgba>(ColorDefaults);
        foreach (var (key, text) in merged.Colors.Bounded(ColorKeys.Length * 4))
            if (!Known(ColorKeys, key, merged.Id, "color", logger)) continue;
            else if (Parse(text) is { } color) colors[key] = color;
            else logger?.Warning("Komet styles: '{0}' colour '{1}' is not #RRGGBB or #RRGGBBAA: {2}", merged.Id, key, text);
        var sizes = new Dictionary<string, double>(SizeDefaults);
        foreach (var (key, value) in merged.Sizes.Bounded(SizeKeys.Length * 4))
            if (Known(SizeKeys, key, merged.Id, "size", logger) && Finite(value)) sizes[key] = Math.Clamp(value, 0, 4096);
        var fonts = new Dictionary<string, string>();
        foreach (var (key, family) in merged.Fonts.Bounded(FontKeys.Length * 4))
            if (Known(FontKeys, key, merged.Id, "font", logger) && family is not null) fonts[key] = family;
        var background = merged.Background ?? "blur";
        return new StyleSheet(merged.Id, merged.Name.Length > 0 ? merged.Name : merged.Id, colors, sizes, fonts)
        {
            Shader = shader,
            Blur = shader is null ? background != "none" : background.Length > 0,
            Logo = logo
        };
    }

    private static bool Known(string[] keys, string key, string style, string kind, ILogger? logger)
    {
        if (Array.IndexOf(keys, key) >= 0) return true;
        logger?.Warning("Komet styles: '{0}' has an unknown {1} '{2}' (known: {3})", style, kind, key, string.Join(", ", keys));
        return false;
    }

    // "#RRGGBB" or "#RRGGBBAA"
    internal static Rgba? Parse(string? text)
    {
        if (text is null) return null;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        if (hex.Length == 6) v = (v << 8) | 0xFF;
        return new Rgba((v >> 24) / 255.0, ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    // The logo's surface freed; the library calls it when no paint can still read it
    public void Release()
    {
        if (Logo is not { } logo) return;
        Logo = null;
        logo.Dispose();
    }
}

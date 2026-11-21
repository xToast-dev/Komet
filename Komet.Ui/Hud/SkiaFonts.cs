using Cairo;
using SkiaSharp;

namespace Komet.Hud;

// Komet's GUI is painted by SkiaSharp. The engine's CairoFont stays the font descriptor (family, size, colour, weight):
// it holds no drawing state of its own, so no Cairo context is needed to measure or draw it anymore. Faces come from the
// platform font manager, which sees the same fontconfig/GDI registrations as the engine.
internal static class SkiaFonts
{
    private const int MaxFaces = 64;

    private static readonly Dictionary<(string Family, int Weight, int Slant), SKTypeface> Faces = [];
    private static bool _noSkia;

    public static SKColor Color(Rgba color) => new(Byte(color.R), Byte(color.G), Byte(color.B), Byte(color.A));

    public static SKColor Color(double[]? color)
    {
        if (color is [var r, var g, var b])
        {
            return new SKColor(Byte(r), Byte(g), Byte(b), 255);
        }

        if (color is [var r4, var g4, var b4, var a])
        {
            return new SKColor(Byte(r4), Byte(g4), Byte(b4), Byte(a));
        }

        return new SKColor(255, 255, 255, 255);
    }

    // Subpixel antialiasing is what CairoFont.SetupContext selected; it must match the engine's Skia GUI so the two
    // agree on advances and glyph outlines. Callers keep one font and change Typeface/Size in a measuring loop.
    public static SKFont Font(CairoFont font, double size)
    {
        var result = new SKFont { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        Setup(result, font, size);
        return result;
    }

    public static void Setup(SKFont font, CairoFont face, double size)
    {
        if (Face(face) is { } typeface) font.Typeface = typeface;
        font.Size = (float)size;
    }

    public static SKTypeface? Face(CairoFont font) => Face(font.Fontname,
        font.FontWeight == FontWeight.Bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal,
        font.Slant == FontSlant.Italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright);

    public static SKTypeface? Face(string family, int weight = (int)SKFontStyleWeight.Normal,
        int slant = (int)SKFontStyleSlant.Upright)
    {
        if (_noSkia) return null;

        var key = (family, weight, slant);
        if (Faces.TryGetValue(key, out var face)) return face;

        try
        {
            using var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, (SKFontStyleSlant)slant);
            face = SKFontManager.Default.MatchFamily(family, style) ?? SKTypeface.Default;
            if (Faces.Count < MaxFaces) Faces[key] = face;
            return face;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            _noSkia = true; // tests without the game's native Skia: no face to measure with
            return null;
        }
    }

    public static double Width(CairoFont font, double size, string text)
    {
        using var f = Font(font, size);
        return f.MeasureText(text);
    }

    // Skia's ascent is negative; Cairo's FontExtents.Ascent is the positive distance from the baseline to the top
    public static double Height(SKFont font)
    {
        _ = font.GetFontMetrics(out var metrics);
        return -metrics.Ascent + metrics.Descent;
    }

    public static double Height(CairoFont font, double size)
    {
        using var f = Font(font, size);
        return Height(f);
    }

    public static double Ascent(SKFont font)
    {
        _ = font.GetFontMetrics(out var metrics);
        return -metrics.Ascent;
    }

    private static byte Byte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}

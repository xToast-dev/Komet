using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace Komet.Hud;

// HudCanvas: drawing the uploaded texture, Skia helpers and the recorded command types.
internal sealed partial class HudCanvas
{
    // z: depth among the GUI's draws (the hotbar's item stacks sit above the default)
    // placeOnly: where clicks land is set, nothing drawn (the caller draws it in parts)
    public void Draw(double x, double y, float z = 50, bool placeOnly = false)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0) || !Assert(_texture.Width >= Width)) return;

        X = Math.Clamp(x, 0, Math.Max(0, _capi.Render.FrameWidth - Width));
        Y = Math.Clamp(y, 0, Math.Max(0, _capi.Render.FrameHeight - Height));
        if (placeOnly) return;

        _capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height, z);
    }

    public (double X, double Y) Within(double x, double y) =>
        !Finite(x + y)
            ? (0, 0)
            : (Math.Clamp(x, 0, Math.Max(0, _capi.Render.FrameWidth - Width)),
                Math.Clamp(y, 0, Math.Max(0, _capi.Render.FrameHeight - Height)));

    // Faded and scaled about its centre (window opening); X and Y stay where a click lands
    public void Fade(double x, double y, float z, double alpha, double scale)
    {
        if (!Finite(alpha + scale) || !Assert(scale > 0) || !Assert(_texture.TextureId != 0)) return;

        (X, Y) = Within(x, y);
        var a = (float)Math.Clamp(alpha, 0, 1);
        _ = _tint.Set(a, a, a, a);
        var (w, h) = (_texture.Width * scale, _texture.Height * scale);
        _capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X + Width * (1 - scale) / 2,
            Y + Height * (1 - scale) / 2, w, h,
            z, _tint);
    }

    // The picture at x, y faded, without moving where clicks land (a part of it sliding under a scissor)
    public void Shifted(double x, double y, float z, double alpha)
    {
        if (!Finite(x + y + alpha) || alpha <= 0 || !Assert(_texture.TextureId != 0)) return;

        var a = (float)Math.Min(1, alpha);
        _ = _tint.Set(a, a, a, a);
        _capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, x, y, _texture.Width, _texture.Height, z,
            _tint);
    }

    // Premultiplied tint over a white canvas: a quad of any colour
    public void Stretch(double x, double y, double w, double h, float z, Rgba color)
    {
        if (!Finite(x + y) || w <= 0 || h <= 0 || color.A <= 0 || !Assert(_texture.TextureId != 0)) return;

        _ = _tint.Set((float)(color.R * color.A), (float)(color.G * color.A), (float)(color.B * color.A),
            (float)color.A);
        _capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, x, y, w, h, z, _tint);
    }

    // The font keeps its typeface and size from text to text; the worker never touches the main thread's measure font
    private static void Setup(SKFont font, CairoFont face, double size) => SkiaFonts.Setup(font, face, size);

    [MemberNotNull(nameof(_ctx), nameof(_surface))]
    private void Surface(int width, int height)
    {
        Release();
        _surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _ctx = new SKCanvas(_surface);
        _pixels = new int[width * height];
    }

    private static void FontColor(SKPaint paint, double[]? color) => paint.Color = SkiaFonts.Color(color);

    private static void Source(SKPaint paint, Rgba color) => paint.Color = SkiaFonts.Color(color);

    private static int RoundUp(double value) =>
        Assert(value is > 0 and <= MaxSize) ? (int)Math.Ceiling(value / SizeStep) * SizeStep : SizeStep;

    private enum Op : byte
    {
        Fill,
        Text,
        Line,
        Polyline,
        Area,
        Circle,
        Arc,
        Caret,
        Clip,
        Unclip,
        Outline,
        Image,
        Arrow
    }

    private readonly record struct Cmd(
        Op Op,
        double A,
        double B,
        double C = 0,
        double D = 0,
        double E = 0,
        double F = 0,
        Rgba Color = default,
        CairoFont? Font = null,
        string? Text = null,
        int From = 0,
        int Count = 0,
        bool Flag = false,
        SKBitmap? Image = null);

    private sealed class Page
    {
        public readonly List<Cmd> Cmds = [];
        public readonly List<double> Points = [];
        public object? Tag; // what goes with the picture (a dialog's click map)
        public double W, H;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Cairo;

namespace Komet.Hud;

internal readonly record struct Rgba(double R, double G, double B, double A)
{
    public static Rgba White(double alpha) => new(1, 1, 1, Assert(alpha is >= 0 and <= 1) ? alpha : 1);
}

internal sealed class HudCanvas(ICoreClientAPI capi) : IDisposable
{
    public const int GraphFrames = 240;
    private const int SizeStep = 32, MaxSize = 16384, MaxGridLines = MaxSize / 8;

    // The options screen's and the update check's colours
    public static readonly Rgba Accent = new(0.16, 0.45, 0.85, 1), Warning = new(1.0, 0.72, 0.25, 1), Error = new(1.0, 0.38, 0.35, 1),
        Good = new(0.35, 0.75, 0.45, 1);

    private static readonly Rgba GridLine = Rgba.White(0.07), GridMajor = Rgba.White(0.18);

    private Context? _ctx;
    private CairoFont? _font; // set up on _ctx since Begin() or BeginMeasure()

    private ImageSurface? _surface;
    private LoadedTexture _texture = new(capi);
    private readonly Vintagestory.API.MathTools.Vec4f _tint = new();
    private byte[] _utf8 = new byte[256]; // the text for Cairo, used by whichever thread draws this canvas

    public double Width { get; private set; }
    public double Height { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }

    public bool Ready => _texture.TextureId != 0; // End() has uploaded at least once

    public void Dispose()
    {
        _texture.Dispose();
        Release();
    }

    // The CPU side, width x height x 4 bytes; Draw() needs only the texture, and the next Begin() or BeginMeasure() makes a new one
    private void Release()
    {
        _ctx?.Dispose();
        _surface?.Dispose();
        (_ctx, _surface, _font) = (null, null, null);
    }

    // A measure pass starts here: the fonts may have been resized since the context last set one up
    public void BeginMeasure()
    {
        if (_ctx is null || _surface is null) Surface(SizeStep, SizeStep);
        _font = null;
    }

    // On this canvas's context with the drawing's font: CairoFont.GetTextExtents runs SetupContext on every call, slower and allocating
    public double TextWidth(CairoFont font, string text)
    {
        if (text.Length == 0 || !NotNull(_ctx) || !Assert(font.UnscaledFontsize > 0)) return 0;
        _ = Use(font);
        return _ctx.TextExtents(Utf8(text)).XAdvance;
    }

    public bool Contains(double px, double py) =>
        Finite(px) && Finite(py) && px >= X && px < X + Width && py >= Y && py < Y + Height;

    public void Begin(double width, double height)
    {
        if (!Assert(width > 0) || !Assert(height > 0) || !Assert(width <= MaxSize) ||
            !Assert(height <= MaxSize)) return;
        (Width, Height) = (width, height);
        _font = null; // before anything below can return: a new context has no font set up
        int w = RoundUp(width), h = RoundUp(height);
        if (_ctx is null || _surface is null || _surface.Width != w || _surface.Height != h) Surface(w, h);
        if (!Assert(_surface.Status == Status.Success) || !Source(default)) return;
        _ctx.Operator = Operator.Source;
        _ctx.Paint();
        _ctx.Operator = Operator.Over;
    }

    // A straight line; dash > 0 draws it dashed (the mockup's guide lines)
    public void Line(double x1, double y1, double x2, double y2, Rgba color, double width = 1, double dash = 0)
    {
        if (!Finite(x1 + y1 + x2 + y2) || !Assert(width > 0) || !Source(color)) return;
        _ctx.LineWidth = width;
        if (dash > 0) _ctx.SetDash([dash, dash], 0);
        _ctx.MoveTo(x1, y1);
        _ctx.LineTo(x2, y2);
        _ctx.Stroke();
        if (dash > 0) _ctx.SetDash([], 0);
    }

    // count points through at; NaN y leaves a gap
    public void Polyline(int count, System.Func<int, (double X, double Y)> at, Rgba color, double width)
    {
        if (!NotNull(at) || !Assert(count is >= 0 and <= MaxSize) || count < 2 || !Source(color)) return;
        var open = false;
        for (var i = 0; i < Math.Min(count, MaxSize); i++)
        {
            var (px, py) = at(i);
            if (!double.IsFinite(py)) open = false;
            else if (!open) (open, _) = (true, Move(px, py));
            else _ctx.LineTo(px, py);
        }

        _ctx.LineWidth = width;
        _ctx.Stroke();
    }

    // The area under count points down to bottom, a gradient from color at top to clear at bottom; NaN y counts as the bottom
    public void Area(int count, System.Func<int, (double X, double Y)> at, double top, double bottom, Rgba color)
    {
        if (!NotNull(at) || !NotNull(_ctx) || !Assert(count is >= 0 and <= MaxSize) || count < 2 || !Assert(bottom > top)) return;
        var (x0, _) = at(0);
        _ctx.MoveTo(x0, bottom);
        var last = x0;
        for (var i = 0; i < Math.Min(count, MaxSize); i++)
        {
            var (px, py) = at(i);
            _ctx.LineTo(px, double.IsFinite(py) ? py : bottom);
            last = px;
        }

        _ctx.LineTo(last, bottom);
        _ctx.ClosePath();
        using var gradient = new LinearGradient(0, top, 0, bottom);
        _ = gradient.AddColorStop(0, new Color(color.R, color.G, color.B, color.A));
        _ = gradient.AddColorStop(1, new Color(color.R, color.G, color.B, 0));
        _ctx.SetSource(gradient);
        _ctx.Fill();
    }

    private bool Move(double x, double y)
    {
        if (!NotNull(_ctx) || !Finite(x + y)) return false;
        _ctx.MoveTo(x, y);
        return true;
    }

    public void Circle(double cx, double cy, double r, Rgba color)
    {
        if (!Finite(cx + cy) || !Assert(r > 0) || !Source(color)) return;
        _ctx.Arc(cx, cy, r, 0, 2 * Math.PI);
        _ctx.Fill();
    }

    // A warning sign: a filled triangle pointing up, its box size wide
    public void Triangle(double x, double y, double size, Rgba color)
    {
        if (!Finite(x + y) || !Assert(size > 0) || !Source(color)) return;
        _ctx.MoveTo(x + size / 2, y);
        _ctx.LineTo(x + size, y + size);
        _ctx.LineTo(x, y + size);
        _ctx.ClosePath();
        _ctx.Fill();
    }

    // A disclosure caret in a box of size: pointing right when closed, down when open
    public void Caret(double x, double y, double size, bool down, Rgba color)
    {
        if (!Finite(x + y) || !Assert(size > 0) || !Source(color)) return;
        _ctx.MoveTo(x, y);
        if (down) _ctx.LineTo(x + size, y);
        else _ctx.LineTo(x + size, y + size / 2);
        _ctx.LineTo(down ? x + size / 2 : x, y + size);
        _ctx.ClosePath();
        _ctx.Fill();
    }

    public void Text(double x, double y, double rowHeight, CairoFont font, string text, Rgba? color = null)
    {
        if (text.Length == 0 || !NotNull(_ctx) || !Finite(x) || !Finite(y) || !Assert(rowHeight > 0)) return;
        if (!Use(font) && color is null) FontColor(font.Color);

        if (color is { } c && !Source(c)) return;
        var extents = _ctx.FontExtents;
        _ctx.MoveTo(x, y + (rowHeight - extents.Height * font.LineHeightMultiplier) / 2 + extents.Ascent);
        _ctx.ShowText(Utf8(text));
    }

    public void Fill(double x, double y, double width, double height, Rgba color, double radius = 0)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width >= 0) || !Assert(height >= 0) || !Assert(radius >= 0) ||
            !Source(color)) return;
        RoundRectangle(_ctx, x, y, width, height, radius);
        _ctx.Fill();
    }

    // False when Begin() refused the size
    public bool Blank(double width, double height)
    {
        Begin(width, height);
        return Assert(Width >= width) && Assert(Height >= height);
    }

    public void Clip(double x, double y, double width, double height)
    {
        if (!NotNull(_ctx) || !Assert(width >= 0) || !Assert(height >= 0)) return;
        _ctx.Rectangle(x, y, width, height);
        _ctx.Clip();
    }

    public void Unclip()
    {
        if (NotNull(_ctx)) _ctx.ResetClip();
    }

    public void Outline(double x, double y, double width, double height, Rgba color)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width > 1) || !Assert(height > 1) || !Source(color)) return;
        RoundRectangle(_ctx, x + 0.5, y + 0.5, width - 1, height - 1, 0);
        _ctx.LineWidth = 1;
        _ctx.Stroke();
    }

    public static HudCanvas Grid(ICoreClientAPI capi, double step)
    {
        var canvas = new HudCanvas(capi);
        double width = capi.Render.FrameWidth, height = capi.Render.FrameHeight;
        if (!Assert(step > 0) || !Assert(width > 0) || !Assert(height > 0)) return canvas;
        canvas.Begin(width, height);
        for (var i = 0; i < Math.Min((int)(width / step) + 1, MaxGridLines); i++)
            canvas.Fill(i * step, 0, 1, height, i % 4 == 0 ? GridMajor : GridLine);
        for (var i = 0; i < Math.Min((int)(height / step) + 1, MaxGridLines); i++)
            canvas.Fill(0, i * step, width, 1, i % 4 == 0 ? GridMajor : GridLine);
        canvas.End();
        canvas.Release(); // kept for the session: the texture alone, not the full-screen surface
        return canvas;
    }

    public void End()
    {
        if (!NotNull(_surface) || !Assert(Width > 0)) return; // End() without Begin()
        capi.Gui.LoadOrUpdateCairoTexture(_surface, false, ref _texture);
    }

    // z: the depth among the GUI's own draws (the hotbar's item stacks sit above the default)
    public void Draw(double x, double y, float z = 50)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0) || !Assert(_texture.Width >= Width)) return;
        X = Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width));
        Y = Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height));
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height, z);
    }

    // Where Draw puts it: inside the frame
    public (double X, double Y) Within(double x, double y) => !Finite(x + y) ? (0, 0) :
        (Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width)), Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height)));

    // Faded by alpha and scaled about its centre, the windows' opening; X and Y stay where a click lands
    public void Fade(double x, double y, float z, double alpha, double scale)
    {
        if (!Finite(alpha + scale) || !Assert(scale > 0) || !Assert(_texture.TextureId != 0)) return;
        (X, Y) = Within(x, y);
        var a = (float)Math.Clamp(alpha, 0, 1);
        _ = _tint.Set(a, a, a, a);
        var (w, h) = (_texture.Width * scale, _texture.Height * scale);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X + Width * (1 - scale) / 2, Y + Height * (1 - scale) / 2, w, h,
            z, _tint);
    }

    // This canvas stretched over a box and tinted: on a plain white canvas a quad of any colour (premultiplied: the colour times alpha)
    public void Stretch(double x, double y, double w, double h, float z, Rgba color)
    {
        if (!Finite(x + y) || w <= 0 || h <= 0 || color.A <= 0 || !Assert(_texture.TextureId != 0)) return;
        _ = _tint.Set((float)(color.R * color.A), (float)(color.G * color.A), (float)(color.B * color.A), (float)color.A);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, x, y, w, h, z, _tint);
    }

    // Where it is put, partly off the frame too: a list taller than its view, moved by its scroll under a scissor
    public void Place(double x, double y, float z)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0)) return;
        (X, Y) = (x, y);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height, z);
    }

    // CairoFont.SetupContext sets size and face and allocates a finalizable FontOptions every call; a panel measures and draws a few
    // hundred strings in three fonts, so the context keeps the last one. True when it set the font up, colour included.
    private bool Use(CairoFont font)
    {
        if (ReferenceEquals(font, _font) || !NotNull(_ctx)) return false;
        font.SetupContext(_ctx);
        _font = font;
        return true;
    }

    [MemberNotNull(nameof(_ctx), nameof(_surface))]
    private void Surface(int width, int height)
    {
        _ctx?.Dispose();
        _surface?.Dispose();
        _surface = new ImageSurface(Format.Argb32, width, height);
        _ctx = new Context(_surface);
        _ = Assert(width <= MaxSize && height <= MaxSize);
    }

    // ShowText(string) and TextExtents(string) encode into a new array on every call; an array that ends in 0 they take as it is,
    // and Cairo reads it up to the first 0
    private byte[] Utf8(string text)
    {
        var need = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
        if (_utf8.Length < need) _utf8 = new byte[Math.Max(need, 2 * _utf8.Length)];
        var n = Encoding.UTF8.GetBytes(text, _utf8);
        (_utf8[n], _utf8[^1]) = (0, 0);
        return Assert(n < _utf8.Length) ? _utf8 : [0];
    }

    // What SetupContext sets from the font: RGB or RGBA, nothing for a font without a colour
    private void FontColor(double[]? color)
    {
        if (!NotNull(_ctx) || !Assert(color is null or { Length: 3 or 4 })) return;
        if (color is [var r, var g, var b]) _ctx.SetSourceRGB(r, g, b);
        else if (color is [var r4, var g4, var b4, var a]) _ctx.SetSourceRGBA(r4, g4, b4, a);
    }

    [MemberNotNullWhen(true, nameof(_ctx))]
    private bool Source(Rgba color)
    {
        if (!NotNull(_ctx)) return false;
        _ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        return true;
    }

    private static int RoundUp(double value) =>
        Assert(value is > 0 and <= MaxSize) ? (int)Math.Ceiling(value / SizeStep) * SizeStep : SizeStep;
}

// Rebuilt when the font scale or the game's GUI scale changed (RuntimeEnv.GUIScale changes without an event, and CairoFont scales by
// it). Main thread: CairoFont measures on the engine's shared context.
internal sealed class HudFonts
{
    private const double BaseFontSize = 14, BaseTitleSize = 16, HeaderPad = 2, BadgePad = 3;
    private int _gui;
    private long _scale = BitConverter.DoubleToInt64Bits(double.NaN);

    public CairoFont Text { get; } = CairoFont.WhiteDetailText().WithLineHeightMultiplier(0.9);
    public CairoFont Header { get; } = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);
    public CairoFont Title { get; } = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
    public int Epoch { get; private set; }
    public double TextRow { get; private set; }
    public double HeaderRow { get; private set; }
    public double BadgeRow { get; private set; }

    public void Update(double fontScale)
    {
        var (scale, gui) = (BitConverter.DoubleToInt64Bits(fontScale),
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (scale == _scale && gui == _gui) return;
        (_scale, _gui) = (scale, gui);
        if (!Assert(HudSettings.ScaleRange.Contains(fontScale))) fontScale = 1;
        Text.UnscaledFontsize = Header.UnscaledFontsize = BaseFontSize * fontScale;
        Title.UnscaledFontsize = BaseTitleSize * fontScale;
        var header = Height(Header);
        (TextRow, HeaderRow, BadgeRow) = (Height(Text), header + 2 * scaled(HeaderPad), header + 2 * scaled(BadgePad));
        Epoch++;
        _ = Assert(TextRow > 0 && BadgeRow > 0);
    }

    private static double Height(CairoFont font) =>
        Assert(font.UnscaledFontsize > 0) ? font.GetFontExtents().Height * font.LineHeightMultiplier : 0;
}

// One full-screen texture (2560x1440 is a 14.7 MB surface and a TexImage2D), built on the first drag and again only when the frame
// size or the GUI scale changed; built is told of each build, a frame of its own cost.
internal sealed class SnapGrid(ICoreClientAPI capi, Action built) : IDisposable
{
    private HudCanvas? _canvas;
    private (int Width, int Height, int Scale) _key;

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
    }

    public void Draw()
    {
        var key = (capi.Render.FrameWidth, capi.Render.FrameHeight,
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (!Assert(key.FrameWidth > 0 && key.FrameHeight > 0)) return;
        if (_canvas is null || key != _key)
        {
            _canvas?.Dispose();
            _canvas = HudCanvas.Grid(capi, scaled(HudSettings.SnapStep));
            _key = key;
            built();
        }

        _canvas.Draw(0, 0);
    }
}

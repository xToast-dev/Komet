using System.Diagnostics.CodeAnalysis;
using Cairo;

namespace Komet.Hud;

internal readonly record struct Rgba(double R, double G, double B, double A)
{
    public static Rgba White(double alpha)
    {
        return new Rgba(1, 1, 1, Assert(alpha is >= 0 and <= 1) ? alpha : 1);
    }
}

internal sealed class HudCanvas(ICoreClientAPI capi) : IDisposable
{
    public const int GraphFrames = 240;
    private const int SizeStep = 32, MaxSize = 16384, MaxGridLines = MaxSize / 8, MaxGuides = 4;

    private const double BarW = 120, BarH = 6, GraphMinScaleMs = 20, GraphHeadroom = 1.1;

    public static readonly Rgba PanelBackground = new(0.05, 0.06, 0.09, 1),
        Accent = new(0.16, 0.45, 0.85, 1),
        Neutral = new(0.35, 0.35, 0.35, 1);

    public static readonly Rgba Warning = new(1.0, 0.72, 0.25, 1),
        Error = new(1.0, 0.38, 0.35, 1),
        Good = new(0.35, 0.75, 0.45, 1),
        Dim = Rgba.White(0.5);

    private static readonly (double Ms, string Label)[] GraphGuides =
        [(1000.0 / 60, "60 fps"), (1000.0 / 120, "120 fps")];

    private static readonly Rgba BarTrack = Rgba.White(0.12),
        BarMarker = Rgba.White(0.8),
        HeaderBackground = Rgba.White(0.08),
        RuleLine = Rgba.White(0.3);

    private static readonly Rgba GridLine = Rgba.White(0.07),
        GridMajor = Rgba.White(0.18),
        GraphBackground = Rgba.White(0.06);

    private static readonly Rgba GuideLine = Rgba.White(0.25), GuideText = Rgba.White(0.6), Plot = Rgba.White(0.9);
    private Context? _ctx;
    private CairoFont? _font; // set up on _ctx since Begin() or BeginMeasure()

    private ImageSurface? _surface;
    private LoadedTexture _texture = new(capi);

    public double Width { get; private set; }
    public double Height { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }

    public static double BarWidth => scaled(BarW);

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

    // On this canvas's context, with the font drawing sets up. CairoFont.GetTextExtents runs SetupContext on every call; a measure pass
    // of panel 9 with its detail lines took 144 us and 24.4 KB that way, 119 us and 22.0 KB here (same process, alternating).
    public double TextWidth(CairoFont font, string text)
    {
        if (text.Length == 0 || !NotNull(_ctx) || !Assert(font.UnscaledFontsize > 0)) return 0;
        _ = Use(font);
        return _ctx.TextExtents(text).XAdvance;
    }

    public double BadgeWidth(HudFonts fonts, string text)
    {
        return TextWidth(fonts.Header, text) + HudFonts.BadgePadding;
    }

    public bool Contains(double px, double py)
    {
        return Finite(px) && Finite(py) && px >= X && px < X + Width && py >= Y && py < Y + Height;
    }

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

    public void Text(double x, double y, double rowHeight, CairoFont font, string text, Rgba? color = null)
    {
        if (text.Length == 0 || !NotNull(_ctx) || !Finite(x) || !Finite(y) || !Assert(rowHeight > 0)) return;
        if (!Use(font) && color is null) FontColor(font.Color);

        if (color is { } c && !Source(c)) return;
        var extents = _ctx.FontExtents;
        _ctx.MoveTo(x, y + (rowHeight - extents.Height * font.LineHeightMultiplier) / 2 + extents.Ascent);
        _ctx.ShowText(text);
    }

    public void Fill(double x, double y, double width, double height, Rgba color, double radius = 0)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width >= 0) || !Assert(height >= 0) || !Assert(radius >= 0) ||
            !Source(color)) return;
        RoundRectangle(_ctx, x, y, width, height, radius);
        _ctx.Fill();
    }

    public void Header(double x, double y, double width, double rowHeight, HudFonts fonts, string text)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width > 0) || !Assert(rowHeight > 0)) return;
        var pad = HudFonts.HeaderPadding;
        Fill(x - pad, y, width + 2 * pad, rowHeight, HeaderBackground, scaled(2));
        Text(x, y, rowHeight, fonts.Header, text);
    }

    public void Rule(double x, double y, double width, double rowHeight)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width > 0) || !Assert(rowHeight > 0)) return;
        var thickness = Math.Max(1, scaled(1));
        Fill(x, y + (rowHeight - thickness) / 2, width, thickness, RuleLine);
    }

    // fraction and marker are 0..1; good: a full bar is the good outcome (a hit rate), so the green-to-red ramp runs the other way
    public void Bar(double x, double y, double rowHeight, double w, double fraction, double marker, Rgba? fill = null,
        bool good = false)
    {
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(rowHeight > 0) ||
            !Assert(marker is >= 0 and <= 1)) return;
        double h = scaled(BarH), top = y + (rowHeight - h) / 2, tick = Math.Max(1, scaled(1));
        var tone = good ? 1 - fraction : fraction;
        Fill(x, top, w, h, BarTrack);
        if (fraction > 0 && Assert(fraction <= 1))
            Fill(x, top, w * fraction, h,
                fill ?? new Rgba(Math.Min(1, tone * 2), Math.Min(1, (1 - tone) * 2), 0.15, 0.9));
        if (marker > 0) Fill(x + w * marker - tick, top - tick, tick, h + 2 * tick, BarMarker);
    }

    public double Badge(double x, double y, double rowHeight, HudFonts fonts, string text, Rgba color,
        double? width = null)
    {
        double w = width ?? BadgeWidth(fonts, text), h = fonts.BadgeRow, top = y + (rowHeight - h) / 2;
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(h > 0) || !Assert(rowHeight > 0)) return 0;
        Fill(x, top, w, h, color, scaled(3));
        Text(x + (w - TextWidth(fonts.Header, text)) / 2, top, h, fonts.Header, text);
        return w;
    }

    // Every fourth line a major one, each axis to its own extent
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

    public void Graph(double x, double y, double w, double h, HudFonts fonts, FrameStats frames)
    {
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(h > 0) ||
            !Assert(frames.HistoryLength >= GraphFrames)) return;
        var first = frames.HistoryLength - GraphFrames;
        var skip = GraphFrames -
                   Math.Min(frames.Recorded, GraphFrames); // a slot the ring has not written yet is not a 0 ms frame
        double maxMs = 0;
        Fill(x, y, w, h, GraphBackground);
        for (var i = skip; i < GraphFrames; i++) maxMs = Math.Max(maxMs, frames.HistoryMs(first + i));
        var scaleMs = Math.Max(GraphMinScaleMs, maxMs * GraphHeadroom);
        if (!Assert(scaleMs > 0)) return; // NaN fails too

        double textHeight = fonts.TextRow, lastTextTop = double.MaxValue;
        foreach (var (guideMs, label) in GraphGuides.Bounded(MaxGuides))
        {
            var lineY = ToY(guideMs);
            Fill(x, lineY, w, 1, GuideLine);
            var textTop = lineY - textHeight;
            if (textTop < y || Math.Abs(textTop - lastTextTop) < textHeight) continue;
            Text(x + scaled(3), textTop, textHeight, fonts.Text, label, GuideText);
            lastTextTop = textTop;
        }

        if (!Source(Plot)) return;
        for (var i = skip; i < GraphFrames; i++)
        {
            double px = x + w * i / (GraphFrames - 1), py = ToY(frames.HistoryMs(first + i));
            if (i == skip) _ctx.MoveTo(px, py);
            else _ctx.LineTo(px, py);
        }

        _ctx.LineWidth = 1;
        _ctx.Antialias = Antialias.Fast; // 240 jagged segments: 1.2 ms with the default, 0.4 ms with Fast
        _ctx.Stroke();
        _ctx.Antialias = Antialias.Default;
        return;

        double ToY(double value)
        {
            return Assert(value >= 0) ? y + h - value / scaleMs * h : y + h;
        }
    }

    public void End()
    {
        if (!NotNull(_surface) || !Assert(Width > 0)) return; // End() without Begin()
        capi.Gui.LoadOrUpdateCairoTexture(_surface, false, ref _texture);
    }

    // Kept on screen; the texture is a 32px-rounded superset of the logical size
    public void Draw(double x, double y)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0) || !Assert(_texture.Width >= Width)) return;
        X = Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width));
        Y = Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height));
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height);
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

    private static int RoundUp(double value)
    {
        return Assert(value is > 0 and <= MaxSize) ? (int)Math.Ceiling(value / SizeStep) * SizeStep : SizeStep;
    }
}

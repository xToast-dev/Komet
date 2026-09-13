using System.Diagnostics.CodeAnalysis;
using Cairo;
using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

internal readonly record struct Rgba(double R, double G, double B, double A)
{
    public static Rgba White(double alpha) => new(1, 1, 1, Assert(alpha is >= 0 and <= 1) ? alpha : 1);
}

internal sealed class HudCanvas(ICoreClientAPI capi) : IDisposable
{
    public const int GraphFrames = 240;
    public static readonly Rgba PanelBackground = new(0.05, 0.06, 0.09, 1), Accent = new(0.16, 0.45, 0.85, 1), Neutral = new(0.35, 0.35, 0.35, 1);
    public static readonly Rgba Warning = new(1.0, 0.72, 0.25, 1), Error = new(1.0, 0.38, 0.35, 1), Dim = Rgba.White(0.5);
    private const int SizeStep = 32, MaxSize = 16384, MaxGridLines = MaxSize / 8, MaxGuides = 4;
    private const double BarW = 120, BarH = 6, BadgePad = 3, HeaderPad = 2, RuleH = 9, GraphMinScaleMs = 20, GraphHeadroom = 1.1;
    private static readonly (double Ms, string Label)[] GraphGuides = [(1000.0 / 60, "60 fps"), (1000.0 / 120, "120 fps")];
    private static readonly Rgba BarTrack = Rgba.White(0.12), BarMarker = Rgba.White(0.8), HeaderBackground = Rgba.White(0.08), RuleLine = Rgba.White(0.3);
    private static readonly Rgba GridLine = Rgba.White(0.07), GridMajor = Rgba.White(0.18), GraphBackground = Rgba.White(0.06);
    private static readonly Rgba GuideLine = Rgba.White(0.25), GuideText = Rgba.White(0.6), Plot = Rgba.White(0.9);

    private ImageSurface? _surface;
    private Context? _ctx;
    private LoadedTexture _texture = new(capi);

    public double Width { get; private set; }
    public double Height { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }

    public static double TextWidth(CairoFont font, string text) => text.Length == 0 || !Assert(font.UnscaledFontsize > 0) ? 0 : font.GetTextExtents(text).XAdvance;
    public static double LineHeight(CairoFont font) => Assert(font.UnscaledFontsize > 0) ? font.GetFontExtents().Height * font.LineHeightMultiplier : 0;
    public static double HeaderHeight(CairoFont font) => LineHeight(font) + 2 * scaled(HeaderPad);
    public static double RuleHeight => scaled(RuleH);
    public static double BarWidth => scaled(BarW);
    public static double BadgeWidth(CairoFont font, string text) => TextWidth(font, text) + 2 * scaled(BadgePad);
    public static double BadgeHeight(CairoFont font) => LineHeight(font) + 2 * scaled(BadgePad);

    public bool Contains(double px, double py) => px >= X && px < X + Width && py >= Y && py < Y + Height;

    public void Begin(double width, double height)
    {
        if (!Assert(width > 0) || !Assert(height > 0) || !Assert(width <= MaxSize) || !Assert(height <= MaxSize)) return;
        (Width, Height) = (width, height);
        int w = RoundUp(width), h = RoundUp(height);
        if (_ctx is null || _surface is null || _surface.Width != w || _surface.Height != h)
        {
            _ctx?.Dispose();
            _surface?.Dispose();
            _surface = new ImageSurface(Format.Argb32, w, h);
            _ctx = new Context(_surface);
        }
        if (!Assert(_surface.Status == Status.Success) || !Source(default)) return;
        _ctx.Operator = Operator.Source;
        _ctx.Paint();
        _ctx.Operator = Operator.Over;
    }

    public void Text(double x, double y, double rowHeight, CairoFont font, string text, Rgba? color = null)
    {
        if (text.Length == 0 || !NotNull(_ctx) || !Finite(x) || !Finite(y) || !Assert(rowHeight > 0)) return;
        font.SetupContext(_ctx);
        if (color is { } c && !Source(c)) return;
        var extents = _ctx.FontExtents;
        _ctx.MoveTo(x, y + (rowHeight - extents.Height * font.LineHeightMultiplier) / 2 + extents.Ascent);
        _ctx.ShowText(text);
    }

    public void Fill(double x, double y, double width, double height, Rgba color, double radius = 0)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width >= 0) || !Assert(height >= 0) || !Assert(radius >= 0) || !Source(color)) return;
        RoundRectangle(_ctx, x, y, width, height, radius);
        _ctx.Fill();
    }

    public void Header(double x, double y, double width, double rowHeight, CairoFont font, string text)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width > 0) || !Assert(rowHeight > 0)) return;
        var pad = scaled(HeaderPad);
        Fill(x - pad, y, width + 2 * pad, rowHeight, HeaderBackground, scaled(2));
        Text(x, y, rowHeight, font, text);
    }

    public void Rule(double x, double y, double width, double rowHeight)
    {
        if (!Finite(x) || !Finite(y) || !Assert(width > 0) || !Assert(rowHeight > 0)) return;
        var thickness = Math.Max(1, scaled(1));
        Fill(x, y + (rowHeight - thickness) / 2, width, thickness, RuleLine);
    }

    // fraction and marker are 0..1
    public void Bar(double x, double y, double rowHeight, double w, double fraction, double marker, Rgba? fill = null)
    {
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(rowHeight > 0) || !Assert(marker is >= 0 and <= 1)) return;
        double h = scaled(BarH), top = y + (rowHeight - h) / 2, tick = Math.Max(1, scaled(1));
        Fill(x, top, w, h, BarTrack);
        if (fraction > 0 && Assert(fraction <= 1)) Fill(x, top, w * fraction, h, fill ?? new Rgba(Math.Min(1, fraction * 2), Math.Min(1, (1 - fraction) * 2), 0.15, 0.9));
        if (marker > 0) Fill(x + w * marker - tick, top - tick, tick, h + 2 * tick, BarMarker);
    }

    public double Badge(double x, double y, double rowHeight, CairoFont font, string text, Rgba color, double? width = null)
    {
        double w = width ?? BadgeWidth(font, text), h = BadgeHeight(font), top = y + (rowHeight - h) / 2;
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(h > 0) || !Assert(rowHeight > 0)) return 0;
        Fill(x, top, w, h, color, scaled(3));
        Text(x + (w - TextWidth(font, text)) / 2, top, h, font, text);
        return w;
    }

    public static HudCanvas Grid(ICoreClientAPI capi, double step)
    {
        var canvas = new HudCanvas(capi);
        double width = capi.Render.FrameWidth, height = capi.Render.FrameHeight;
        if (!Assert(step > 0) || !Assert(width > 0) || !Assert(height > 0)) return canvas;
        canvas.Begin(width, height);
        var lines = (int)(Math.Max(width, height) / step) + 1;
        for (var i = 0; i < Math.Min(lines, MaxGridLines); i++)
        {
            var color = i % 4 == 0 ? GridMajor : GridLine;
            canvas.Fill(i * step, 0, 1, height, color);
            canvas.Fill(0, i * step, width, 1, color);
        }
        canvas.End();
        return canvas;
    }

    public void Graph(double x, double y, double w, double h, CairoFont font, FrameStats frames)
    {
        if (!Finite(x) || !Finite(y) || !Assert(w > 0) || !Assert(h > 0) || !Assert(frames.HistoryLength >= GraphFrames)) return;
        var first = frames.HistoryLength - GraphFrames;
        double maxMs = 0;
        Fill(x, y, w, h, GraphBackground);
        for (var i = 0; i < GraphFrames; i++) maxMs = Math.Max(maxMs, frames.HistoryMs(first + i));
        var scaleMs = Math.Max(GraphMinScaleMs, maxMs * GraphHeadroom);
        if (!Assert(scaleMs > 0)) return;   // NaN fails too

        double textHeight = LineHeight(font), lastTextTop = double.MaxValue;
        foreach (var (guideMs, label) in GraphGuides.Bounded(MaxGuides))
        {
            var lineY = ToY(guideMs);
            Fill(x, lineY, w, 1, GuideLine);
            var textTop = lineY - textHeight;
            if (textTop < y || Math.Abs(textTop - lastTextTop) < textHeight) continue;
            Text(x + scaled(3), textTop, textHeight, font, label, GuideText);
            lastTextTop = textTop;
        }

        if (!Source(Plot)) return;
        for (var i = 0; i < GraphFrames; i++)
        {
            double px = x + w * i / (GraphFrames - 1), py = ToY(frames.HistoryMs(first + i));
            if (i == 0) _ctx.MoveTo(px, py); else _ctx.LineTo(px, py);
        }
        _ctx.LineWidth = 1;
        _ctx.Stroke();
        return;

        double ToY(double value) => Assert(value >= 0) ? y + h - value / scaleMs * h : y + h;
    }

    public void End()
    {
        if (!NotNull(_surface) || !Assert(Width > 0)) return;   // End() without Begin()
        capi.Gui.LoadOrUpdateCairoTexture(_surface, linearMag: false, ref _texture);
    }

    // Kept on screen; the texture is a 32px-rounded superset of the logical size
    public void Draw(double x, double y)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0) || !Assert(_texture.Width >= Width)) return;
        X = Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width));
        Y = Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height));
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height);
    }

    [MemberNotNullWhen(true, nameof(_ctx))]
    private bool Source(Rgba color)
    {
        if (!NotNull(_ctx)) return false;
        _ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        return true;
    }

    private static int RoundUp(double value) => Assert(value is > 0 and <= MaxSize) ? (int)Math.Ceiling(value / SizeStep) * SizeStep : SizeStep;

    public void Dispose()
    {
        _texture.Dispose();
        _ctx?.Dispose();
        _surface?.Dispose();
        (_ctx, _surface) = (null, null);
    }
}

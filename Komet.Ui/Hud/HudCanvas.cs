using SkiaSharp;
using Vintagestory.API.MathTools;

namespace Komet.Hud;

internal readonly record struct Rgba(double R, double G, double B, double A)
{
    public static Rgba White(double alpha) => new(1, 1, 1, Assert(alpha is >= 0 and <= 1) ? alpha : 1);

    public static Rgba Black(double alpha) => new(0, 0, 0, Assert(alpha is >= 0 and <= 1) ? alpha : 1);
}

// Draws recorded on the main thread, painted by SkiaSharp at End() there or (background) on a worker; the main thread
// only uploads. A page not yet painted when a newer one ends is dropped.
internal sealed partial class HudCanvas(ICoreClientAPI capi, bool background = false) : IDisposable
{
    public const int GraphFrames = 240;
    private const int SizeStep = 32, MaxSize = 16384, MaxGridLines = MaxSize / 8, MaxCmds = 1 << 16;

    private const int MaxCuts = 16;

    // The HUD's palette under the names the options screen and the update notice use
    public static readonly Rgba Warning = HudUi.Warn, Error = HudUi.Err, Good = HudUi.Ok;

    private static readonly Rgba GridLine = Rgba.White(0.07), GridMajor = Rgba.White(0.18);

    // ReSharper disable once ReplaceWithPrimaryConstructorParameter - the other partial files use it, not the parameter
    private readonly bool _background = background;

    // ReSharper disable once ReplaceWithPrimaryConstructorParameter - the other partial files use it, not the parameter
    private readonly ICoreClientAPI _capi = capi; // the primary constructor's parameters reach only this file

    private readonly Vec4f _tint = new();
    private Page? _abandoned; // the page painting when Drop() came: thrown away, not uploaded

    private SKCanvas? _ctx; // the painter's: the worker's while a job runs
    private bool _dropped; // the shown page is hidden until the next upload
    private bool _faulted; // a failed paint is logged once
    private CairoFont? _font;
    private double _fontSize; // the size _font was set up with: a text records the size it was drawn at
    private Task? _job;

    private SKFont? _measure; // the main thread's, kept so measuring in a loop allocates no font
    private CairoFont? _measureFont; // set up on _measure since Blank() or BeginMeasure()
    private double _measureSize;
    private int[] _pixels = []; // the upload buffer, reused while the surface keeps its size
    private Page? _next, _busy, _spare; // waiting to be painted, being painted, free

    private Page _page = new();
    private SKBitmap? _surface;
    private LoadedTexture _texture = new(capi);

    public double Width { get; private set; }
    public double Height { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }

    public bool Ready
    {
        get
        {
            if (_background) Pump();

            return !_dropped && _texture.TextureId != 0;
        }
    }

    // Multiplies the alpha of everything recorded while it is below 1 (a list fading in)
    public double Opacity { get; set; } = 1;

    // A background canvas shows the last page a while after End()
    public object? Shown { get; private set; }

    public void Dispose()
    {
        if (_job is not null)
        {
            _ = ((IAsyncResult)_job).AsyncWaitHandle.WaitOne(); // a worker still painting into the surface
        }

        _texture.Dispose();
        Release();
        _measure?.Dispose();
        (_measure, _measureFont) = (null, null);
    }

    private void Release()
    {
        _ctx?.Dispose();
        _surface?.Dispose();
        (_ctx, _surface, _font) = (null, null, null);
    }

    public void BeginMeasure()
    {
        _measure ??= new SKFont { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _measureFont = null;
    }

    // MeasureText allocates no glyph buffer of its own; the font is set up per font and size only
    public double TextWidth(CairoFont font, string text)
    {
        if (text.Length == 0 || !Assert(font.UnscaledFontsize > 0)) return 0;

        if (_measure is null) BeginMeasure();

        var size = scaled(font.UnscaledFontsize);
        if (!ReferenceEquals(font, _measureFont) || Math.Abs(size - _measureSize) > 1e-9)
        {
            SkiaFonts.Setup(_measure!, font, size);
        }

        (_measureFont, _measureSize) = (font, size);
        return _measure!.MeasureText(text);
    }

    // The longest start of text that fits width with an ellipsis (whole if it fits). trimmed: at least one character, and
    // no space before the ellipsis (the options screen's rows).
    public string Fit(CairoFont font, string text, double width, bool trimmed = false)
    {
        if (!NotNull(text)) return "";

        if (!NotNull(font) || !Finite(width)) return text;

        if (text.Length == 0 || TextWidth(font, text) <= width) return text;

        var (lo, hi) = (0, text.Length);
        for (var step = 0; step < 16 && lo < hi; step++)
        {
            var mid = (lo + hi + 1) / 2;
            if (TextWidth(font, text[..mid] + "…") <= width)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return trimmed ? text[..Math.Max(1, lo)].TrimEnd() + "…" : text[..lo] + "…";
    }

    // Greedy wrap of one paragraph at its spaces, at most maxWords words: a line takes words while it fits width. A lone
    // word wider than that (a path) breaks after its last '/' that fits, else after the last character that fits.
    public void Wrap(CairoFont font, string paragraph, double width, int maxWords, List<string> into)
    {
        if (!NotNull(paragraph)) return;

        var line = "";
        foreach (var word in paragraph.Split(' ').Bounded(maxWords))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && TextWidth(font, next) > width)
            {
                into.Add(line);
                next = word;
            }

            for (var cut = 0; cut < MaxCuts && next.Length > 1 && TextWidth(font, next) > width; cut++)
            {
                var at = Cut(font, next, width);
                into.Add(next[..at]);
                next = next[at..];
            }

            line = next;
        }

        into.Add(line);
    }

    // Where a too wide text breaks: after the last '/' that fits, else after the last character that fits; at least one
    public int Cut(CairoFont font, string text, double width)
    {
        var (lo, hi) = (1, text.Length - 1);
        for (var step = 0; step < MaxCuts * 2 && lo < hi; step++) // the most characters that fit (binary search)
        {
            var mid = (lo + hi + 1) / 2;
            if (TextWidth(font, text[..mid]) <= width)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var slash = text.LastIndexOf('/', lo - 1);
        return slash > 0 ? slash + 1 : lo;
    }

    public bool Contains(double px, double py) =>
        Finite(px) && Finite(py) && px >= X && px < X + Width && py >= Y && py < Y + Height;

    public bool Blank(double width, double height)
    {
        if (!Assert(width > 0) || !Assert(height > 0) || !Assert(width <= MaxSize) ||
            !Assert(height <= MaxSize))
        {
            return false;
        }

        (_page.W, _page.H, _measureFont) = (width, height, null);
        _page.Cmds.Clear();
        _page.Points.Clear();
        return true;
    }

    private void Add(in Cmd cmd)
    {
        if (!Assert(_page.W > 0) || !Assert(_page.Cmds.Count < MaxCmds))
        {
            return; // W 0: a draw without Blank()
        }

        if (Opacity >= 1 || cmd.Op is Op.Clip or Op.Unclip)
        {
            _page.Cmds.Add(cmd);
        }
        else if (cmd is { Op: Op.Text, Flag: false, Font.Color: [var r, var g, var b, ..] color })
        {
            _page.Cmds.Add(cmd with
            {
                Flag = true, Color = new Rgba(r, g, b, (color.Length > 3 ? color[3] : 1) * Math.Max(0, Opacity))
            });
        }
        else
        {
            _page.Cmds.Add(cmd with { Color = cmd.Color with { A = cmd.Color.A * Math.Max(0, Opacity) } });
        }
    }
}

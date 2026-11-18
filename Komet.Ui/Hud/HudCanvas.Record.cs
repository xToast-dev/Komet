using Cairo;

namespace Komet.Hud;

// HudCanvas: the drawing commands a page records (shapes, text, images, clips) and its end.
internal sealed partial class HudCanvas
{
    // An image fitted into the square at x, y, centred: whole-number steps upward with sharp pixels (pixel art), whole
    // fractions (1/2, 1/3) downward, averaged evenly. The surface must outlive the paint (a worker may still read it).
    public void Image(ImageSurface image, double x, double y, double size)
    {
        if (NotNull(image) && Finite(x + y) && Assert(size > 0) && image is { Width: > 0, Height: > 0 })
        {
            Add(new Cmd(Op.Image, x, y, size, Color: Rgba.White(1), Image: image));
        }
    }

    // The side an image is drawn at when fitted into a square of size (Image's whole steps)
    public static double Fitted(ImageSurface image, double size)
    {
        if (!NotNull(image) || !Assert(size > 0) || image.Width <= 0 || image.Height <= 0) return 0;

        var side = Math.Max(image.Width, image.Height);
        var scale = size / side;
        return side * (scale >= 1 ? Math.Floor(scale) : 1 / Math.Ceiling(1 / scale));
    }

    public void Line(double x1, double y1, double x2, double y2, Rgba color, double width = 1, double dash = 0)
    {
        if (Finite(x1 + y1 + x2 + y2) && Assert(width > 0)) Add(new Cmd(Op.Line, x1, y1, x2, y2, width, dash, color));
    }

    // Gradient area from fill to clear (NaN y: bottom) and the line (NaN y: gap)
    public void Graph(int count, System.Func<int, (double X, double Y)> at, (double Top, double Bottom) span, Rgba fill,
        (Rgba Color, double Width) line)
    {
        if (!NotNull(at) || !Assert(count is >= 0 and <= MaxSize) || count < 2 ||
            !Assert(span.Bottom > span.Top))
        {
            return;
        }

        var from = Points(count, at);
        Add(new Cmd(Op.Area, span.Top, span.Bottom, Color: fill, From: from, Count: count));
        Add(new Cmd(Op.Polyline, 0, 0, E: line.Width, Color: line.Color, From: from, Count: count));
    }

    private int Points(int count, System.Func<int, (double X, double Y)> at)
    {
        var from = _page.Points.Count;
        for (var i = 0; i < Math.Min(count, MaxSize); i++)
        {
            var (px, py) = at(i);
            _page.Points.Add(px);
            _page.Points.Add(py);
        }

        return from;
    }

    public void Circle(double cx, double cy, double r, Rgba color)
    {
        if (Finite(cx + cy) && Assert(r > 0)) Add(new Cmd(Op.Circle, cx, cy, r, Color: color));
    }

    public void Arc(double cx, double cy, double r, double from, double to, Rgba color, double width)
    {
        if (Finite(cx + cy + from + to) && Assert(r > 0) && Assert(width > 0))
        {
            Add(new Cmd(Op.Arc, cx, cy, r, from, to, width, color));
        }
    }

    // A filled arrowhead with a notch, centred at cx, cy, pointing right (true) or left
    public void Arrow(double cx, double cy, double size, bool right, Rgba color)
    {
        if (Finite(cx + cy) && Assert(size > 0)) Add(new Cmd(Op.Arrow, cx, cy, size, Color: color, Flag: right));
    }

    public void Caret(double x, double y, double size, bool down, Rgba color)
    {
        if (Finite(x + y) && Assert(size > 0)) Add(new Cmd(Op.Caret, x, y, size, Color: color, Flag: down));
    }

    // The font's size is taken here: the main thread resizes fonts while a worker paints the page
    public void Text(double x, double y, double rowHeight, CairoFont font, string text, Rgba? color = null)
    {
        if (text.Length > 0 && NotNull(font) && Finite(x) && Finite(y) && Assert(rowHeight > 0))
        {
            Add(new Cmd(Op.Text, x, y, rowHeight, scaled(font.UnscaledFontsize), Color: color ?? default, Font: font,
                Text: text,
                Flag: color is not null));
        }
    }

    public void Fill(double x, double y, double width, double height, Rgba color, double radius = 0)
    {
        if (Finite(x) && Finite(y) && Assert(width >= 0) && Assert(height >= 0) && Assert(radius >= 0))
        {
            Add(new Cmd(Op.Fill, x, y, width, height, radius, Color: color));
        }
    }

    public void Clip(double x, double y, double width, double height)
    {
        if (Assert(width >= 0) && Assert(height >= 0)) Add(new Cmd(Op.Clip, x, y, width, height));
    }

    public void Unclip() => Add(new Cmd(Op.Unclip, 0, 0));

    public void Outline(double x, double y, double width, double height, Rgba color)
    {
        if (Finite(x) && Finite(y) && Assert(width > 1) && Assert(height > 1))
        {
            Add(new Cmd(Op.Outline, x, y, width, height, Color: color));
        }
    }

    public static HudCanvas Grid(ICoreClientAPI capi, double step)
    {
        var canvas = new HudCanvas(capi);
        double width = capi.Render.FrameWidth, height = capi.Render.FrameHeight;
        if (!Assert(step > 0) || !Assert(width > 0) || !Assert(height > 0) || !canvas.Blank(width, height))
        {
            return canvas;
        }

        for (var i = 0; i < Math.Min((int)(width / step) + 1, MaxGridLines); i++)
        {
            canvas.Fill(i * step, 0, 1, height, i % 4 == 0 ? GridMajor : GridLine);
        }

        for (var i = 0; i < Math.Min((int)(height / step) + 1, MaxGridLines); i++)
        {
            canvas.Fill(0, i * step, width, 1, i % 4 == 0 ? GridMajor : GridLine);
        }

        canvas.End();
        canvas.Release(); // kept while dragging: the texture alone, not the full-screen surface
        return canvas;
    }

    // Hides the shown page and the pages still waiting until a newer one is uploaded: a dialog opened again shows nothing
    // rather than its last picture
    public void Drop()
    {
        (Shown, _dropped, _abandoned) = (null, true, _busy);
        if (_next is null) return;

        _spare ??= _next;
        _next = null;
    }

    public bool Holds(object tag) =>
        NotNull(tag) && (ReferenceEquals(Shown, tag) || ReferenceEquals(_next?.Tag, tag) ||
                                ReferenceEquals(_busy?.Tag, tag));

    public void End(object? tag = null)
    {
        if (!Assert(_page.W > 0))
        {
            return; // End() without Blank()
        }

        _page.Tag = tag;
        if (!_background)
        {
            Paint(_page);
            Upload(_page);
            return;
        }

        var free = _next ?? _spare ?? new Page();
        if (ReferenceEquals(free, _spare)) _spare = null;

        (_next, _page) = (_page, free);
        Pump();
    }
}

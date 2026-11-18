using System.Runtime.InteropServices;
using Cairo;

namespace Komet.Hud;

// HudCanvas: painting a recorded page with Cairo off the main thread and uploading it.
internal sealed partial class HudCanvas
{
    // A paint that threw leaves its context in an error state: it is dropped, the next page paints on a new one
    private void Pump()
    {
        if (_job is { IsCompleted: true } done)
        {
            if (done.IsFaulted)
            {
                if (!_faulted)
                {
                    _capi.Logger.Warning("Komet HUD: painting failed: {0}", done.Exception?.GetBaseException());
                }

                _faulted = true;
                Release();
            }
            else if (Assert(done.IsCompletedSuccessfully) && NotNull(_busy) && !ReferenceEquals(_busy, _abandoned))
            {
                Upload(_busy);
            }

            _abandoned = null;
            (_spare, _busy, _job) = (_busy, null, null);
        }

        if (_job is not null || _next is not { } next) return;

        (_busy, _next) = (next, null);
        _job = Task.Run(() => Paint(next));
    }

    private void Upload(Page page)
    {
        if (!NotNull(_surface) || !Assert(page is { W: > 0, H: > 0 }))
        {
            return; // only pages End() took are painted
        }

        _capi.Gui.LoadOrUpdateCairoTexture(_surface, false, ref _texture);
        (Width, Height, Shown, _dropped) = (page.W, page.H, page.Tag, false);
    }

    private void Paint(Page page)
    {
        int w = RoundUp(page.W), h = RoundUp(page.H);
        if (_ctx is null || _surface is null || _surface.Width != w || _surface.Height != h ||
            _ctx.Status != Status.Success)
        {
            Surface(w, h);
        }

        if (!Assert(_surface.Status == Status.Success)) return;

        var ctx = _ctx;
        _font = null; // a new page may come with resized fonts
        ctx.Operator = Operator.Source;
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();
        ctx.Operator = Operator.Over;
        var cmds = CollectionsMarshal.AsSpan(page.Cmds);
        var points = CollectionsMarshal.AsSpan(page.Points);
        for (var i = 0; i < Math.Min(cmds.Length, MaxCmds); i++)
        {
            Paint(ctx, cmds[i], points);
        }

        ctx.ResetClip();
        _surface.Flush();
    }

    private void Paint(Context ctx, in Cmd c, ReadOnlySpan<double> points)
    {
        if (c.Op is not (Op.Text or Op.Clip or Op.Unclip)) Source(ctx, c.Color);

        switch (c.Op)
        {
            case Op.Fill:
                RoundRectangle(ctx, c.A, c.B, c.C, c.D, c.E);
                ctx.Fill();
                break;
            case Op.Text:
                Text(ctx, c);
                break;
            case Op.Line:
                Line(ctx, c);
                break;
            case Op.Polyline:
                Polyline(ctx, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Area:
                Area(ctx, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Circle:
                ctx.Arc(c.A, c.B, c.C, 0, 2 * Math.PI);
                ctx.Fill();
                break;
            case Op.Arc:
                Bow(ctx, c);
                break;
            case Op.Caret:
                ctx.MoveTo(c.A, c.B);
                ctx.LineTo(c.A + c.C, c.Flag ? c.B : c.B + c.C / 2);
                ctx.LineTo(c.Flag ? c.A + c.C / 2 : c.A, c.B + c.C);
                ctx.ClosePath();
                ctx.Fill();
                break;
            case Op.Clip:
                ctx.Rectangle(c.A, c.B, c.C, c.D);
                ctx.Clip();
                break;
            case Op.Unclip:
                ctx.ResetClip();
                break;
            case Op.Outline:
                Outline(ctx, c);
                break;
            case Op.Image:
                Picture(ctx, c);
                break;
            case Op.Arrow:
                Arrowhead(ctx, c);
                break;
        }
    }

    private static void Bow(Context ctx, in Cmd c)
    {
        ctx.LineWidth = c.F;
        ctx.NewPath();
        ctx.Arc(c.A, c.B, c.C, c.D, c.E);
        ctx.Stroke();
    }

    private static void Arrowhead(Context ctx, in Cmd c)
    {
        var (d, s) = (c.Flag ? 1 : -1, c.C / 2);
        ctx.MoveTo(c.A + d * s * 0.7, c.B);
        ctx.LineTo(c.A - d * s * 0.7, c.B - s);
        ctx.LineTo(c.A - d * s * 0.25, c.B);
        ctx.LineTo(c.A - d * s * 0.7, c.B + s);
        ctx.ClosePath();
        ctx.Fill();
    }

    private static void Outline(Context ctx, in Cmd c)
    {
        RoundRectangle(ctx, c.A + 0.5, c.B + 0.5, c.C - 1, c.D - 1, 0);
        ctx.LineWidth = 1;
        ctx.Stroke();
    }

    private static void Picture(Context ctx, in Cmd c)
    {
        if (c.Image is not { } image || !Assert(c.C > 0)) return;

        var scale = c.C / Math.Max(image.Width, image.Height);
        scale = scale >= 1
            ? Math.Floor(scale)
            : 1 / Math.Ceiling(1 / scale); // whole steps both ways: pixel art stays even
        var (w, h) = (image.Width * scale, image.Height * scale);
        ctx.Save();
        ctx.Translate(Math.Round(c.A + (c.C - w) / 2), Math.Round(c.B + (c.C - h) / 2));
        ctx.Scale(scale, scale);
        using var pattern = new SurfacePattern(image);
        pattern.Filter = scale >= 1 ? Filter.Nearest : Filter.Best;
        ctx.SetSource(pattern);
        ctx.Rectangle(0, 0, image.Width, image.Height);
        ctx.Clip();
        ctx.PaintWithAlpha(c.Color.A);
        ctx.Restore();
    }

    private static void Line(Context ctx, in Cmd c)
    {
        ctx.LineWidth = c.E;
        if (c.F > 0) ctx.SetDash([c.F, c.F], 0);

        ctx.MoveTo(c.A, c.B);
        ctx.LineTo(c.C, c.D);
        ctx.Stroke();
        if (c.F > 0) ctx.SetDash([], 0);
    }

    private void Text(Context ctx, in Cmd c)
    {
        if (c.Font is not { } font || !NotNull(c.Text) || !Assert(c.D > 0))
        {
            return; // Text() records both and the size
        }

        var setUp = !ReferenceEquals(font, _font) || Math.Abs(c.D - _fontSize) > 1e-9;
        if (setUp) Setup(ctx, font, c.D);

        (_font, _fontSize) = (font, c.D);
        if (c.Flag)
        {
            Source(ctx, c.Color);
        }
        else if (!setUp)
        {
            FontColor(ctx, font.Color);
        }

        var extents = ctx.FontExtents;
        ctx.MoveTo(c.A, c.B + (c.C - extents.Height * font.LineHeightMultiplier) / 2 + extents.Ascent);
        ctx.ShowText(Utf8(ref _text, c.Text));
    }

    private static void Polyline(Context ctx, in Cmd c, ReadOnlySpan<double> xy)
    {
        var open = false;
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
        {
            if (!double.IsFinite(xy[i + 1]))
            {
                open = false;
            }
            else if (open)
            {
                ctx.LineTo(xy[i], xy[i + 1]);
            }
            else if (double.IsFinite(xy[i]))
            {
                ctx.MoveTo(xy[i], xy[i + 1]);
                open = true;
            }
        }

        ctx.LineWidth = c.E;
        ctx.Stroke();
    }

    private static void Area(Context ctx, in Cmd c, ReadOnlySpan<double> xy)
    {
        ctx.MoveTo(xy[0], c.B);
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
        {
            ctx.LineTo(xy[i], double.IsFinite(xy[i + 1]) ? xy[i + 1] : c.B);
        }

        ctx.LineTo(xy[^2], c.B);
        ctx.ClosePath();
        using var gradient = new LinearGradient(0, c.A, 0, c.B);
        _ = gradient.AddColorStop(0, new Color(c.Color.R, c.Color.G, c.Color.B, c.Color.A));
        _ = gradient.AddColorStop(1, new Color(c.Color.R, c.Color.G, c.Color.B, 0));
        ctx.SetSource(gradient);
        ctx.Fill();
    }
}

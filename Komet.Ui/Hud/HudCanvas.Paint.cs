using System.Runtime.InteropServices;
using SkiaSharp;

namespace Komet.Hud;

// HudCanvas: painting a recorded page with SkiaSharp off the main thread and uploading it.
internal sealed partial class HudCanvas
{
    // A paint that threw leaves its canvas in an error state: it is dropped, the next page paints on a new one
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
        if (_surface is not { } bitmap || !Assert(page is { W: > 0, H: > 0 })) return;

        var count = bitmap.Width * bitmap.Height;
        if (_pixels.Length != count) _pixels = new int[count];
        Marshal.Copy(bitmap.GetPixels(), _pixels, 0, count); // BGRA, premultiplied: the engine's upload seam

        (_texture.Width, _texture.Height) = (bitmap.Width, bitmap.Height);
        _capi.Render.LoadOrUpdateTextureFromBgra(_pixels, false, 0, ref _texture);
        (Width, Height, Shown, _dropped) = (page.W, page.H, page.Tag, false);
    }

    private void Paint(Page page)
    {
        int w = RoundUp(page.W), h = RoundUp(page.H);
        if (_ctx is null || _surface is null || _surface.Width != w || _surface.Height != h)
        {
            Surface(w, h);
        }

        if (_surface is null || _ctx is not { } ctx) return;

        _font = null; // a new page may come with resized fonts
        ctx.Clear(); // a transparent page: Skia's equivalent of Cairo's Source + Paint
        ctx.RestoreToCount(0);

        using var paint = new SKPaint { IsAntialias = true };
        using var font = new SKFont { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        var cmds = CollectionsMarshal.AsSpan(page.Cmds);
        var points = CollectionsMarshal.AsSpan(page.Points);
        for (var i = 0; i < Math.Min(cmds.Length, MaxCmds); i++)
        {
            Paint(ctx, paint, font, cmds[i], points);
        }

        ctx.RestoreToCount(0);
        _surface.SetImmutable();
    }

    private void Paint(SKCanvas ctx, SKPaint paint, SKFont font, in Cmd c, ReadOnlySpan<double> points)
    {
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = null;
        paint.PathEffect = null;
        if (c.Op is not (Op.Text or Op.Clip or Op.Unclip)) Source(paint, c.Color);
        Draw(ctx, paint, font, c, points);
    }

    // One recorded command; the clip's Save() pairs with the following Unclip()'s Restore()
    private void Draw(SKCanvas ctx, SKPaint paint, SKFont font, in Cmd c, ReadOnlySpan<double> points)
    {
        switch (c.Op)
        {
            case Op.Fill:
                using (var fill = RoundRect(c.A, c.B, c.C, c.D, c.E))
                {
                    ctx.DrawPath(fill, paint);
                }
                break;
            case Op.Text:
                Text(ctx, paint, font, c);
                break;
            case Op.Line:
                Line(ctx, paint, c);
                break;
            case Op.Polyline:
                Polyline(ctx, paint, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Area:
                Area(ctx, paint, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Circle:
                ctx.DrawCircle((float)c.A, (float)c.B, (float)c.C, paint);
                break;
            case Op.Arc:
                Bow(ctx, paint, c);
                break;
            case Op.Caret:
                Caret(ctx, paint, c);
                break;
            case Op.Clip:
                _ = ctx.Save();
                ctx.ClipRect(Rect(c.A, c.B, c.C, c.D));
                break;
            case Op.Unclip:
                ctx.Restore();
                break;
            case Op.Outline:
                Outline(ctx, paint, c);
                break;
            case Op.Image:
                Picture(ctx, paint, c);
                break;
            case Op.Arrow:
                using (var head = Arrowhead(c))
                {
                    ctx.DrawPath(head, paint);
                }
                break;
        }
    }

    private static void Caret(SKCanvas ctx, SKPaint paint, in Cmd c)
    {
        using var path = new SKPath();
        path.MoveTo((float)c.A, (float)c.B);
        path.LineTo((float)(c.A + c.C), (float)(c.Flag ? c.B : c.B + c.C / 2));
        path.LineTo((float)(c.Flag ? c.A + c.C / 2 : c.A), (float)(c.B + c.C));
        path.Close();
        ctx.DrawPath(path, paint);
    }

    private static SKPath Arrowhead(in Cmd c)
    {
        var (d, s) = (c.Flag ? 1 : -1, c.C / 2);
        var path = new SKPath();
        path.MoveTo((float)(c.A + d * s * 0.7), (float)c.B);
        path.LineTo((float)(c.A - d * s * 0.7), (float)(c.B - s));
        path.LineTo((float)(c.A - d * s * 0.25), (float)c.B);
        path.LineTo((float)(c.A - d * s * 0.7), (float)(c.B + s));
        path.Close();
        return path;
    }

    private static void Outline(SKCanvas ctx, SKPaint paint, in Cmd c)
    {
        using var path = RoundRect(c.A + 0.5, c.B + 0.5, c.C - 1, c.D - 1, 0);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        ctx.DrawPath(path, paint);
    }

    private static void Picture(SKCanvas ctx, SKPaint paint, in Cmd c)
    {
        if (c.Image is not { } image || !Assert(c.C > 0)) return;

        var scale = c.C / Math.Max(image.Width, image.Height);
        scale = scale >= 1
            ? Math.Floor(scale)
            : 1 / Math.Ceiling(1 / scale); // whole steps both ways: pixel art stays even
        var (w, h) = (image.Width * scale, image.Height * scale);
        using var source = SKImage.FromBitmap(image);
        var sampling = new SKSamplingOptions(scale >= 1 ? SKFilterMode.Nearest : SKFilterMode.Linear);
        _ = ctx.Save();
        ctx.Translate((float)Math.Round(c.A + (c.C - w) / 2), (float)Math.Round(c.B + (c.C - h) / 2));
        ctx.Scale((float)scale, (float)scale);
        ctx.DrawImage(source, 0, 0, sampling, paint);
        ctx.Restore();
    }

    private static void Line(SKCanvas ctx, SKPaint paint, in Cmd c)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = (float)c.E;
        if (c.F > 0) paint.PathEffect = SKPathEffect.CreateDash([(float)c.F, (float)c.F], 0);

        ctx.DrawLine((float)c.A, (float)c.B, (float)c.C, (float)c.D, paint);
    }

    private void Text(SKCanvas ctx, SKPaint paint, SKFont font, in Cmd c)
    {
        if (c.Font is not { } face || !NotNull(c.Text) || !Assert(c.D > 0)) return;

        var setUp = !ReferenceEquals(face, _font) || Math.Abs(c.D - _fontSize) > 1e-9;
        if (setUp) Setup(font, face, c.D);

        (_font, _fontSize) = (face, c.D);
        if (c.Flag)
        {
            Source(paint, c.Color);
        }
        else
        {
            FontColor(paint, face.Color);
        }

        var height = SkiaFonts.Height(font);
        var y = c.B + (c.C - height * face.LineHeightMultiplier) / 2 + SkiaFonts.Ascent(font);
        ctx.DrawText(c.Text, (float)c.A, (float)y, font, paint);
    }

    private static void Polyline(SKCanvas ctx, SKPaint paint, in Cmd c, ReadOnlySpan<double> xy)
    {
        using var path = new SKPath();
        var open = false;
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
        {
            if (!double.IsFinite(xy[i + 1]))
            {
                open = false;
            }
            else if (open)
            {
                path.LineTo((float)xy[i], (float)xy[i + 1]);
            }
            else if (double.IsFinite(xy[i]))
            {
                path.MoveTo((float)xy[i], (float)xy[i + 1]);
                open = true;
            }
        }

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = (float)c.E;
        ctx.DrawPath(path, paint);
    }

    private static void Area(SKCanvas ctx, SKPaint paint, in Cmd c, ReadOnlySpan<double> xy)
    {
        using var path = new SKPath();
        path.MoveTo((float)xy[0], (float)c.B);
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
        {
            path.LineTo((float)xy[i], (float)(double.IsFinite(xy[i + 1]) ? xy[i + 1] : c.B));
        }

        path.LineTo((float)xy[^2], (float)c.B);
        path.Close();
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, (float)c.A), new SKPoint(0, (float)c.B),
            [SkiaFonts.Color(c.Color), SkiaFonts.Color(c.Color with { A = 0 })], SKShaderTileMode.Clamp);
        paint.Shader = gradient;
        ctx.DrawPath(path, paint);
    }

    private static void Bow(SKCanvas ctx, SKPaint paint, in Cmd c)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = (float)c.F;
        using var path = new SKPath();
        path.AddArc(Rect(c.A - c.C, c.B - c.C, 2 * c.C, 2 * c.C),
            (float)(c.D * 180 / Math.PI), (float)((c.E - c.D) * 180 / Math.PI));
        ctx.DrawPath(path, paint);
    }

    private static SKPath RoundRect(double x, double y, double w, double h, double r)
    {
        var path = new SKPath();
        var rect = Rect(x, y, w, h);
        if (r > 0)
        {
            path.AddRoundRect(rect, (float)Math.Min(r, w / 2), (float)Math.Min(r, h / 2));
        }
        else
        {
            path.AddRect(rect);
        }

        return path;
    }

    private static SKRect Rect(double x, double y, double w, double h) =>
        new((float)x, (float)y, (float)(x + w), (float)(y + h));
}

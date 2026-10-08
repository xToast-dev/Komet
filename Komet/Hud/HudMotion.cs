namespace Komet.Hud;

// What makes a window feel alive without composing it again: it fades and rises in on opening and fades out on closing, a soft
// shadow lies under it, the part under the mouse lights up, a click flashes where it landed, the tab underline slides to the open
// tab. All of it tinted quads of one small white canvas over and under the composed one, drawn each frame at no Cairo cost.
internal sealed class HudMotion(ICoreClientAPI capi) : IDisposable
{
    private const double OpenMs = 170, CloseMs = 120, HoverMs = 120, FlashMs = 280, SlideMs = 180, Rise = 10, Shrink = 0.03;
    private const int Rings = 5;
    private static readonly double[] Shadow = [0.12, 0.08, 0.05, 0.03, 0.015];

    private readonly HudCanvas _solid = new(capi);
    private long _openedAt, _closingAt = -1, _hoverAt, _flashAt = long.MinValue / 2, _slideAt;
    private (double X, double Y, double W, double H) _hover, _flash, _from, _to;
    private bool _hovering;

    private static long Now => Environment.TickCount64;

    // 0 at the start of a move, 1 at its end, slowing down towards it
    private static double Ease(double t)
    {
        var u = 1 - Math.Clamp(double.IsFinite(t) ? t : 1, 0, 1);
        return Assert(u is >= 0 and <= 1) ? 1 - u * u * u : 1;
    }

    public bool Closing => _closingAt >= 0;

    // Faded out after a close: time to close the dialog for real
    public bool Closed => Closing && Now - _closingAt >= CloseMs;

    // How far the window is in: 0 gone, 1 fully there
    public double Shown => Closing ? 1 - Ease((Now - _closingAt) / CloseMs) : Ease((Now - _openedAt) / OpenMs);

    // On opening, and to come back while still fading out: from where the fade got to
    public void Open()
    {
        var t = Closing ? 1 - Math.Cbrt(1 - Shown) : 0; // Ease inverted: the fade goes on from where it is
        (_openedAt, _closingAt, _hovering) = (Now - (long)(t * OpenMs), -1, false);
        _ = Assert(_openedAt <= Now);
    }

    public void Close()
    {
        if (!Assert(OpenMs > 0)) return;
        if (!Closing) _closingAt = Now;
        _ = Assert(_closingAt >= 0);
    }

    // The canvas with its shadow, risen and faded in by Shown
    public void Draw(HudCanvas canvas, double x, double y, float z)
    {
        if (!NotNull(canvas) || !Solid()) return;
        var s = Shown;
        var (cx, cy) = canvas.Within(x, y + (1 - s) * scaled(Rise));
        for (var i = 0; i < Rings; i++)
        {
            var g = scaled(2 * (i + 1));
            _solid.Stretch(cx - g, cy - g + scaled(4), canvas.Width + 2 * g, canvas.Height + 2 * g, z - 1, new Rgba(0, 0, 0, Shadow[i] * s));
        }

        canvas.Fade(cx, cy, z, s, 1 - (1 - s) * Shrink);
    }

    // The hit under the mouse lit, fading in; a click's flash fading out. Rows across the window get a lighter touch than buttons.
    public void Hover(HudCanvas canvas, List<HudUi.Hit> hits, float z)
    {
        if (!NotNull(canvas) || !NotNull(hits) || !Solid()) return;
        var (mx, my) = (capi.Input.MouseX - canvas.X, capi.Input.MouseY - canvas.Y);
        var found = HudUi.HitAt(hits, mx, my);
        if (found is { } hit && (!_hovering || (hit.X, hit.Y, hit.W, hit.H) != _hover))
            (_hover, _hoverAt) = ((hit.X, hit.Y, hit.W, hit.H), Now);
        _hovering = found is not null && !Closing;
        var s = Shown;
        if (_hovering)
        {
            var strength = _hover.W > canvas.Width * 0.6 ? 0.045 : 0.1;
            _solid.Stretch(canvas.X + _hover.X, canvas.Y + _hover.Y, _hover.W, _hover.H, z + 1,
                Rgba.White(strength * Ease((Now - _hoverAt) / HoverMs) * s));
        }

        var flash = 1 - (Now - _flashAt) / FlashMs;
        if (flash > 0)
            _solid.Stretch(canvas.X + _flash.X, canvas.Y + _flash.Y, _flash.W, _flash.H, z + 1, Rgba.White(0.22 * flash * flash * s));
    }

    public void Press(HudUi.Hit hit)
    {
        if (!Assert(hit.W >= 0) || !Assert(hit.H >= 0) || !Finite(hit.X + hit.Y)) return;
        (_flash, _flashAt) = ((hit.X, hit.Y, hit.W, hit.H), Now);
    }

    // An underline sliding from the tab it was under to the open one
    public void Slide(HudCanvas canvas, (double X, double Y, double W, double H) to, float z, Rgba color)
    {
        if (!NotNull(canvas) || !Assert(to.W >= 0) || !Solid()) return;
        if (to != _to)
        {
            _from = _to.W > 0 ? Lerp(Ease((Now - _slideAt) / SlideMs)) : to;
            (_to, _slideAt) = (to, Now);
        }

        var at = Lerp(Ease((Now - _slideAt) / SlideMs));
        _solid.Stretch(canvas.X + at.X, canvas.Y + at.Y, at.W, at.H, z + 1, color with { A = color.A * Shown });
    }

    private (double X, double Y, double W, double H) Lerp(double t) => !Assert(t is >= 0 and <= 1) ? _to :
        (_from.X + (_to.X - _from.X) * t, _from.Y + (_to.Y - _from.Y) * t, _from.W + (_to.W - _from.W) * t, _from.H + (_to.H - _from.H) * t);

    // A drag's landing: on the snap step, inside the frame's margin
    public static double Snap(double at, double size, double frame)
    {
        var step = scaled(HudSettings.SnapStep);
        var margin = scaled(HudSettings.ScreenMargin);
        if (!Finite(at) || !Assert(step > 0)) return margin;
        return Math.Clamp(Math.Round(at / step) * step, margin, Math.Max(margin, frame - size - margin));
    }

    // The white canvas the quads are cut from, made once
    private bool Solid()
    {
        if (!NotNull(_solid) || _solid.Ready) return true;
        if (!_solid.Blank(32, 32)) return false;
        _solid.Fill(0, 0, 32, 32, Rgba.White(1));
        _solid.End();
        return _solid.Ready;
    }

    public void Dispose()
    {
        if (!NotNull(_solid) || !Assert(Rings == Shadow.Length)) return;
        _solid.Dispose();
    }
}

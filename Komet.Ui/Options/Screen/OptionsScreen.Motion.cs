using Komet.Hud;

namespace Komet.Options;

// The screen's motion, drawn over the picture so nothing waits for a new one. The page title decodes into place: random
// glyphs settle into the letters from left to right, on a small texture of its own painted again only while it decodes.
// A new page's list comes in from the side it follows, row after row: the picture is made once and each row is its band
// of it drawn again under a scissor, shifted and faded (a full-screen texture a frame stuttered on long pages). A press
// answers at once: where a click or a key landed, a flash of the accent fills the spot and corner marks spring out from
// it and fade.
internal sealed partial class OptionsScreen
{
    private const double DecodeSeconds = 0.4, SlideSeconds = 0.42, SlideStagger = 0.035, SlideShift = 36;
    private const double SlidePending = -2; // the new page is chosen, its picture not yet on screen
    private const double PressSeconds = 0.38, PressSpread = 10, PressFlash = 0.45;
    private const int MaxStagger = 14;
    private const float TitleDepth = ScreenDepth + 0.2f, PressDepth = GlowDepth + 0.5f;
    private const string Glyphs = "ABCDEFGHJKLMNPRSTUVWXYZ0123456789/<>#";

    private HudCanvas? _titleCanvas;
    private string _titleKey = "", _slideFor = ""; // _titleKey: the text the title texture shows
    private (int Epoch, double W, double H) _titleSize; // and the font size and box it was painted for
    private double _decodeAt = -1; // _clock when the title began to decode; negative: it stands
    private double _slide = -1; // _clock when the list began to come in; negative: it stands
    private int _slideDir = 1, _minute = -1;
    private uint _noise = 7; // the decoding glyphs' sequence: looks, not chance
    private (double X, double Y, double W, double H, double At) _press = (0, 0, 0, 0, -1);

    // Per frame: a list coming in asks for pictures until it stands; the footer's clock each minute; a code block's
    // "Copied" back to "Copy" once it has shown (nothing else recomposes a doc page that is left alone)
    private void Moving()
    {
        var minute = DateTime.Now.Minute;
        if (minute != _minute) (_minute, _dirty) = (minute, true);
        if (_copied.Block >= 0 && Environment.TickCount64 - _copied.At >= CopiedSeconds * 1000) (_copied, _dirty) = ((-1, 0), true);
        if (_slide < 0) return;
        if (_slide > _clock || _clock - _slide > (SlideSeconds + MaxStagger * SlideStagger) * Math.Max(1, Pace) + 0.1) _slide = -1;
    }

    // How far row i of the list has come in, 0 to 1
    private double In(int i)
    {
        if (_slide < 0 || _slide > _clock || !Moves("slide")) return 1;
        var t = (_clock - _slide - Math.Min(i, MaxStagger) * SlideStagger * Pace) / (SlideSeconds * Pace);
        return 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    }

    // Text decoding into place: the first part shown, the rest random glyphs
    private string Decode(string text, double progress)
    {
        if (progress >= 1 || !Moves("decode")) return text;
        var shown = (int)(Math.Max(0, progress) * text.Length);
        var chars = text.ToCharArray();
        for (var i = shown; i < Math.Min(chars.Length, 4096); i++)
        {
            if (char.IsWhiteSpace(chars[i])) continue;
            _noise = _noise * 1664525 + 1013904223;
            chars[i] = Glyphs[(int)(_noise >> 8) % Glyphs.Length];
        }

        return new string(chars);
    }

    private void Titled(double x, double y)
    {
        var (tx, ty, tw, th, text, tracking) = OnScreen.Title;
        if (text.Length == 0 || tw <= 0 || th <= 0 || !Finite(x + y)) return;
        var progress = _decodeAt < 0 || !Moves("decode") ? 1 : (_clock - _decodeAt) / (DecodeSeconds * Pace);
        if (progress >= 1) _decodeAt = -1;
        var shown = Decode(text, progress);
        var size = (_fonts.Epoch, Math.Round(tw), Math.Round(th)); // compared in parts: no key string built every frame
        _titleCanvas ??= new HudCanvas(capi);
        if ((!string.Equals(shown, _titleKey, StringComparison.Ordinal) || size != _titleSize) && _titleCanvas.Blank(tw, th))
        {
            var at = 0.0;
            foreach (var c in shown.ToCharArray().Bounded(256))
            {
                var letter = c.ToString();
                _titleCanvas.Text(at, 0, th, _fonts.Big, letter);
                at += _titleCanvas.TextWidth(_fonts.Big, letter) + Math.Max(0, tracking);
            }

            _titleCanvas.End();
            (_titleKey, _titleSize) = (shown, size);
        }

        if (_titleCanvas.Ready) _titleCanvas.Shifted(x + tx, y + ty, TitleDepth, _lens.Alpha);
    }

    private void Sliding(double x, double y)
    {
        var (vx, vy, vw, vh) = OnScreen.View;
        var (w, h) = (_canvas.Width, _canvas.Height);
        if (!Finite(x + y) || vw <= 0)
        {
            _canvas.Draw(x, y, ScreenDepth);
            return;
        }

        _canvas.Draw(x, y, ScreenDepth, placeOnly: true); // sets where clicks land; drawn below in four parts
        Scissored(x, y, w, vy, 0, 1);
        Scissored(x, y + vy + vh, w, h - vy - vh, 0, 1);
        Scissored(x, y + vy, vx, vh, 0, 1);
        Scissored(x + vx + vw, y + vy, w - vx - vw, vh, 0, 1);
        var bands = OnScreen.Bands;
        for (var i = 0; i < Math.Min(bands.Count, MaxItems); i++)
        {
            var come = In(i + 1);
            Scissored(x + vx, y + bands[i].Y, vw, bands[i].H, (1 - come) * S(SlideShift) * _slideDir, come);
        }

        capi.Render.GlScissorFlag(false);
    }

    // The picture shifted by dx and faded to alpha, seen only through the rectangle at x, y
    private void Scissored(double x, double y, double w, double h, double dx, double alpha)
    {
        if (w <= 0 || h <= 0 || alpha <= 0.01 || !Finite(x + y + dx)) return;
        var frame = capi.Render.FrameHeight;
        capi.Render.GlScissor((int)Math.Round(x), (int)Math.Round(frame - y - h), (int)Math.Round(w), (int)Math.Round(h));
        capi.Render.GlScissorFlag(true);
        _canvas.Shifted(_canvas.X + dx, _canvas.Y, ScreenDepth, alpha);
    }

    // A spot in canvas pixels was pressed
    private void Press(double x, double y, double w, double h)
    {
        if (!Finite(x + y) || w <= 0 || h <= 0) return;
        _press = (x, y, w, h, _clock);
    }

    // A key of the footer or the header, by its caption
    private void PressKey(string key)
    {
        if (!OnScreen.Caps.TryGetValue(key, out var cap)) return;
        Press(cap.X, cap.Y, cap.W, cap.H);
    }

    // The lit row, where it shows
    private void PressRow()
    {
        var (vx, vy, vw, vh) = OnScreen.View;
        var hit = _cursor is null ? null : OnScreen.Rows.Find(r => r.Row && ReferenceEquals(r.Option, _cursor));
        if (hit is null || hit.Y + hit.H <= vy || hit.Y >= vy + vh) return;
        var (top, bottom) = (Math.Max(hit.Y, vy), Math.Min(hit.Y + hit.H, vy + vh));
        Press(vx + S(Spacer), top, vw - 2 * S(Spacer), bottom - top);
    }

    private void Pressed(double x, double y)
    {
        var (px, py, pw, ph, at) = _press;
        if (at < 0 || !Finite(x + y)) return;
        var t = (_clock - at) / (PressSeconds * Math.Max(1, Pace));
        if (t >= 1 || Pace <= 0)
        {
            _press.At = -1;
            return;
        }

        var (grow, fade) = (S(PressSpread) * (1 - Math.Pow(1 - t, 3)), 1 - t);
        Rect(x + px, y + py, pw, ph, Accent with { A = PressFlash * fade * fade }, PressDepth);
        Ticks(x + px - grow, y + py - grow, pw + 2 * grow, ph + 2 * grow, S(8), _style["changed"] with { A = fade }, PressDepth);
    }
}

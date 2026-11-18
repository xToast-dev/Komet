using Komet.Hud;
using Komet.Ui.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The options menu's controls, painted in canvas pixels with the menu's fonts and style: the pill between Off and On,
// the wheel (a name between two arrows, a dot per name under it), the rail (a thin line with ticks between arrows and
// its number), the group heading (spaced capitals and a hairline) and the lit row. They only paint; the caller keeps
// its own hit boxes, so the options screen and the shader studio look the same and click as they always did.
internal sealed class MenuControls(HudCanvas canvas, HudFonts fonts)
{
    public const double ArrowBox = 24, PillWidth = 44, ControlShare = 0.5, ControlWidth = 300, NumberColumn = 52;

    private const double ArrowSize = 14,
        PillHeight = 22,
        KnobWidth = 4,
        KnobHeight = 14,
        CapHeight = 11,
        KnobRadius = 11,
        KnobRing = 3,
        ChipPad = 8,
        AccentBar = 4;

    private const int MaxDots = 12, MaxTicks = 12;

    public StyleSheet Style { get; set; } = StyleSheet.Fallback();
    public HudCanvas Canvas => canvas;
    public HudFonts Fonts => fonts;

    private Rgba Accent => Style["accent"];
    private Rgba Faint => Style["faint"];
    private Rgba Soft => Style["soft"];
    private Rgba Line => Style["line"];
    public Rgba Text => Style["text"];

    // The menu's unit: a size in its px at the game's GUI scale
    public static double S(double size) => Finite(size) ? scaled(size) : 0;

    // The text colour of a row: dimmed when disabled, the selection's on the lit row
    public Rgba Ink(bool enabled, bool lit)
    {
        if (!enabled) return Faint;

        return lit ? Style["selectText"] : Style["text"];
    }

    // Off, the pill, On: centred in the control column, as Pragmatic's
    public void Pill(double left, double right, double y, double h, bool on, Rgba color, bool enabled)
    {
        var (pw, cy) = (S(PillWidth), y + h / 2);
        if (!Finite(right)) return;

        var (offW, gap) = (canvas.TextWidth(fonts.Header, T("off")), S(10));
        var px = (left + right - pw) / 2;
        canvas.Text(px - gap - offW, y, h, fonts.Header, T("off"), on ? Faint : color);
        canvas.Text(px + pw + gap, y, h, fonts.Header, T("on"), on ? color : Faint);
        PillOnly(px, cy, on, color, enabled);
    }

    // The pill alone, its left edge at x, centred on cy (the studio's Auto switch)
    public void PillOnly(double x, double cy, bool on, Rgba color, bool enabled)
    {
        double pw = S(PillWidth), ph = S(PillHeight);
        canvas.Fill(x, cy - ph / 2, pw, ph, enabled ? color : Faint, ph / 2);
        var inner = on && enabled ? Accent : Style["bubble"] with { A = 1 };
        canvas.Fill(x + S(2), cy - ph / 2 + S(2), pw - S(4), ph - S(4), inner, ph / 2 - S(2));
        canvas.Circle(on ? x + pw - ph / 2 : x + ph / 2, cy, ph / 2 - S(5), enabled ? color : Faint);
    }

    // The value between two arrows at the column's ends, a dot per name under it (none past MaxDots)
    public void Wheel(double left, double right, double y, double h, string name, int at, int count, Rgba color,
        bool enabled)
    {
        var (box, cy) = (S(ArrowBox), y + h / 2);
        if (!Assert(right > left)) return;

        canvas.Arrow(left + box / 2, cy, S(ArrowSize), false, enabled ? color : Faint);
        canvas.Arrow(right - box / 2, cy, S(ArrowSize), true, enabled ? color : Faint);
        var (text, font) = Shrunk(name, right - left - 2 * box - S(8), fonts.Header);
        canvas.Text((left + right - canvas.TextWidth(font, text)) / 2, y, h - S(6), font, text, color);
        var shown = Math.Min(count, MaxDots);
        var (dot, step) = (S(2.5), S(6));
        var dx = (left + right - (shown - 1) * step) / 2;
        for (var i = 0; shown > 1 && count <= MaxDots && i < Math.Min(shown, MaxDots); i++) // too many: no dots
        {
            canvas.Fill(dx + i * step - dot / 2, cy + S(9), dot, dot, i == at ? color : Faint);
        }
    }

    // Arrows at the column's ends minus the number's column, the rail between: the same length on every row. shown is
    // the knob's fraction, standard the default's (NaN: none marked). The rail's span for the caller's hit boxes.
    public (double From, double Width, double End) Rail(double left, double right, double y, double h, string text,
        (double Shown, double Standard, int Steps) at, Rgba color, bool enabled, bool bubble)
    {
        var (box, cy, column) = (S(ArrowBox), y + h / 2, S(NumberColumn));
        canvas.Text(right - canvas.TextWidth(fonts.Text, text), y, h, fonts.Text, text, color);
        var end = right - column;
        canvas.Arrow(left + box / 2, cy, S(ArrowSize), false, enabled ? color : Faint);
        canvas.Arrow(end - box / 2, cy, S(ArrowSize), true, enabled ? color : Faint);
        double from = left + box, to = end - box, w = to - from;
        if (w <= S(20)) return (from, 0, end);

        var marks = at.Steps is > 0 and <= MaxTicks * 2 ? at.Steps : 10;
        canvas.Fill(from, cy - S(0.75), w, S(1.5), enabled ? Soft : Faint);
        for (var i = 0; i <= Math.Min(marks, MaxTicks * 2); i++) // a mark per step, taller at the ends
        {
            var tall = i == 0 || i == marks ? S(CapHeight) : S(5);
            canvas.Fill(from + w * i / marks - S(0.75), cy - tall / 2, S(1.5), tall, enabled ? Soft : Faint);
        }

        canvas.Fill(from, cy - S(1.5), w * at.Shown, S(3), enabled ? color : Faint);
        if (enabled && double.IsFinite(at.Standard))
        {
            canvas.Fill(from + w * at.Standard - S(1), cy - S(10), S(2), S(4), Soft);
        }

        var kx = from + w * at.Shown;
        canvas.Fill(kx - S(KnobWidth) / 2 - S(1), cy - S(KnobHeight) / 2 - S(1), S(KnobWidth) + S(2),
            S(KnobHeight) + S(2), color);
        canvas.Fill(kx - S(KnobWidth) / 2, cy - S(KnobHeight) / 2, S(KnobWidth), S(KnobHeight),
            enabled ? Accent : Faint);
        if (bubble) Bubble(kx, cy, text);

        return (from, w, end);
    }

    // The value over a dragged knob
    private void Bubble(double x, double cy, string text)
    {
        var (bw, bh) = (canvas.TextWidth(fonts.Text, text) + 2 * S(ChipPad), fonts.TextRow);
        var by = cy - S(KnobRadius + KnobRing) * 1.22 - S(6) - bh;
        canvas.Fill(x - bw / 2, by, bw, bh, Style["bubble"], S(6));
        canvas.Text(x - bw / 2 + S(ChipPad), by, bh, fonts.Text, text);
    }

    // A group's heading: its (already upper-case) text spaced in the faint tone, a hairline to the row's end
    public void Group(double x, double y, double w, double h, string text, double pad)
    {
        var tw = SpacedWidth(fonts.Small, text, S(2));
        Spaced(x + pad * 0.6, y, h, fonts.Small, text, S(2), Faint);
        canvas.Fill(x + pad * 0.6 + tw + S(10), y + h / 2, Math.Max(0, w - tw - pad * 1.2 - S(10)), 1, Line);
    }

    // The lit row: the style's selection with its accent bar on the right
    public void Lit(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;

        canvas.Fill(x, y, w, h, Style["select"]);
        canvas.Fill(x + w - S(AccentBar), y, S(AccentBar), h, Accent);
    }

    // Text that fits width: in its font, else a size smaller, and only past that cut with an ellipsis
    public (string Text, CairoFont Font) Shrunk(string text, double width, CairoFont font)
    {
        if (!NotNull(text)) return ("", font);

        if (!Assert(width > 0) || canvas.TextWidth(font, text) <= width) return (text, font);

        var small = fonts.Small;
        return canvas.TextWidth(small, text) <= width ? (text, small) : (Fit(text, width, small), small);
    }

    private string Fit(string text, double width, CairoFont? font = null) =>
        NotNull(text) && Assert(width > 0) ? canvas.Fit(font ?? fonts.Text, text, width, true) : text;

    public void Spaced(double x, double y, double h, CairoFont font, string text, double tracking, Rgba? color)
    {
        if (!NotNull(text) || !Finite(x + y) || !Assert(h > 0)) return;

        if (tracking <= 0.01)
        {
            canvas.Text(x, y, h, font, text, color);
            return;
        }

        foreach (var c in text.ToCharArray().Bounded(256))
        {
            var s = c.ToString();
            canvas.Text(x, y, h, font, s, color);
            x += canvas.TextWidth(font, s) + tracking;
        }
    }

    public double SpacedWidth(CairoFont font, string text, double tracking) =>
        NotNull(text) && Finite(tracking)
            ? canvas.TextWidth(font, text) + Math.Max(0, tracking) * Math.Max(0, text.Length - 1)
            : 0;
}

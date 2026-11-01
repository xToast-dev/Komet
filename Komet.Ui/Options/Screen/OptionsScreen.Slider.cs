using Komet.Api;

namespace Komet.Options;

// Sliders: the knob glides (follows a drag), staged changes grow the bar; the default (preset and computer) is marked
// and restored by Delete or a right click. Keys act on the focused slider. Recomposes only while a value moves.
internal sealed partial class OptionsScreen
{
    private const double KnobRadius = 11, KnobRing = 3, ChipPad = 8, Spring = 18, Settled = 0.002, BigStep = 10;
    private const int MaxTicks = 12, MaxMotions = 256;

    private sealed class Motion
    {
        public required OptionRow Row { get; init; }
        public double Shown { get; set; }
        public double Staged { get; set; }
    }

    private readonly List<Motion> _motions = [];
    private OptionRow? _focus;

    private static double Fraction(OptionRow row, double value) =>
        Assert(row.Max > row.Min) && Finite(value) ? Math.Clamp((value - row.Min) / (row.Max - row.Min), 0, 1) : 0;

    private static bool Resettable(OptionRow row, double value) =>
        row.Standard is var standard && double.IsFinite(standard) && Math.Abs(value - standard) > 1e-9;

    private Motion MotionOf(OptionRow row)
    {
        for (var i = 0; i < Math.Min(_motions.Count, MaxMotions); i++)
            if (ReferenceEquals(_motions[i].Row, row))
                return _motions[i];
        var made = new Motion { Row = row, Shown = Fraction(row, Value(row)), Staged = _staged.ContainsKey(row) ? 1 : 0 };
        if (_motions.Count < MaxMotions) _motions.Add(made);
        return made;
    }

    private void Animate(float deltaTime)
    {
        if (!Finite(deltaTime) || !Assert(deltaTime >= 0) || _motions.Count == 0) return;
        var k = 1 - Math.Exp(-deltaTime * Spring);
        var moving = false;
        foreach (var m in _motions.Bounded(MaxMotions))
        {
            var (row, shown) = (m.Row, Fraction(m.Row, Value(m.Row)));
            if (ReferenceEquals(_drag?.Option, row)) m.Shown = shown; // a drag: the knob stays under the cursor
            var staged = _staged.ContainsKey(row) ? 1.0 : 0;
            if (Math.Abs(m.Shown - shown) + Math.Abs(m.Staged - staged) <= Settled)
            {
                (m.Shown, m.Staged) = (shown, staged);
                continue;
            }

            (m.Shown, m.Staged, moving) = (Ease(m.Shown, shown, k), Ease(m.Staged, staged, k), true);
        }

        if (moving) _dirty = true;
    }

    private void Bubble(double x, double cy, string text)
    {
        var (bw, bh) = (_canvas.TextWidth(_fonts.Text, text) + 2 * S(ChipPad), _fonts.TextRow);
        var by = cy - S(KnobRadius + KnobRing) * 1.22 - S(6) - bh;
        _canvas.Fill(x - bw / 2, by, bw, bh, BubbleBack, S(6));
        _canvas.Text(x - bw / 2 + S(ChipPad), by, bh, _fonts.Text, text);
    }

    // The focused slider's keys after TabKeys took its own (Left, Right, Del): Home, End, Backspace
    private bool Keyed(KeyEvent args)
    {
        if (!NotNull(args) || _binding is not null || _searching) return false;
        if (_focus is not { } row || !Assert(row.Step > 0)) return false;
        var (value, step) = (Value(row), row.Step * (args.ShiftPressed ? BigStep : 1));
        double? next = args.KeyCode switch
        {
            (int)GlKeys.Left => value - step,
            (int)GlKeys.Right => value + step,
            (int)GlKeys.Home => row.Min,
            (int)GlKeys.End => row.Max,
            (int)GlKeys.Delete or (int)GlKeys.BackSpace when double.IsFinite(row.Standard) => row.Standard,
            _ => null
        };
        if (next is not { } to) return false;
        if (row.IsEnabled) Stage(row, row.At(Fraction(row, to)));
        return true;
    }
}

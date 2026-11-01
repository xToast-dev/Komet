using Komet.Api;
using Komet.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The list's rows: a switch as a pill between Off and On, a choice between two arrows with a dot per name, a slider
// as a thin rail between arrows with its number; the keys act on the lit row (arrows, Q/E, Tab, R, Enter).
internal sealed partial class OptionsScreen
{
    private const double ArrowBox = 24, ArrowSize = 14, PillWidth = 44, PillHeight = 22, KnobWidth = 4, KnobHeight = 14, CapHeight = 11,
        ControlShare = 0.5, ControlWidth = 300, NumberColumn = 52;
    private const int MaxDots = 12;

    // A throwing mod getter costs its row, not the screen; logged once
    private void TabGuarded(OptionRow row, double x, double y, double w, double h, double come)
    {
        if (!NotNull(row) || !Finite(y)) return;
        try
        {
            TabRow(row, x, y, w, h, come);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = _map.Rows.RemoveAll(hit => ReferenceEquals(hit.Option, row));
            _canvas.Text(x + S(Pad), y, h, _fonts.Text, row.Label, Faint);
            if (_failed.Add(row)) capi.Logger?.Warning("Komet options: the row '{0}' failed and is left out: {1}", row.Label, e);
        }
    }

    private void TabRow(OptionRow row, double x, double y, double w, double h, double come)
    {
        var (enabled, lit) = (row.IsEnabled, ReferenceEquals(row, _cursor));
        if (!Assert(h > 0) || !Assert(w > 0)) return;
        var cw = Math.Min(S(ControlWidth), w * ControlShare);
        var (text, font) = Shrunk(row.Label, w - Reserved(row, cw) - TagsWidth(row) - 3 * S(Pad), _fonts.Text);
        var label = Decode(text, come * 1.2);
        var color = Ink(enabled, lit);
        Rgba? ink = enabled && !lit ? null : color;
        var lx = x + S(Pad) + (lit ? S(4) : 0);
        _canvas.Text(lx, y, h, font, label, ink);
        Tags(row, lx + _canvas.TextWidth(font, label) + (_staged.ContainsKey(row) ? S(14) : 0), y, h);
        if (_staged.ContainsKey(row))
            _canvas.Fill(lx + _canvas.TextWidth(font, label) + S(9), y + h / 2 - S(2.5), S(5), S(5), _style["changed"]);
        _map.Rows.Add(new Hit(x, y, w, h, (_, _) => { }, Option: row, Row: true, Group: ListGroup));
        var (left, right) = (x + w - S(Pad) - cw, x + w - S(Pad));
        switch (row.Kind)
        {
            case OptionKind.Switch:
                Pill(row, (x, w), left, right, y, h, color, enabled);
                break;
            case OptionKind.Choice:
                Carousel(row, (x, w), left, right, y, h, color, enabled);
                break;
            case OptionKind.Slider when Widest(row) > S(NumberColumn):
                Named(row, (x, w), left, right, y, h, color, enabled);
                break;
            case OptionKind.Slider:
                Rail(row, left, right, y, h, color, enabled);
                break;
            case OptionKind.Button:
                RightText(right, y, h, Caption(row), enabled && !lit ? Title : color);
                if (enabled) _map.Rows.Insert(0, new Hit(x, y, w, h, (_, _) => row.Set(0), Option: row));
                break;
            case OptionKind.Key:
                Key(row, x, y, w, h, right, enabled);
                break;
        }
    }

    // Off, the pill, On: centred in the control column, as Pragmatic's
    private void Pill(OptionRow row, (double X, double W) span, double left, double right, double y, double h, Rgba color, bool enabled)
    {
        var on = Value(row) >= 0.5;
        double pw = S(PillWidth), ph = S(PillHeight), cy = y + h / 2;
        if (!Finite(right)) return;
        var (offW, gap) = (_canvas.TextWidth(_fonts.Header, T("off")), S(10));
        var px = (left + right - pw) / 2;
        _canvas.Text(px - gap - offW, y, h, _fonts.Header, T("off"), on ? Faint : color);
        _canvas.Text(px + pw + gap, y, h, _fonts.Header, T("on"), on ? color : Faint);
        _canvas.Fill(px, cy - ph / 2, pw, ph, enabled ? color : Faint, ph / 2);
        var inner = on && enabled ? Accent : _style["bubble"] with { A = 1 };
        _canvas.Fill(px + S(2), cy - ph / 2 + S(2), pw - S(4), ph - S(4), inner, ph / 2 - S(2));
        _canvas.Circle(on ? px + pw - ph / 2 : px + ph / 2, cy, ph / 2 - S(5), enabled ? color : Faint);
        if (enabled) _map.Rows.Insert(0, new Hit(span.X, y, span.W, h, (_, _) => Stage(row, on ? 0 : 1), Option: row));
    }

    private void Carousel(OptionRow row, (double X, double W) span, double left, double right, double y, double h, Rgba color,
        bool enabled)
    {
        var offered = Offered(row);
        if (!Finite(y)) return;
        var at = Math.Clamp((int)Math.Round(Value(row)), 0, row.Names.Length - 1);
        Wheel(row, (span, left, right, y, h), Parts(row.Names[at]).Main, (at, offered), color, enabled,
            back => Stage(row, Next(at, offered, back)));
    }

    // A slider whose values are words (Very high quality): turned like a choice, a dot per step
    private void Named(OptionRow row, (double X, double W) span, double left, double right, double y, double h, Rgba color,
        bool enabled)
    {
        var steps = (int)Math.Round((row.Max - row.Min) / row.Step);
        if (!Assert(steps > 0) || !Finite(y)) return;
        var at = (int)Math.Round((Value(row) - row.Min) / row.Step);
        Wheel(row, (span, left, right, y, h), row.ValueText(Value(row)), (at, steps + 1), color, enabled, back => Nudge(row, back, false));
    }

    // The value between two arrows at the column's ends, a dot per name under it
    private void Wheel(OptionRow row, ((double X, double W) Span, double Left, double Right, double Y, double H) at, string name,
        (int At, int Count) dots, Rgba color, bool enabled, Action<int> turn)
    {
        var (span, left, right, y, h) = at;
        var (box, cy) = (S(ArrowBox), y + h / 2);
        if (!Assert(right > left)) return;
        _canvas.Arrow(left + box / 2, cy, S(ArrowSize), false, enabled ? color : Faint);
        _canvas.Arrow(right - box / 2, cy, S(ArrowSize), true, enabled ? color : Faint);
        var (text, font) = Shrunk(name, right - left - 2 * box - S(8), _fonts.Header);
        _canvas.Text((left + right - _canvas.TextWidth(font, text)) / 2, y, h - S(6), font, text, color);
        var count = Math.Min(dots.Count, MaxDots);
        var (dot, step) = (S(2.5), S(6));
        var dx = (left + right - (count - 1) * step) / 2;
        for (var i = 0; count > 1 && dots.Count <= MaxDots && i < Math.Min(count, MaxDots); i++) // too many steps: no dots
            _canvas.Fill(dx + i * step - dot / 2, cy + S(9), dot, dot, i == dots.At ? color : Faint);
        if (!enabled) return;
        _map.Rows.Insert(0, new Hit(span.X, y, span.W, h, (_, back) => turn(back ? -1 : 1), Option: row));
        _map.Rows.Insert(0, new Hit(left, y, box, h, (_, _) => turn(-1), Option: row));
        _map.Rows.Insert(0, new Hit(right - box, y, box, h, (_, _) => turn(1), Option: row));
    }

    // What a row's control takes from the right: a button or a key only its caption, the rest the control column
    private double Reserved(OptionRow row, double column) => row.Kind switch
    {
        OptionKind.Button => _canvas.TextWidth(_fonts.Text, Caption(row)),
        OptionKind.Key => _canvas.TextWidth(_fonts.Text, Bound(row)) + S(ClearWidth),
        _ => column
    };

    // Text that fits width: in its font, else a size smaller, and only past that cut with an ellipsis
    private (string Text, CairoFont Font) Shrunk(string text, double width, CairoFont font)
    {
        if (!NotNull(text) || !Assert(width > 0) || _canvas.TextWidth(font, text) <= width) return (text ?? "", font);
        var small = _fonts.Small;
        return _canvas.TextWidth(small, text) <= width ? (text, small) : (Fit(text, width, small), small);
    }

    // The text colour of a row: dimmed when disabled, the selection's on the lit row
    private Rgba Ink(bool enabled, bool lit)
    {
        if (!enabled) return Faint;
        return lit ? _style["selectText"] : _style["text"];
    }

    // The next offered name; a state past them steps to either end
    private static int Next(int at, int offered, int direction)
    {
        if (at < offered) return (at + direction + offered) % offered;
        return direction > 0 ? 0 : offered - 1;
    }

    // Arrows at the column's ends minus the number's column, the rail between: the same length on every row
    private void Rail(OptionRow row, double left, double right, double y, double h, Rgba color, bool enabled)
    {
        var value = Value(row);
        if (!Finite(value) || !Assert(row.Max > row.Min) || !Assert(right > left)) return;
        var (box, cy, column) = (S(ArrowBox), y + h / 2, S(NumberColumn));
        var text = row.ValueText(value);
        _canvas.Text(right - _canvas.TextWidth(_fonts.Text, text), y, h, _fonts.Text, text, color);
        var end = right - column;
        _canvas.Arrow(left + box / 2, cy, S(ArrowSize), false, enabled ? color : Faint);
        _canvas.Arrow(end - box / 2, cy, S(ArrowSize), true, enabled ? color : Faint);
        double from = left + box, to = end - box, w = to - from;
        if (w <= S(20)) return;
        var shown = MotionOf(row).Shown;
        var steps = (int)Math.Round((row.Max - row.Min) / row.Step);
        var marks = steps is > 0 and <= MaxTicks * 2 ? steps : 10;
        _canvas.Fill(from, cy - S(0.75), w, S(1.5), enabled ? Soft : Faint);
        for (var i = 0; i <= Math.Min(marks, MaxTicks * 2); i++) // a mark per step, taller at the ends
        {
            var tall = i == 0 || i == marks ? S(CapHeight) : S(5);
            _canvas.Fill(from + w * i / marks - S(0.75), cy - tall / 2, S(1.5), tall, enabled ? Soft : Faint);
        }

        _canvas.Fill(from, cy - S(1.5), w * shown, S(3), enabled ? color : Faint);
        if (enabled && Resettable(row, value)) _canvas.Fill(from + w * Fraction(row, row.Standard) - S(1), cy - S(10), S(2), S(4), Soft);
        var kx = from + w * shown;
        _canvas.Fill(kx - S(KnobWidth) / 2 - S(1), cy - S(KnobHeight) / 2 - S(1), S(KnobWidth) + S(2), S(KnobHeight) + S(2), color);
        _canvas.Fill(kx - S(KnobWidth) / 2, cy - S(KnobHeight) / 2, S(KnobWidth), S(KnobHeight), enabled ? Accent : Faint);
        if (ReferenceEquals(_drag?.Option, row)) Bubble(kx, cy, text);
        if (!enabled) return;
        _map.Rows.Insert(0, new Hit(left, y, box, h, (_, _) => Nudge(row, -1, false), Option: row));
        _map.Rows.Insert(0, new Hit(end - box, y, box, h, (_, _) => Nudge(row, 1, false), Option: row));
        _map.Rows.Insert(0, new Hit(from, y, w, h, (f, back) =>
        {
            (_focus, _cursor) = (row, row);
            Stage(row, back && double.IsFinite(row.Standard) ? row.Standard : row.At(f));
        }, true, row));
    }

    // A row's value a step on: a switch flips, a choice turns, a slider moves (ten steps with Shift)
    private void Nudge(OptionRow? row, int direction, bool big)
    {
        if (row is null || !row.IsEnabled) return;
        var value = Value(row);
        switch (row.Kind)
        {
            case OptionKind.Switch:
                Stage(row, value >= 0.5 ? 0 : 1);
                break;
            case OptionKind.Choice:
                Stage(row, Next((int)Math.Round(value), Offered(row), direction));
                break;
            case OptionKind.Slider when Assert(row.Step > 0):
                Stage(row, row.At(Fraction(row, value + direction * row.Step * (big ? BigStep : 1))));
                break;
        }
    }

    // R: every row of the open page back to its default, staged (Enter applies, Del drops); a key row is bound to the
    // game's default at once. Defaults: a row's own (Komet's knobs), else the game's (its pages), else the row stays.
    private void ResetPage()
    {
        if (Array.Find(_pages, p => p.Id == _page) is not { } page || Filtering || !Assert(page.Count <= OptionPage.MaxRows)) return;
        List<OptionRow> rows = [];
        for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++) rows.Add(page[i]);
        var game = page.Section == T("section-game") ? EngineOptions.Defaults(rows) : [];
        for (var i = 0; i < Math.Min(rows.Count, OptionPage.MaxRows); i++)
        {
            var row = rows[i];
            if (!row.IsEnabled || row.Live || row.Kind is OptionKind.Group or OptionKind.Button) continue;
            if (row.Kind == OptionKind.Key)
            {
                EngineOptions.Defaulted(row.Code);
                continue;
            }

            var to = double.IsFinite(row.Standard) || i >= game.Length ? row.Standard : game[i];
            if (double.IsFinite(to)) Stage(row, row.Kind == OptionKind.Choice ? Math.Clamp(Math.Round(to), 0, Offered(row) - 1) : Math.Clamp(to, row.Min, row.Max));
        }

        _dirty = true;
    }

    private void Unstage()
    {
        _staged.Clear();
        _dirty = true;
    }

    // The screen's keys; true when one was taken
    private bool TabKeys(KeyEvent args)
    {
        if (!NotNull(args) || _searching || _binding is not null) return false;
        var (shift, ctrl) = (args.ShiftPressed, args.CtrlPressed);
        switch (args.KeyCode)
        {
            case (int)GlKeys.Q: PressKey("Q"); Section(-1); return true;
            case (int)GlKeys.E: PressKey("E"); Section(1); return true;
            case (int)GlKeys.Tab: PageStep(shift ? -1 : 1); return true;
            case (int)GlKeys.Up: Cursor(-1); return true;
            case (int)GlKeys.Down: Cursor(1); return true;
            case (int)GlKeys.PageUp: Cursor(-8); return true;
            case (int)GlKeys.PageDown: Cursor(8); return true;
            case (int)GlKeys.Left: PressRow(); Nudge(_cursor, -1, shift); return true;
            case (int)GlKeys.Right: PressRow(); Nudge(_cursor, 1, shift); return true;
            case (int)GlKeys.R: PressKey("R"); ResetPage(); return true;
            case (int)GlKeys.Delete when _staged.Count > 0: PressKey("Del"); Unstage(); return true;
            case (int)GlKeys.F when ctrl: _searching = true; return true;
            case (int)GlKeys.Enter or (int)GlKeys.KeypadEnter:
                Entered();
                return true;
            default: return false;
        }
    }

    // Enter: a button presses, a key row waits for its key, anything else applies
    private void Entered()
    {
        PressRow();
        if (_cursor is { Kind: OptionKind.Button, IsEnabled: true } button) button.Set(0);
        else if (_cursor is { Kind: OptionKind.Key, IsEnabled: true } key) Binding(key);
        else
        {
            PressKey("Enter");
            Apply();
        }
    }

    // The next or previous section, at its first page
    private void Section(int direction)
    {
        var sections = Sections();
        var at = sections.IndexOf(Section());
        if (sections.Count == 0) return;
        Section(sections[(Math.Max(0, at) + direction + sections.Count) % sections.Count]);
    }

    private void Section(string section)
    {
        if (section == Section() && !Filtering) return;
        if (Array.Find(_pages, p => p.Section == section) is { } first) Select(first);
    }

    private void PageStep(int direction)
    {
        var pages = Array.FindAll(_pages, p => p.Section == Section());
        var at = Array.FindIndex(pages, p => p.Id == _page);
        if (pages.Length == 0) return;
        Select(pages[(Math.Max(0, at) + direction + pages.Length) % pages.Length]);
    }

    // The lit row moves by steps over the page's rows, and the list scrolls to keep it in view
    private void Cursor(int steps)
    {
        var rows = Items().FindAll(i => i.Kind == ItemKind.Row && i.Row is not null);
        if (rows.Count == 0) return;
        var at = rows.FindIndex(i => ReferenceEquals(i.Row, _cursor));
        var next = Math.Clamp(at + steps, 0, rows.Count - 1);
        if (at < 0) next = steps > 0 ? 0 : rows.Count - 1;
        (_cursor, _focus) = (rows[next].Row, null);
        double top = 0, row = S(Math.Max(24, _style.Size("row")));
        foreach (var item in Items().Bounded(MaxItems))
        {
            if (ReferenceEquals(item.Row, _cursor)) break;
            top += item.Kind == ItemKind.Row ? row : item.Height;
        }

        var view = OnScreen.View.H;
        if (top < _scroll) _scroll = top;
        else if (view > 0 && top + row > _scroll + view) _scroll = top + row - view;
    }

    // The grounds of the shown picture, scaled and faded with it
    private void Backs(double x, double y)
    {
        var backs = OnScreen.Backs;
        if (!Finite(x + y) || !Assert(backs.Count <= MaxItems)) return;
        foreach (var b in backs.Bounded(MaxItems))
        {
            Rect(x + b.X, y + b.Y, b.W, b.H, b.Color, BackDepth);
            if (b.Ticks > 0) Ticks(x + b.X, y + b.Y, b.W, b.H, b.Ticks, b.Tick, BackDepth);
            if (!b.Panel) continue;
            var one = Math.Max(1, Math.Round(scaled(1))); // a panel's sides: hairlines, as Pragmatic draws them
            Rect(x + b.X, y + b.Y, one, b.H, _style["line"], BackDepth);
            Rect(x + b.X + b.W - one, y + b.Y, one, b.H, _style["line"], BackDepth);
        }

        var (ux, uy, uw, uh) = OnScreen.Underline;
        var target = uw > 0 ? new Light(ux, uy, uw, uh, 1, TabGroup) : _tabLine with { Alpha = 0 };
        _tabLine = _tabLine.W <= 0 ? target : Glide(_tabLine, target, 0.25);
        if (_tabLine.Alpha > 0.01) Rect(x + _tabLine.X, y + _tabLine.Y, _tabLine.W, _tabLine.H, Accent with { A = _tabLine.Alpha }, MarkDepth);
    }

    // The lit row's light under the picture: the style's selection with corner marks, gliding row to row
    private void Mark(float deltaTime, double x, double y)
    {
        var (vx, vy, vw, vh) = OnScreen.View;
        var hit = _cursor is { } cursor ? RowOf(OnScreen.Rows, cursor) : null;
        var target = _mark with { Alpha = 0 };
        if (hit is not null && hit.Y + hit.H > vy && hit.Y < vy + vh)
        {
            var (top, bottom) = (Math.Max(hit.Y, vy), Math.Min(hit.Y + hit.H, vy + vh));
            target = new Light(vx + S(Spacer), top, vw - 2 * S(Spacer), bottom - top, 1, ListGroup);
        }

        _mark = _mark.Alpha < Settle ? target : Glide(_mark, target, 1 - Math.Exp(-deltaTime * Speed));
        if (_mark.Alpha < Settle || _mark.W <= 0) return;
        var select = _style["select"];
        Rect(x + _mark.X, y + _mark.Y, _mark.W, _mark.H, select with { A = select.A * _mark.Alpha }, MarkDepth);
        if (_style.Size("ticks") > 0) Ticks(x + _mark.X - S(3), y + _mark.Y - S(3), _mark.W + S(6), _mark.H + S(6), S(6), _style["selectText"] with { A = 0.85 * _mark.Alpha },
            MarkDepth);
    }

    // Per frame, so a loop, not a Find capturing this
    private static Hit? RowOf(List<Hit> rows, OptionRow option)
    {
        for (var i = 0; i < Math.Min(rows.Count, MaxHits); i++)
            if (rows[i].Row && ReferenceEquals(rows[i].Option, option))
                return rows[i];
        return null;
    }

    private static Light Glide(Light from, Light to, double k) => Finite(k)
        ? new Light(Ease(from.X, to.X, k), Ease(from.Y, to.Y, k), Ease(from.W, to.W, k), Ease(from.H, to.H, k),
            Ease(from.Alpha, to.Alpha, k), to.Group)
        : to;

    // Four corner marks of length t, one pixel thick
    private void Ticks(double x, double y, double w, double h, double t, Rgba color, float z)
    {
        var one = Math.Max(1, Math.Round(scaled(1)));
        if (!Finite(x + y) || t <= 0 || !Assert(w > 0 && h > 0)) return;
        Rect(x, y, t, one, color, z);
        Rect(x, y, one, t, color, z);
        Rect(x + w - t, y, t, one, color, z);
        Rect(x + w - one, y, one, t, color, z);
        Rect(x, y + h - one, t, one, color, z);
        Rect(x, y + h - t, one, t, color, z);
        Rect(x + w - t, y + h - one, t, one, color, z);
        Rect(x + w - one, y + h - t, one, t, color, z);
    }

    // A rectangle in screen pixels, scaled about the picture's centre and faded with it (a change of style)
    private void Rect(double x, double y, double w, double h, Rgba color, float z)
    {
        var (cx, cy, scale, alpha) = _lens;
        if (!Finite(x + y + w + h) || w <= 0 || h <= 0) return;
        var a = color.A * alpha;
        if (a <= 0.002) return;
        Solid(cx + (x - cx) * scale, cy + (y - cy) * scale, w * scale, h * scale, color with { A = Math.Min(1, a) }, z);
    }
}

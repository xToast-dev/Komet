using System.Text.RegularExpressions;
using static Komet.Options.KometPages;

namespace Komet.Options;

internal sealed partial class OptionsScreen
{
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Markup();

    private void Search(Columns c)
    {
        double h = S(SearchHeight), close = c.Desc > 0 ? h : 0, w = c.Side + c.Gap + c.Content - (close > 0 ? 0 : h + c.Gap);
        if (!Assert(w > 0) || !Assert(h > 0)) return;
        _canvas.Fill(0, 0, w, h, Panel);
        var text = _searching ? "" : T("search");
        if (_query.Length > 0) text = _query;
        _canvas.Text(S(Pad), 0, h, _fonts.Text, text + (_searching ? "_" : ""), _query.Length > 0 ? null : Faint);
        _hits.Add(new Hit(0, 0, w, h, (_, _) => _searching = true, Glow: SoftGlow, Group: TopGroup));
        if (_query.Length > 0)
        {
            _canvas.Text(w - S(Pad) - _canvas.TextWidth(_fonts.Header, "×"), 0, h, _fonts.Header, "×", Soft);
            _hits.Insert(0, new Hit(w - h, 0, h, h, (_, _) => (_query, _searching) = ("", false), Glow: StrongGlow, Group: TopGroup));
        }

        var x = close > 0 ? c.Width - h : w + c.Gap;
        _canvas.Fill(x, 0, h, h, Panel);
        _canvas.Text(x + (h - _canvas.TextWidth(_fonts.Header, "×")) / 2, 0, h, _fonts.Header, "×", Soft);
        _hits.Add(new Hit(x, 0, h, h, (_, _) => Close(), Glow: StrongGlow, Group: TopGroup));
    }

    // Closing without Done (the ×, Escape) drops what was not applied
    private bool Close()
    {
        _staged.Clear();
        return Assert(_staged.Count == 0) && TryClose();
    }

    private void Sidebar(Columns c, string current)
    {
        var (bottom, rowH) = (c.Height - (c.Desc > 0 ? 0 : 3 * (S(ButtonHeight) + c.Gap)), S(RowHeight));
        if (!Assert(bottom > c.Top) || !Assert(rowH > 0)) return;
        _canvas.Fill(0, c.Top, c.Side, c.Height - c.Top, Panel);
        _canvas.Clip(0, c.Top, c.Side, bottom - c.Top);
        var y = c.Top + S(Spacer);
        string? section = null;
        foreach (var page in _pages.Bounded(MaxPages))
        {
            if (page.Section != section)
            {
                if (section is not null) y += 2 * S(Spacer);
                section = page.Section;
                SectionName(y, section);
                y += S(SectionHeight);
            }

            var chosen = _query.Length == 0 && page.Id == current;
            if (chosen) Lit(0, y, c.Side, rowH);
            _canvas.Text(S(Pad) * 1.5, y, rowH, _fonts.Text, page.Title, chosen ? null : Soft);
            _hits.Add(new Hit(0, y, c.Side, rowH, (_, _) => Select(page), Glow: RowGlow, Group: SideGroup));
            y += rowH;
        }

        _canvas.Unclip();
    }

    private void SectionName(double y, string section)
    {
        var half = S(SectionHeight) / 2;
        if (!NotNull(section) || !Finite(y) || !Assert(half > 0)) return;
        _canvas.Text(S(Pad), y, half, _fonts.Title, section);
        _canvas.Text(S(Pad), y + half, half, _fonts.Text, Version(section), Faint);
    }

    private string Version(string section)
    {
        if (!NotNull(section) || !NotNull(capi)) return "";
        if (section == T("section-game")) return GameVersion.ShortGameVersion;
        var mod = capi.ModLoader?.Mods?.FirstOrDefault(m => m.Info?.Name == section || m.Info?.ModID == section);
        return mod?.Info?.Version ?? "";
    }

    private void Select(OptionPage page)
    {
        if (!NotNull(page) || !Assert(page.Id.Length > 0)) return;
        (_page, _scroll, _query, _searching, _picker, _dirty) = (page.Id, 0, "", false, null, true);
    }

    private void Content(Columns c, List<Item> items)
    {
        var x = c.Side + c.Gap;
        if (!Assert(c.Content > 0) || !NotNull(items)) return;
        _canvas.Clip(x, c.Top, c.Content, c.Height - c.Top);
        _view = (x, c.Top, c.Content, c.Height - c.Top);
        var y = c.Top - _scroll;
        foreach (var item in items.Bounded(MaxItems))
        {
            if (y + item.Height > c.Top && y < c.Height) Paint(item, x, y, c.Content);
            y += item.Height;
        }

        _canvas.Unclip();
    }

    private void Paint(Item item, double x, double y, double w)
    {
        if (!NotNull(item) || !Finite(y)) return;
        switch (item.Kind)
        {
            case ItemKind.Section:
                Band(x, y, w, item.Height);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Title, item.Text);
                Value(x + w - S(Pad), y, item.Height, item.Page.Section, Faint);
                break;
            case ItemKind.Page:
                Band(x, y, w, item.Height);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Header, item.Text, Title);
                break;
            case ItemKind.Group:
                _canvas.Fill(x, y, w, item.Height, RowBack);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Header, item.Text.ToUpperInvariant(), Faint);
                break;
            case ItemKind.Row when item.Row is { } row:
                Guarded(row, x, y, w, item.Height);
                break;
        }
    }

    // A mod's getter that throws costs its row, not the screen: the row keeps its name, the log gets the exception once
    private void Guarded(OptionRow row, double x, double y, double w, double h)
    {
        if (!NotNull(row) || !Finite(y)) return;
        try
        {
            Row(row, x, y, w, h);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = _rows.RemoveAll(hit => ReferenceEquals(hit.Option, row));
            _canvas.Text(x + S(Pad), y, h, _fonts.Text, row.Label, Faint);
            if (_failed.Add(row)) capi.Logger?.Warning("Komet options: the row '{0}' failed and is left out: {1}", row.Label, e);
        }
    }

    private void Row(OptionRow row, double x, double y, double w, double h)
    {
        var (hovered, enabled) = (ReferenceEquals(row, _hover) || ReferenceEquals(_drag?.Option, row), row.IsEnabled);
        if (!Assert(h > 0) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, RowBack);
        if (_staged.ContainsKey(row)) _canvas.Fill(x, y, S(AccentBar), h, HudCanvas.Accent);
        var reserved = ValueWidth(row) + (row.Kind == OptionKind.Slider ? S(TrackWidth) + S(Pad) : 0); // a slider's track shows on hover
        var label = Fit(row.Label, w - reserved - 3 * S(Pad));
        _canvas.Text(x + S(Pad), y, h, _fonts.Text, label, enabled ? null : Faint);
        if (!enabled) _canvas.Fill(x + S(Pad), y + h / 2, _canvas.TextWidth(_fonts.Text, label), 1, Faint);
        _rows.Add(new Hit(x, y, w, h, (_, _) => { }, Option: row, Row: true, Glow: enabled ? RowGlow : SoftGlow, Group: ListGroup));
        var right = x + w - S(Pad);
        switch (row.Kind)
        {
            case OptionKind.Switch:
                Check(row, x, y, w, h, right, enabled);
                break;
            case OptionKind.Choice:
                Choice(row, x, y, w, h, right, enabled);
                break;
            case OptionKind.Slider:
                Slider(row, y, right, h, hovered, enabled);
                break;
            case OptionKind.Button:
                Value(right, y, h, Caption(row), enabled ? Title : Faint);
                if (enabled) _rows.Insert(0, new Hit(x, y, w, h, (_, _) => row.Set(0), Option: row));
                break;
            case OptionKind.Key:
                Key(row, x, y, w, h, right, enabled);
                break;
        }
    }

    private static string Caption(OptionRow row) => NotNull(row) ? "›  " + (row.Shows?.Invoke() ?? "") : "";

    private void Key(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        if (!NotNull(row) || !Finite(right)) return;
        var waiting = ReferenceEquals(row, _binding);
        if (waiting) Lit(x, y, w, h);
        var color = enabled ? Soft : Faint;
        if (enabled && row.Warn?.Invoke() == true) color = HudCanvas.Error;
        if (waiting) color = Title;
        Value(right, y, h, Bound(row), color);
        if (!enabled) return;
        _rows.Insert(0, new Hit(x, y, w, h, (_, back) =>
        {
            if (back) EngineOptions.Unbind(row.Code);
            else Binding(row);
        }, Option: row));
    }

    private string Bound(OptionRow row)
    {
        if (!NotNull(row)) return "";
        return ReferenceEquals(row, _binding) ? T("key-waiting") : row.Shows?.Invoke() ?? "";
    }

    private void Check(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        if (!NotNull(row)) return;
        var (box, on) = (S(Box), Value(row) >= 0.5);
        if (!Assert(box > 4)) return;
        var top = y + (h - box) / 2;
        _canvas.Outline(right - box, top, box, box, enabled ? BoxLine : Faint);
        if (on) _canvas.Fill(right - box + 3, top + 3, box - 6, box - 6, enabled ? HudCanvas.Accent : Faint);
        if (enabled) _rows.Insert(0, new Hit(x, y, w, h, (_, _) => Stage(row, on ? 0 : 1), Option: row));
    }

    private void Choice(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        var names = row.Names;
        if (!Assert(names.Length > 0) || !Finite(right)) return;
        var at = Math.Clamp((int)Math.Round(Value(row)), 0, names.Length - 1);
        var (listed, open) = (names.Length > MaxCycled, ReferenceEquals(row, _picker));
        if (open) Lit(x, y, w, h);
        var color = enabled ? Soft : Faint;
        Value(right, y, h, Shown(row), open ? Title : color);
        if (!enabled) return;
        _rows.Insert(0, new Hit(x, y, w, h, (_, back) =>
        {
            if (listed && !back) (_picker, _pickScroll) = (ReferenceEquals(_picker, row) ? null : row, double.NaN);
            else Stage(row, (at + (back ? names.Length - 1 : 1)) % names.Length);
        }, Option: row));
    }

    private string Shown(OptionRow row)
    {
        var names = row.Names;
        if (!NotNull(names) || names.Length == 0) return "";
        var name = Parts(names[Math.Clamp((int)Math.Round(Value(row)), 0, names.Length - 1)]).Main;
        return names.Length > MaxCycled ? name + "  ›" : name;
    }

    private void Slider(OptionRow row, double y, double right, double h, bool hovered, bool enabled)
    {
        var value = Value(row);
        if (!Finite(value)) return;
        var text = row.ValueText(value);
        Value(right, y, h, text, enabled ? Soft : Faint);
        double w = S(TrackWidth), left = right - Widest(row) - S(Pad) - w, range = row.Max - row.Min;
        if (!enabled || !Assert(range > 0) || w <= S(KnobSize)) return;
        var fraction = Math.Clamp((value - row.Min) / range, 0, 1);
        if (hovered)
        {
            double track = S(Track), knob = S(KnobSize);
            _canvas.Fill(left, y + (h - track) / 2, w, track, Rgba.White(0.18), track / 2);
            _canvas.Fill(left, y + (h - track) / 2, w * fraction, track, HudCanvas.Accent, track / 2);
            _canvas.Fill(left + w * fraction - knob / 2, y + (h - knob) / 2, knob, knob, Rgba.White(0.95), knob / 2);
        }

        _rows.Insert(0, new Hit(left, y, w, h, (f, _) => Stage(row, row.At(f)), true, row));
    }

    // What a row shows at its right, so its label may run up to it
    private double ValueWidth(OptionRow row)
    {
        if (!NotNull(row) || !Assert(row.Label.Length > 0)) return 0;
        return row.Kind switch
        {
            OptionKind.Switch => S(Box),
            OptionKind.Choice => _canvas.TextWidth(_fonts.Text, Shown(row)),
            OptionKind.Button => _canvas.TextWidth(_fonts.Text, Caption(row)),
            OptionKind.Key => _canvas.TextWidth(_fonts.Text, Bound(row)),
            _ => Widest(row)
        };
    }

    // The widest value a slider can show, so its track stays put while the value changes
    // (up to 65 measures, so once per font size: a composition asks for it twice per slider)
    private double Widest(OptionRow row)
    {
        if (!NotNull(row) || !Assert(row.Step > 0)) return 0;
        if (_widest.TryGetValue(row, out var known)) return known;
        var (widest, steps) = (0.0, (int)Math.Min((row.Max - row.Min) / row.Step, MaxSteps));
        for (var i = 0; i <= Math.Min(steps, MaxSteps); i++)
            widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Min + i * row.Step)));
        widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Max)));
        if (_widest.Count < MaxItems) _widest[row] = widest;
        return widest;
    }

    // The text, cut to width with an ellipsis; the description shows it whole
    private string Fit(string text, double width)
    {
        if (!NotNull(text) || !Assert(width > 0) || _canvas.TextWidth(_fonts.Text, text) <= width) return text;
        var cut = text.Length;
        for (var i = 0; i < Math.Min(text.Length, MaxWords); i++)
        {
            cut = text.Length - 1 - i;
            if (cut <= 1 || _canvas.TextWidth(_fonts.Text, text[..cut] + "…") <= width) break;
        }

        return text[..Math.Max(1, cut)].TrimEnd() + "…";
    }

    private void Lit(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, Selected);
        _canvas.Fill(x + w - S(AccentBar), y, S(AccentBar), h, HudCanvas.Accent);
    }

    private void Band(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, HeaderBack);
        _canvas.Fill(x, y, S(AccentBar), h, Title);
    }

    private void Value(double right, double y, double h, string text, Rgba color)
    {
        if (NotNull(text) && Finite(right)) _canvas.Text(right - _canvas.TextWidth(_fonts.Text, text), y, h, _fonts.Text, text, color);
    }

    private void Description(Columns c)
    {
        if (c.Desc <= 0 || _picker is not null || _hover is not { } row) return; // an open window shows the hint itself
        double x = c.Width - c.Desc, pad = S(Pad), line = _fonts.TextRow;
        var hint = Markup().Replace(row.Hint ?? "", ""); // the game's hints carry VTML
        if (row.Locked?.Invoke() is { } locked) hint = locked + "\n" + hint;
        var (title, lines) = (Wrap(row.Label, _fonts.Header, c.Desc - 2 * pad), Wrap(hint, _fonts.Text, c.Desc - 2 * pad));
        var room = c.Height - c.Top - 3 * (S(ButtonHeight) + c.Gap) - 2 * pad - title.Count * _fonts.HeaderRow; // above the buttons
        if (!Assert(line > 0)) return;
        if (lines.Count * line > room) lines = [.. lines.Take(Math.Max(0, (int)(room / line)))];
        _canvas.Fill(x, c.Top, c.Desc, 2 * pad + (title.Count * _fonts.HeaderRow) + lines.Count * line, Panel);
        var y = c.Top + pad;
        foreach (var text in title.Bounded(MaxLines))
        {
            _canvas.Text(x + pad, y, _fonts.HeaderRow, _fonts.Header, text);
            y += _fonts.HeaderRow;
        }

        foreach (var text in lines.Bounded(MaxLines * 4))
        {
            _canvas.Text(x + pad, y, line, _fonts.Text, text, Soft);
            y += line;
        }
    }

    private void Buttons(Columns c)
    {
        double w = c.Desc > 0 ? Math.Min(c.Desc, S(ButtonWidth)) : c.Side, h = S(ButtonHeight);
        var x = c.Desc > 0 ? c.Width - w : 0;
        if (!Assert(w > 0) || !Assert(h > 0)) return;
        var y = c.Height - h;
        Button(x, y, w, h, T("done"), true, () =>
        {
            Apply();
            _ = Close();
        });
        y -= h + c.Gap;
        var count = _staged.Count;
        Button(x, y, w, h, count > 0 ? T("apply") + " (" + count + ")" : T("apply"), count > 0, Apply);
        if (count == 0) return;
        y -= h + c.Gap;
        Button(x, y, w, h, T("undo"), true, () =>
        {
            _staged.Clear();
            _dirty = true;
        });
    }

    private void Button(double x, double y, double w, double h, string text, bool enabled, Action click)
    {
        if (!NotNull(click) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, HeaderBack);
        _canvas.Text(x + (w - _canvas.TextWidth(_fonts.Text, text)) / 2, y, h, _fonts.Text, text, enabled ? null : Faint);
        if (enabled) _hits.Insert(0, new Hit(x, y, w, h, (_, _) => click(), Glow: StrongGlow, Group: ButtonGroup));
    }
}

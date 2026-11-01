using System.Globalization;
using Komet.Api;
using Komet.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The screen's layout: a header with the logo, the brand and one tab per section (Q/E); under it the section's pages,
// the page's rows and the detail of the row the keys act on; a footer with the date, the version, the changes and the
// keys. Panels are grounds drawn under the picture (Backs), so the light of the chosen row glides between them and the
// text; the list comes in from the side of the page it follows, names decoding into place.
internal sealed partial class OptionsScreen
{
    private const double PageRow = 36, PagesWidth = 280, DetailWidth = 420, ListMax = 1000, ListMin = 400, TitleRow = 56,
        SearchRow = 40, KeyWidth = 26, KeyHeight = 20;
    private const int TabGroup = 6, KeyGroup = 7, MaxSections = 64, MaxDetailLines = 10, MaxTitleLines = 3;
    private const float BackDepth = ScreenDepth - 0.6f, MarkDepth = ScreenDepth - 0.3f;

    private (double X, double Y, double Scale, double Alpha) _lens = (0, 0, 1, 1); // the picture's centre, scale, alpha
    private OptionRow? _cursor; // the row the keys act on, lit
    private Light _mark, _tabLine;

    private sealed record Frame(double Width, double Bottom, double Header, double Footer, double Top, double Body, double Gap,
        double PagesX, double Pages, double ListX, double List, double DetailX, double Detail);

    private Frame Geometry(double width, double bottom)
    {
        double gap = S(Math.Max(4, _style.Size("gap"))), room = width - 2 * S(Margin);
        var header = Math.Max(S(64), Math.Min(S(_style.Size("header")), bottom * 0.2));
        var footer = S(Math.Max(28, _style.Size("footer")));
        double top = header + gap * 1.4, body = bottom - footer - gap * 1.4, pages = S(PagesWidth), detail = S(DetailWidth);
        var list = Math.Min(S(ListMax), room - pages - detail - 2 * gap);
        if (list < S(ListMin)) (detail, list) = (0, Math.Min(S(ListMax), room - pages - gap));
        if (list < S(ListMin)) (pages, list) = (0, room);
        var total = pages + (pages > 0 ? gap : 0) + list + (detail > 0 ? gap + detail : 0);
        var x0 = Math.Round((width - total) / 2);
        var listX = x0 + (pages > 0 ? pages + gap : 0);
        return new Frame(width, bottom, header, footer, top, body, gap, x0, pages, listX, list, listX + list + gap, detail);
    }

    private bool Tabs(double width, double bottom)
    {
        if (!_canvas.Blank(width, bottom)) return false;
        var f = Geometry(width, bottom);
        Header(f);
        PageList(f);
        Listing(f);
        Detail(f);
        Footer(f);
        _canvas.Opacity = 1;
        return true;
    }

    // A panel's ground under the picture, with the style's corner marks
    private void Ground(double x, double y, double w, double h, Rgba color)
    {
        if (!Finite(x + y) || w <= 0 || h <= 0 || !Assert(_map.Backs.Count < MaxItems)) return;
        _map.Backs.Add(new Plate(x, y, w, h, color, S(_style.Size("ticks")), _style["faint"], Panel: true));
    }

    private void Header(Frame f)
    {
        var (h, x0) = (f.Header, f.PagesX);
        if (!Assert(h > 0) || !Assert(x0 >= 0)) return;
        _map.Backs.Add(new Plate(0, 0, f.Width, h, HeaderBack));
        _map.Backs.Add(new Plate(0, h - 1, f.Width, 1, _style["line"]));
        var logo = Math.Min(S(_style.Size("logo")), h - S(40));
        var x = x0;
        if (logo > 0 && _style.Logo is { } image)
        {
            // the pixel-art logo in whole steps, in a frame of blocks a little larger than its pixels
            var side = HudCanvas.Fitted(image, logo);
            var block = Math.Max(2, Math.Round(side / 24));
            var frame = side + 4 * block;
            var top = Math.Round((h - frame) / 2);
            HudUi.PixelFrame(_canvas, x, top, frame, block, (_style["backdrop"] with { A = 0.85 }, _style["soft"]));
            _canvas.Image(image, x + 2 * block, top + 2 * block, side);
            x += frame + block + S(18);
        }

        Spaced(x, h / 2 - S(26), S(30), _fonts.Big, "KOMET", S(_style.Size("tracking")), null);
        var brandEnd = x + SpacedWidth(_fonts.Big, "KOMET", S(_style.Size("tracking")));
        Spaced(x, h / 2 + S(6), S(18), _fonts.Small, T("brand-sub").ToUpperInvariant(), S(_style.Size("tracking")) * 0.6, Faint);
        SectionTabs(f, Math.Max(brandEnd, x + S(160)) + S(32), h);
    }

    // One tab per section with Q and E beside; past the room they shift so the open one shows
    private void SectionTabs(Frame f, double from, double h)
    {
        var sections = Sections();
        var right = f.PagesX + f.Pages + (f.Pages > 0 ? f.Gap : 0) + f.List + (f.Detail > 0 ? f.Gap + f.Detail : 0);
        double key = S(KeyWidth), pad = S(12), tracking = S(1.5), row = S(38), y = Math.Round((h - row) / 2);
        var room = right - from - 2 * (key + S(6));
        if (room < S(80) || !Assert(sections.Count > 0)) return;
        var widths = sections.ConvertAll(s => Math.Min(S(200), SpacedWidth(_fonts.Header, s.ToUpperInvariant(), tracking)) + 2 * pad);
        var current = Math.Max(0, sections.IndexOf(Section()));
        double total = widths.Sum(), before = widths.Take(current).Sum(), shift = 0;
        if (total > room) shift = Math.Clamp(before + widths[current] / 2 - room / 2, 0, total - room);
        var x = Math.Max(from, right - key - S(6) - Math.Min(total, room)) - shift;
        KeyCap(x + shift - key - S(6), y + (row - S(KeyHeight)) / 2, "Q", () => Section(-1));
        KeyCap(Math.Min(right - key, x + shift + Math.Min(total, room) + S(6)), y + (row - S(KeyHeight)) / 2, "E", () => Section(1));
        _canvas.Clip(x + shift, y, Math.Min(total, room), row);
        for (var i = 0; i < Math.Min(sections.Count, MaxSections); i++)
        {
            var (name, open, w) = (sections[i], i == current, widths[i]);
            var text = _fonts.Header;
            Spaced(x + pad, y, row, text, Fit(name.ToUpperInvariant(), w - 2 * pad, text), tracking, open ? null : Faint);
            if (open && !Filtering) _map.Underline = (x + pad, y + row - S(2), w - 2 * pad, S(2));
            var section = name;
            if (x + w > x + shift && x < x + shift + room)
                _map.Hits.Add(new Hit(Math.Max(x, from), y, Math.Min(w, room), row, (_, _) => Section(section), Glow: SoftGlow, Group: TabGroup));
            x += w;
        }

        _canvas.Unclip();
    }

    private void PageList(Frame f)
    {
        if (f.Pages <= 0 || !Assert(f.Body > f.Top)) return;
        var (x, w, row) = (f.PagesX, f.Pages, S(PageRow));
        if (!Assert(row > 0)) return;
        Ground(x, f.Top, w, f.Body - f.Top, Panel);
        SearchField(x, f.Top, w);
        var top = f.Top + S(SearchRow) + S(Spacer);
        var view = f.Body - top - S(Spacer);
        var pages = Array.FindAll(_pages, p => p.Section == Section());
        _map.SideView = (x, top, w, view);
        _sideScroll = Math.Clamp(Finite(_sideScroll) ? _sideScroll : 0, 0, Math.Max(0, pages.Length * row - view));
        _canvas.Clip(x, top, w, view);
        var y = top - _sideScroll;
        foreach (var page in pages.Bounded(MaxPages))
        {
            var chosen = !Filtering && page.Id == _page;
            if (chosen) _canvas.Fill(x, y + S(6), S(3), row - S(12), Accent);
            var (title, font) = Shrunk(page.Title, w - 2 * S(Pad), _fonts.Text);
            _canvas.Text(x + S(Pad), y, row, font, title, chosen ? null : Soft);
            if (y + row > top && y < top + view)
                _map.Hits.Add(new Hit(x, Math.Max(y, top), w, Math.Min(y + row, top + view) - Math.Max(y, top), (_, _) => Select(page),
                    Glow: RowGlow, Group: SideGroup));
            y += row;
        }

        _canvas.Unclip();
    }

    private void SearchField(double x, double y, double w)
    {
        var h = S(SearchRow);
        if (!Assert(w > 2 * h) || !Finite(y)) return;
        var text = _searching ? "" : T("search");
        if (_query.Length > 0) text = _query;
        var (shown, font) = Shrunk(text + (_searching ? "_" : ""), w - 2 * S(Pad) - S(8), _fonts.Text);
        _canvas.Text(x + S(Pad), y, h, font, shown, _query.Length > 0 ? null : Faint);
        _canvas.Fill(x + S(Pad), y + h - 1, w - 2 * S(Pad), 1, _style["line"]);
        _map.Hits.Add(new Hit(x, y, w, h, (_, _) => _searching = true, Glow: SoftGlow, Group: TopGroup));
        if (_query.Length == 0) return;
        _canvas.Text(x + w - S(Pad) - _canvas.TextWidth(_fonts.Header, "×"), y, h, _fonts.Header, "×", Soft);
        _map.Hits.Insert(0, new Hit(x + w - h, y, h, h, (_, _) => (_query, _searching) = ("", false), Glow: StrongGlow, Group: TopGroup));
    }

    private void Listing(Frame f)
    {
        var (x, w) = (f.ListX, f.List);
        if (!Assert(w > 0) || !Assert(f.Body > f.Top)) return;
        Ground(x, f.Top, w, f.Body - f.Top, Panel);
        var page = Array.Find(_pages, p => p.Id == _page);
        var title = _query.Length > 0 ? T("search-title") + ": " + _query : page?.Title ?? "";
        if (_reviewing) title = T("changes-title", _staged.Count);
        var (head, tracking) = (S(TitleRow), S(_style.Size("tracking")) * 0.5);
        _map.Title = (x + S(Pad), f.Top, w * 0.7, head, Fit(title.ToUpperInvariant(), w * 0.7, _fonts.Big), tracking); // its own texture
        var pages = Array.FindAll(_pages, p => p.Section == Section());
        var at = Array.FindIndex(pages, p => p.Id == _page);
        if (!Filtering && at >= 0)
        {
            var index = string.Create(CultureInfo.InvariantCulture, $"{at + 1:00} / {pages.Length:00}");
            Spaced(x + w - S(Pad) - SpacedWidth(_fonts.Small, index, S(2)), f.Top, head, _fonts.Small, index, S(2), Faint);
        }

        _canvas.Fill(x + S(Pad), f.Top + head - 1, w - 2 * S(Pad), 1, _style["line"]);
        if (!Filtering && IsDoc(page)) DocView(page!, x, f.Top + head + S(Spacer), w, f.Body - S(Spacer));
        else Rows(x, f.Top + head + S(Spacer), w, f.Body - S(Spacer));
    }

    private void Rows(double x, double top, double w, double bottom)
    {
        var items = Items();
        var row = S(Math.Max(24, _style.Size("row")));
        double total = 0;
        foreach (var item in items.Bounded(MaxItems)) total += item.Kind == ItemKind.Row ? row : item.Height;
        var view = bottom - top;
        if (!Assert(view > 0) || !Assert(row > 0)) return;
        _scroll = Math.Clamp(Finite(_scroll) ? _scroll : 0, 0, Math.Max(0, total - view));
        _map.View = (x, top, w, view);
        _canvas.Clip(x, top, w, view);
        var y = top - _scroll;
        foreach (var item in items.Bounded(MaxItems))
        {
            var h = item.Kind == ItemKind.Row ? row : item.Height;
            if (y + h > top && y < bottom)
            {
                TabItem(item, x + S(Spacer), y, w - 2 * S(Spacer), h, 1);
                if (_map.Bands.Count < MaxItems) _map.Bands.Add((Math.Max(y, top), Math.Min(y + h, bottom) - Math.Max(y, top)));
            }

            y += h;
        }

        _canvas.Opacity = 1;
        _canvas.Unclip();
        if (total <= view) return;
        var (bar, thumb) = (S(2), Math.Max(S(24), view * view / total)); // a thin bar at the panel's edge, as Pragmatic's
        _canvas.Fill(x + w - S(6), top, bar, view, _style["line"]);
        _canvas.Fill(x + w - S(6), top + (view - thumb) * _scroll / Math.Max(1, total - view), bar, thumb, _style["soft"]);
    }

    private void TabItem(Item item, double x, double y, double w, double h, double come)
    {
        if (!Finite(y)) return;
        switch (item.Kind)
        {
            case ItemKind.Group:
                var text = Decode((item.Row?.Shows?.Invoke() ?? item.Text).ToUpperInvariant(), come * 1.3);
                var tw = SpacedWidth(_fonts.Small, text, S(2));
                Spaced(x + S(Pad) * 0.6, y, h, _fonts.Small, text, S(2), Faint);
                _canvas.Fill(x + S(Pad) * 0.6 + tw + S(10), y + h / 2, Math.Max(0, w - tw - S(Pad) * 1.2 - S(10)), 1, _style["line"]);
                break;
            case ItemKind.Page:
                var (page, font) = Shrunk(item.Text, w - S(Pad), _fonts.Header);
                _canvas.Text(x + S(Pad) * 0.6, y, h, font, page, Title);
                break;
            case ItemKind.Row when item.Row is { } row:
                TabGuarded(row, x, y, w, h, come);
                break;
        }
    }

    // The detail of the row the keys act on (else the one under the mouse)
    private void Detail(Frame f)
    {
        if (f.Detail <= 0 || !Assert(f.Body > f.Top)) return;
        var (x, w, pad) = (f.DetailX, f.Detail, S(Pad));
        if (!Assert(w > 2 * pad)) return;
        Ground(x, f.Top, w, f.Body - f.Top, Panel);
        var page = Array.Find(_pages, p => p.Id == _page);
        if (!Filtering && IsDoc(page))
        {
            DocOutline(page!, x, f.Top, w, f.Body); // a doc page: its outline in place of a row's detail
            return;
        }

        if ((_cursor ?? _hover) is not { } row)
        {
            if (page?.Legend?.Invoke() is { Count: > 0 } legend) Legend(legend, x + pad, f.Top + pad, w - 2 * pad);
            return;
        }

        var y = f.Top + pad;
        Spaced(x + pad, y, S(18), _fonts.Small, ((page?.Section ?? "") + " · " + T("setting")).ToUpperInvariant(), S(2), Faint);
        y += S(26);
        var label = Wrap(row.Label, _fonts.Header, w - 2 * pad);
        for (var i = 0; i < Math.Min(label.Count, MaxTitleLines); i++) // cut on purpose: Bounded would log a long label as a failed check
        {
            _canvas.Text(x + pad, y, _fonts.HeaderRow, _fonts.Header, label[i]);
            y += _fonts.HeaderRow;
        }

        var value = DetailValue(row);
        if (value.Length > 0)
        {
            _canvas.Text(x + pad, y, S(40), _fonts.Huge, Fit(value, w - 2 * pad, _fonts.Huge), _staged.ContainsKey(row) ? _style["changed"] : null);
            y += S(44);
        }

        _canvas.Fill(x + pad, y + S(6), w - 2 * pad, 1, _style["line"]);
        y += S(18);
        if (row.Facts?.Invoke() is { Count: > 0 } facts)
        {
            y = Card(facts, x + pad, y, w - 2 * pad);
            _canvas.Fill(x + pad, y + S(2), w - 2 * pad, 1, _style["line"]);
            y += S(14);
        }

        var hint = Markup.Replace(row.Hint ?? "", "");
        if (row.Locked?.Invoke() is { } locked) hint = locked + "\n" + hint;
        if (row.Kind == OptionKind.Slider && double.IsFinite(row.Standard)) hint += (hint.Length > 0 ? "\n\n" : "") + T("standard") + ": " + row.ValueText(row.Standard);
        var (lines, room) = (Wrap(hint, _fonts.Text, w - 2 * pad), (int)((f.Body - y - pad) / Math.Max(1, _fonts.TextRow)));
        for (var i = 0; i < Math.Min(Math.Min(lines.Count, room), MaxDetailLines * 2); i++)
        {
            _canvas.Text(x + pad, y, _fonts.TextRow, _fonts.Text, lines[i], Soft);
            y += _fonts.TextRow;
        }
    }

    // What a page's symbols mean, while no row is chosen
    private void Legend(IReadOnlyList<(TagIcon Icon, TagKind Kind, string Text)> legend, double x, double y, double w)
    {
        Spaced(x, y, S(18), _fonts.Small, T("legend").ToUpperInvariant(), S(2), Faint);
        _ = Card(legend, x, y + S(30), w);
    }

    private string DetailValue(OptionRow row) => NotNull(row) && Assert(row.Label.Length > 0)
        ? row.Kind switch
        {
            OptionKind.Switch => OnOff(Value(row) >= 0.5),
            OptionKind.Choice => Shown(row),
            OptionKind.Slider => row.ValueText(Value(row)),
            OptionKind.Key => Bound(row),
            _ => ""
        }
        : "";

    private static string OnOff(bool on) => on ? T("on") : T("off");

    private void Footer(Frame f)
    {
        var (h, y) = (f.Footer, f.Bottom - f.Footer);
        if (!Assert(h > 0) || !Assert(f.Width > 0)) return;
        _map.Backs.Add(new Plate(0, y, f.Width, h, HeaderBack));
        _map.Backs.Add(new Plate(0, y, f.Width, 1, _style["line"]));
        var culture = Culture();
        var x = f.PagesX;
        var date = DateTime.Now.ToString("ddd dd.MM.yyyy   HH:mm", culture);
        _canvas.Text(x, y, h, _fonts.Text, date, Soft);
        x += _canvas.TextWidth(_fonts.Text, date) + S(28);
        var version = "Komet " + Version(T("section-komet"));
        _canvas.Text(x, y, h, _fonts.Text, version, Faint);
        x += _canvas.TextWidth(_fonts.Text, version) + S(10);
        x = VersionChips(x, y, h) + S(18);
        if (_staged.Count > 0)
        {
            // the count opens the list of what Enter would apply
            var count = _staged.Count == 1 ? T("change") : T("changes", _staged.Count);
            var cw = _canvas.TextWidth(_fonts.Text, count);
            _canvas.Text(x, y, h, _fonts.Text, count, _style["changed"]);
            if (_reviewing) _canvas.Fill(x, y + h - S(10), cw, Math.Max(1, S(1.5)), _style["changed"]);
            _map.Hits.Add(new Hit(x - S(6), y + S(4), cw + S(12), h - S(8), (_, _) => Review(), Glow: SoftGlow, Group: KeyGroup));
        }
        var right = f.ListX + f.List + (f.Detail > 0 ? f.Gap + f.Detail : 0);
        right = KeyAction(right, y, h, "Enter", T("apply"), Apply);
        right = KeyAction(right, y, h, "Esc", T("close"), () => _ = Close());
        right = KeyAction(right, y, h, "R", T("standard"), ResetPage);
        if (_staged.Count > 0) _ = KeyAction(right, y, h, "Del", T("undo"), Unstage);
    }

    // The edition and what the update check found, outlined chips after the version; a click opens Komet's version window
    private double VersionChips(double x, double y, double h)
    {
        if (VersionState?.Invoke() is not { Count: > 0 } chips) return x;
        var (ch, pad) = (Math.Min(h - S(8), S(22)), S(7));
        var top = y + (h - ch) / 2;
        foreach (var (text, color) in chips.Bounded(4))
        {
            var w = _canvas.TextWidth(_fonts.Small, text) + 2 * pad;
            _canvas.Fill(x, top, w, ch, color with { A = 0.12 });
            _canvas.Outline(x, top, w, ch, color with { A = 0.7 });
            _canvas.Text(x + pad, top, ch, _fonts.Small, text, color);
            if (OpenVersion is { } open) _map.Hits.Add(new Hit(x, top, w, ch, (_, _) => open(), Glow: SoftGlow, Group: KeyGroup));
            x += w + S(6);
        }

        return x - S(6);
    }

    // A key and its caption ending at right; returns where the next one ends
    private double KeyAction(double right, double y, double h, string key, string caption, Action act)
    {
        if (!NotNull(act) || !Finite(right) || !Assert(h > 0)) return right;
        var cw = _canvas.TextWidth(_fonts.Text, caption);
        var kw = Math.Max(S(KeyWidth), _canvas.TextWidth(_fonts.Small, key) + S(10));
        var x = right - cw - S(8) - kw;
        KeyCap(x, y + (h - S(KeyHeight)) / 2, key, null, kw);
        _canvas.Text(x + kw + S(8), y, h, _fonts.Text, caption, Soft);
        _map.Hits.Add(new Hit(x - S(6), y + S(4), right - x + S(12), h - S(8), (_, _) => act(), Glow: SoftGlow, Group: KeyGroup));
        return x - S(24);
    }

    private void KeyCap(double x, double y, string key, Action? act, double width = 0)
    {
        var (w, h) = (width > 0 ? width : S(KeyWidth), S(KeyHeight));
        if (!NotNull(key) || !Finite(x + y) || w <= 1) return;
        _canvas.Outline(x, y, w, h, Faint);
        if (_map.Caps.Count < MaxSections) _map.Caps[key] = (x, y, w, h); // where a key's press lights up
        _canvas.Text(x + (w - _canvas.TextWidth(_fonts.Small, key)) / 2, y, h, _fonts.Small, key, Soft);
        if (act is not null) _map.Hits.Add(new Hit(x - S(4), y - S(4), w + S(8), h + S(8), (_, _) => act(), Glow: SoftGlow, Group: TabGroup));
    }

    // Text with letter spacing, one character at a time
    private void Spaced(double x, double y, double h, CairoFont font, string text, double tracking, Rgba? color)
    {
        if (!NotNull(text) || !Finite(x + y) || !Assert(h > 0)) return;
        if (tracking <= 0.01)
        {
            _canvas.Text(x, y, h, font, text, color);
            return;
        }

        foreach (var c in text.ToCharArray().Bounded(256))
        {
            var s = c.ToString();
            _canvas.Text(x, y, h, font, s, color);
            x += _canvas.TextWidth(font, s) + tracking;
        }
    }

    private double SpacedWidth(CairoFont font, string text, double tracking) => NotNull(text) && Finite(tracking)
        ? _canvas.TextWidth(font, text) + Math.Max(0, tracking) * Math.Max(0, text.Length - 1)
        : 0;

    private List<string> Sections()
    {
        List<string> sections = [];
        foreach (var page in _pages.Bounded(MaxPages))
            if (!sections.Contains(page.Section))
                sections.Add(page.Section);
        return sections;
    }

    private string Section() => Array.Find(_pages, p => p.Id == _page)?.Section ?? (_pages.Length > 0 ? _pages[0].Section : "");

    private static CultureInfo Culture()
    {
        try
        {
            return Lang.CurrentLocale is { Length: > 0 } locale ? CultureInfo.GetCultureInfo(locale) : CultureInfo.InvariantCulture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

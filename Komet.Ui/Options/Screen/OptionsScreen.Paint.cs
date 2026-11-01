using System.Text.RegularExpressions;
using Komet.Api;
using Komet.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

internal sealed partial class OptionsScreen
{
    private static readonly Regex Markup = new("<[^>]*>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private bool Close()
    {
        _staged.Clear();
        return TryClose();
    }

    private string Version(string section)
    {
        if (!NotNull(section)) return "";
        if (section == T("section-game")) return GameVersion.ShortGameVersion;
        var mod = capi.ModLoader?.Mods?.FirstOrDefault(m => m.Info?.Name == section || m.Info?.ModID == section);
        return mod?.Info?.Version ?? "";
    }

    private void Select(OptionPage page)
    {
        if (!NotNull(page) || !Assert(page.Id.Length > 0)) return;
        var (from, to) = (Array.FindIndex(_pages, p => p.Id == _page), Array.IndexOf(_pages, page));
        if (page.Id != _page && Moves("slide")) (_slide, _slideFor, _slideDir) = (SlidePending, page.Id, to >= from ? 1 : -1);
        (_page, _scroll, _query, _searching, _dirty, _focus) = (page.Id, 0, "", false, true, null);
        _reviewing = false;
        _cursor = null;
    }

    private const double TagGap = 5, TagHeight = 20;
    private const int MaxFacts = 16;
    private static readonly Rgba BadgeInk = new(0.05, 0.07, 0.09, 1);
    private const int MaxTags = 6;

    // A row's markers as small square badges after its label: the binding filled in its colour, the rest grey
    private void Tags(OptionRow row, double x, double y, double h)
    {
        if (row.Tags?.Invoke() is not { Count: > 0 } tags) return;
        var (size, left) = (S(TagHeight), x + S(TagGap) * 1.5);
        foreach (var (icon, kind) in tags.Take(MaxTags).Bounded(MaxTags))
        {
            Badge(icon, kind, left, y + (h - size) / 2, size);
            left += size + S(TagGap);
        }
    }

    private static double TagsWidth(OptionRow row)
    {
        if (row?.Tags?.Invoke() is not { Count: > 0 } tags) return 0;
        var n = Math.Min(tags.Count, MaxTags);
        return S(TagGap) * 1.5 + n * (S(TagHeight) + S(TagGap));
    }

    private static readonly Rgba VulkanRed = new(0.78, 0.16, 0.22, 1), VulkanOrange = new(0.96, 0.6, 0.18, 1), GlTeal = new(0.27, 0.68, 0.78, 1);

    // One symbol in its square: the binding and the renderer filled in their colours, the rest grey
    private void Badge(TagIcon icon, TagKind kind, double x, double y, double size)
    {
        var (back, ink) = kind switch
        {
            TagKind.Speaks => (_style["good"], BadgeInk),
            TagKind.TakesOver => (Accent, BadgeInk),
            TagKind.VulkanOk => (VulkanRed, new Rgba(1, 1, 1, 1)),
            TagKind.VulkanPartial => (VulkanOrange, BadgeInk),
            TagKind.OpenGl => (GlTeal, BadgeInk),
            TagKind.Warn => (ChipBack, HudCanvas.Error),
            _ => (ChipBack, Soft)
        };
        _canvas.Fill(x, y, size, size, back, S(4));
        Icon(icon, x + size / 2, y + size / 2, size * 0.62, ink);
    }

    // A card: each fact a symbol and its line beside it, wrapped; returns where the next thing goes
    private double Card(IReadOnlyList<(TagIcon Icon, TagKind Kind, string Text)> facts, double x, double y, double w)
    {
        if (!Assert(w > 0)) return y;
        var (size, gap) = (S(TagHeight), S(10));
        foreach (var (icon, kind, text) in facts.Take(MaxFacts).Bounded(MaxFacts))
        {
            if (kind == TagKind.Heading) // a group's title, set apart above its symbols
            {
                Spaced(x, y + S(4), S(18), _fonts.Small, text.ToUpperInvariant(), S(2), Faint);
                y += S(26);
                continue;
            }

            var lines = Wrap(text, _fonts.Text, w - size - gap);
            var h = Math.Max(size, lines.Count * _fonts.TextRow);
            Badge(icon, kind, x, y + (Math.Min(h, _fonts.TextRow) - size) / 2, size);
            var ty = y;
            foreach (var line in lines.Bounded(4))
            {
                _canvas.Text(x + size + gap, ty, _fonts.TextRow, _fonts.Text, line, kind == TagKind.Warn ? HudCanvas.Error : null);
                ty += _fonts.TextRow;
            }

            y += h + S(6);
        }

        return y;
    }

    // A symbol of size s around (cx, cy), in lines, rings and blocks
    private void Icon(TagIcon icon, double cx, double cy, double s, Rgba ink)
    {
        if (!Finite(cx) || !Finite(cy)) return;
        var (w, r) = (Math.Max(1.2, s * 0.13), s / 2);
        switch (icon)
        {
            case TagIcon.Link: // two rings linked
                _canvas.Arc(cx - r * 0.38, cy, r * 0.52, 0, 2 * Math.PI, ink, w);
                _canvas.Arc(cx + r * 0.38, cy, r * 0.52, 0, 2 * Math.PI, ink, w);
                break;
            case TagIcon.Comet: // a round head up right, its tail fanning out down left
                _canvas.Circle(cx + r * 0.38, cy - r * 0.38, r * 0.48, ink);
                _canvas.Line(cx + r * 0.05, cy - r * 0.05, cx - r, cy + r * 0.25, ink, w);
                _canvas.Line(cx + r * 0.05, cy - r * 0.05, cx - r * 0.8, cy + r * 0.8, ink, w);
                _canvas.Line(cx + r * 0.05, cy - r * 0.05, cx - r * 0.25, cy + r, ink, w);
                break;
            case TagIcon.Eye: // lids and a pupil
                _canvas.Arc(cx, cy + r * 0.9, r * 1.25, Math.PI * 1.22, Math.PI * 1.78, ink, w);
                _canvas.Arc(cx, cy - r * 0.9, r * 1.25, Math.PI * 0.22, Math.PI * 0.78, ink, w);
                _canvas.Circle(cx, cy, r * 0.3, ink);
                break;
            case TagIcon.File: // a sheet with lines
                _canvas.Outline(cx - r * 0.7, cy - r, r * 1.4, r * 2, ink);
                for (var i = 0; i < 3; i++) _canvas.Line(cx - r * 0.4, cy - r * 0.45 + i * r * 0.45, cx + r * 0.4, cy - r * 0.45 + i * r * 0.45, ink, w * 0.8);
                break;
            case TagIcon.Folder: // a folder and its tab
                _canvas.Fill(cx - r, cy - r * 0.45, r * 2, r * 1.3, ink, S(1));
                _canvas.Fill(cx - r, cy - r * 0.8, r * 0.9, r * 0.5, ink, S(1));
                break;
            case TagIcon.Code: // < >
                _canvas.Line(cx - r * 0.25, cy - r * 0.6, cx - r, cy, ink, w);
                _canvas.Line(cx - r, cy, cx - r * 0.25, cy + r * 0.6, ink, w);
                _canvas.Line(cx + r * 0.25, cy - r * 0.6, cx + r, cy, ink, w);
                _canvas.Line(cx + r, cy, cx + r * 0.25, cy + r * 0.6, ink, w);
                break;
            case TagIcon.Versions: // two sheets, one behind the other
                _canvas.Outline(cx - r * 0.9, cy - r * 0.6, r * 1.2, r * 1.6, ink);
                _canvas.Outline(cx - r * 0.3, cy - r, r * 1.2, r * 1.6, ink);
                break;
            case TagIcon.NoVulkan: // a V struck through
                _canvas.Line(cx - r * 0.75, cy - r * 0.7, cx, cy + r * 0.75, ink, w);
                _canvas.Line(cx, cy + r * 0.75, cx + r * 0.75, cy - r * 0.7, ink, w);
                _canvas.Line(cx - r, cy + r, cx + r, cy - r, ink, w);
                break;
            case TagIcon.Pause: // two bars
                _canvas.Fill(cx - r * 0.65, cy - r * 0.8, r * 0.45, r * 1.6, ink);
                _canvas.Fill(cx + r * 0.2, cy - r * 0.8, r * 0.45, r * 1.6, ink);
                break;
            case TagIcon.Warning: // !
                _canvas.Fill(cx - r * 0.16, cy - r, r * 0.32, r * 1.3, ink, S(1));
                _canvas.Circle(cx, cy + r * 0.75, r * 0.2, ink);
                break;
            default:
                Renderer(icon, cx, cy, r, w, ink);
                break;
        }
    }

    // An isometric cube: its outline and the three edges that meet in front
    private void Cube(double cx, double cy, double r, double w, Rgba ink)
    {
        var (dx, dy) = (r * 0.87, r * 0.5);
        (double X, double Y)[] rim = [(cx, cy - r), (cx + dx, cy - dy), (cx + dx, cy + dy), (cx, cy + r), (cx - dx, cy + dy), (cx - dx, cy - dy)];
        for (var i = 0; i < 6; i++) _canvas.Line(rim[i].X, rim[i].Y, rim[(i + 1) % 6].X, rim[(i + 1) % 6].Y, ink, w);
        _canvas.Line(cx, cy, cx, cy + r, ink, w);
        _canvas.Line(cx, cy, cx + dx, cy - dy, ink, w);
        _canvas.Line(cx, cy, cx - dx, cy - dy, ink, w);
    }

    // The renderer's and the card's symbols
    private void Renderer(TagIcon icon, double cx, double cy, double r, double w, Rgba ink)
    {
        switch (icon)
        {
            case TagIcon.Vulkan: // a volcano, as the word says: its slopes, the crater, three sparks
                _canvas.Line(cx - r, cy + r * 0.85, cx - r * 0.3, cy - r * 0.3, ink, w);
                _canvas.Line(cx - r * 0.3, cy - r * 0.3, cx + r * 0.3, cy - r * 0.3, ink, w);
                _canvas.Line(cx + r * 0.3, cy - r * 0.3, cx + r, cy + r * 0.85, ink, w);
                _canvas.Line(cx - r, cy + r * 0.85, cx + r, cy + r * 0.85, ink, w);
                _canvas.Line(cx, cy - r * 0.45, cx, cy - r, ink, w);
                _canvas.Line(cx - r * 0.2, cy - r * 0.5, cx - r * 0.55, cy - r * 0.9, ink, w);
                _canvas.Line(cx + r * 0.2, cy - r * 0.5, cx + r * 0.55, cy - r * 0.9, ink, w);
                break;
            case TagIcon.OpenGl: // a wire cube, the old 3D sign
                Cube(cx, cy, r, w, ink);
                break;
            case TagIcon.Shader: // a triangle, the shape a shader draws
                _canvas.Line(cx, cy - r * 0.85, cx + r * 0.9, cy + r * 0.7, ink, w);
                _canvas.Line(cx + r * 0.9, cy + r * 0.7, cx - r * 0.9, cy + r * 0.7, ink, w);
                _canvas.Line(cx - r * 0.9, cy + r * 0.7, cx, cy - r * 0.85, ink, w);
                break;
            case TagIcon.Replaces: // one in, one out
                _canvas.Line(cx - r, cy - r * 0.35, cx + r * 0.7, cy - r * 0.35, ink, w);
                _canvas.Line(cx + r * 0.3, cy - r * 0.75, cx + r * 0.75, cy - r * 0.35, ink, w);
                _canvas.Line(cx + r, cy + r * 0.35, cx - r * 0.7, cy + r * 0.35, ink, w);
                _canvas.Line(cx - r * 0.3, cy + r * 0.75, cx - r * 0.75, cy + r * 0.35, ink, w);
                break;
        }
    }

    private static string Caption(OptionRow row) => "›  " + (row.Shows?.Invoke() ?? "");

    private void Key(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        if (!Finite(right)) return;
        var waiting = ReferenceEquals(row, _binding);
        if (waiting) Lit(x, y, w, h);
        var color = enabled ? Soft : Faint;
        if (enabled && row.Warn?.Invoke() == true) color = HudCanvas.Error;
        if (waiting) color = Title;
        var cross = S(ClearWidth); // a click on it unbinds
        var unbound = EngineOptions.Unbound(row.Code);
        RightText(right - (unbound || waiting ? 0 : cross), y, h, Bound(row), color);
        if (!enabled) return;
        _map.Rows.Insert(0, new Hit(x, y, w, h, (_, back) =>
        {
            if (back) EngineOptions.Defaulted(row.Code);
            else Binding(row);
        }, Option: row));
        if (unbound || waiting) return;
        RightText(right, y, h, "×", ReferenceEquals(row, _hover) ? Title : Faint);
        _map.Rows.Insert(0, new Hit(right - cross, y, cross, h, (_, _) => EngineOptions.Unbind(row.Code), Option: row));
    }

    private string Bound(OptionRow row)
    {
        return ReferenceEquals(row, _binding) ? T("key-waiting") : row.Shows?.Invoke() ?? "";
    }

    // A choice's current name, without an aside after a tab
    private string Shown(OptionRow row)
    {
        var names = row.Names;
        if (names.Length == 0) return "";
        var at = Math.Clamp((int)Math.Round(Value(row)), 0, names.Length - 1);
        return Parts(names[at]).Main;
    }

    // "name\taside": the aside is a note after the name (a version, a state)
    private static (string Main, string Aside) Parts(string name)
    {
        var tab = NotNull(name) ? name.IndexOf('\t', StringComparison.Ordinal) : -1;
        return tab < 0 ? (name ?? "", "") : (name[..tab], name[(tab + 1)..]);
    }

    // The names a choice offers; any after them only name a state it shows (OptionPage.Offering)
    private static int Offered(OptionRow row) =>
        Assert(row.Max < row.Names.Length) ? Math.Max(1, (int)row.Max + 1) : 1;

    // Keeps the track still; up to 65 measures, cached per font size
    private double Widest(OptionRow row)
    {
        if (!Assert(row.Step > 0)) return 0;
        if (_widest.TryGetValue(row, out var known)) return known;
        var (widest, steps) = (0.0, (int)Math.Min((row.Max - row.Min) / row.Step, MaxSteps));
        for (var i = 0; i <= Math.Min(steps, MaxSteps); i++)
            widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Min + i * row.Step)));
        widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Max)));
        if (_widest.Count < MaxItems) _widest[row] = widest;
        return widest;
    }

    // Cut with an ellipsis where it would run past width (the hover description shows it whole)
    private string Fit(string text, double width, CairoFont? font = null) =>
        NotNull(text) && Assert(width > 0) ? _canvas.Fit(font ?? _fonts.Text, text, width, trimmed: true) : text;

    private void Lit(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, Selected);
        _canvas.Fill(x + w - S(AccentBar), y, S(AccentBar), h, Accent);
    }

    private void RightText(double right, double y, double h, string text, Rgba color)
    {
        if (NotNull(text) && Finite(right)) _canvas.Text(right - _canvas.TextWidth(_fonts.Text, text), y, h, _fonts.Text, text, color);
    }

}

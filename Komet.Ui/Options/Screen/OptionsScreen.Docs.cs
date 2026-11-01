using Komet.Api;
using Komet.Hud;
using static Komet.Options.OptionsText;
using Komet.Core;

namespace Komet.Options;

// The in-game docs' reader (KometDocs): a doc page's blocks laid out once per width and font size, drawn where the rows go
// with the list's scroll, a code block copied with a click; the outline beside it jumps to a heading.
internal sealed partial class OptionsScreen
{
    private const int MaxDocLines = 8192;
    private const double CopiedSeconds = 1.6;

    // A laid out piece: text at X, a band (code, a table's head) when Fill, a hairline when Line; Block: its block's index
    private sealed record DocLine(double Y, double H, double X, string Text, CairoFont? Font, Rgba? Color, int Block,
        double W = 0, bool Fill = false, bool Line = false, bool Framed = false);

    private sealed record DocLayout(string Page, double Width, int Epoch, List<DocLine> Lines, double Total, Dictionary<int, double> Anchors,
        double LastAnchor);

    private static readonly char[] CodeMarks = [DocText.CodeOpen, DocText.CodeClose];

    private DocLayout? _doc;
    private (int Block, long At) _copied = (-1, 0);

    private static bool IsDoc(OptionPage? page) => page?.Doc is { Count: > 0 };

    // The page's text in a panel column from top to bottom; the list's scroll moves it
    private void DocView(OptionPage page, double x, double top, double w, double bottom)
    {
        if (page.Doc is not { } blocks || !Assert(w > 0) || bottom <= top) return;
        var layout = Laid(page.Id, blocks, w);
        var view = bottom - top;
        // far enough that every heading reaches the top: the outline's last entries jump there too (else the page's end
        // stops them short and the outline keeps the one before lit)
        var reach = Math.Max(Math.Max(0, layout.Total - view), layout.LastAnchor);
        _scroll = Math.Clamp(Finite(_scroll) ? _scroll : 0, 0, reach);
        _map.View = (x, top, w, view);
        _canvas.Clip(x, top, w, view);
        var shown = new SortedDictionary<int, (double Top, double Bottom)>(); // each block's visible extent: it slides in as a row does
        foreach (var line in layout.Lines.Bounded(MaxDocLines))
        {
            var y = top + line.Y - _scroll;
            if (y + line.H < top || y > bottom) continue;
            DocPiece(line, x, y, blocks);
            var (from, to) = (Math.Max(top, y), Math.Min(bottom, y + line.H));
            shown[line.Block] = shown.TryGetValue(line.Block, out var had) ? (Math.Min(had.Top, from), Math.Max(had.Bottom, to)) : (from, to);
        }

        foreach (var (_, (from, to)) in shown.Bounded(MaxItems))
            if (_map.Bands.Count < MaxItems && to > from)
                _map.Bands.Add((from, to - from));
        _canvas.Unclip();
        if (reach <= 0) return;
        var thumb = Math.Max(S(24), view * view / (reach + view));
        _canvas.Fill(x + w - S(6), top, S(2), view, _style["line"]);
        _canvas.Fill(x + w - S(6), top + (view - thumb) * _scroll / reach, S(2), thumb, _style["soft"]);
    }

    private void DocPiece(DocLine line, double x, double y, List<DocText.Block> blocks)
    {
        if (!Finite(y)) return;
        if (line.Line)
        {
            _canvas.Fill(x + line.X, y, line.W, 1, _style["line"]);
            return;
        }

        if (line.Framed)
        {
            _canvas.Fill(x + line.X, y, line.W, line.H, Rgba.Black(0.3));
            _canvas.Outline(x + line.X, y, line.W, line.H, Accent with { A = 0.7 });
            return;
        }

        if (line.Fill)
        {
            _canvas.Fill(x + line.X, y, line.W, line.H, Rgba.Black(0.35));
            if (Index(line.Block, blocks.Count) && blocks[line.Block].Kind == DocText.Kind.Code) CopyCap(line, x, y, blocks[line.Block]);
            return;
        }

        if (line.Font is { } font) Marked(x + line.X, y, line.H, font, line.Text, line.Color);
    }

    // A line whose `code` spans (DocText's marks) show in the title colour
    private void Marked(double x, double y, double h, CairoFont font, string text, Rgba? color)
    {
        if (!NotNull(text)) return;
        if (text.IndexOf(DocText.CodeOpen, StringComparison.Ordinal) < 0)
        {
            _canvas.Text(x, y, h, font, text, color);
            return;
        }

        var code = false;
        foreach (var part in text.Split(CodeMarks).Bounded(256))
        {
            if (part.Length > 0)
            {
                _canvas.Text(x, y, h, font, part, code ? Title : color);
                x += _canvas.TextWidth(font, part);
            }

            code = !code;
        }
    }

    // A word wider than the column (a path) in pieces that fit, cut after a '/' where one fits; code marks go around each
    private IEnumerable<(string Word, bool Own)> Pieces(string word, CairoFont font, double width)
    {
        var plain = DocText.Strip(word);
        if (_canvas.TextWidth(font, plain) <= width)
        {
            yield return (word, false);
            yield break;
        }

        var code = word.Contains(DocText.CodeOpen, StringComparison.Ordinal);
        for (var i = 0; i < 16 && plain.Length > 0; i++)
        {
            var at = _canvas.TextWidth(font, plain) <= width ? plain.Length : _canvas.Cut(font, plain, width);
            yield return (code ? DocText.CodeOpen + plain[..at] + DocText.CodeClose : plain[..at], true);
            plain = plain[at..];
        }
    }

    // Words to lines of width, measured without the code marks; a span cut by a line break is closed and opened again
    private List<string> DocWrap(string text, CairoFont font, double width)
    {
        List<string> lines = [];
        if (!NotNull(text) || !Assert(width > 0)) return lines;
        var (line, inCode) = (new System.Text.StringBuilder(), false);
        var lineWidth = 0.0;
        var space = _canvas.TextWidth(font, " ");
        foreach (var whole in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Bounded(1 << 14))
        {
            foreach (var (word, own) in Pieces(whole, font, width).Bounded(16))
            {
                var w = _canvas.TextWidth(font, DocText.Strip(word));
                if (own && line.Length > 0) lineWidth = width; // a piece of a cut word starts its own line
                if (line.Length > 0 && lineWidth + space + w > width)
                {
                    if (inCode) _ = line.Append(DocText.CodeClose);
                    lines.Add(line.ToString());
                    (line, lineWidth) = (new System.Text.StringBuilder(inCode ? DocText.CodeOpen.ToString() : ""), 0);
                }

                if (lineWidth > 0) (line, lineWidth) = (line.Append(' '), lineWidth + space);
                (line, lineWidth) = (line.Append(word), lineWidth + w);
                inCode ^= word.Count(c => c is DocText.CodeOpen or DocText.CodeClose) % 2 == 1;
            }
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    // "Copy" at a code block's corner; the block copied to the clipboard on a click
    private void CopyCap(DocLine band, double x, double y, DocText.Block block)
    {
        var copied = _copied.Block == band.Block && Environment.TickCount64 - _copied.At < CopiedSeconds * 1000;
        var cap = T(copied ? "docs-copied" : "docs-copy");
        var cw = _canvas.TextWidth(_fonts.Small, cap);
        _canvas.Text(x + band.X + band.W - cw - S(10), y + S(4), S(18), _fonts.Small, cap, copied ? _style["good"] : Faint);
        var (index, text) = (band.Block, string.Join('\n', block.Lines ?? []));
        _map.Hits.Add(new Hit(x + band.X, y, band.W, band.H, (_, _) =>
        {
            capi.Input.ClipboardText = text;
            (_copied, _dirty) = ((index, Environment.TickCount64), true);
        }, Glow: SoftGlow, Group: ListGroup));
    }

    // Laid out anew only for another page, width or font size
    private DocLayout Laid(string page, List<DocText.Block> blocks, double width)
    {
        if (_doc is { } known && known.Page == page && Math.Abs(known.Width - width) < 0.5 && known.Epoch == _fonts.Epoch) return known;
        var (lines, anchors) = (new List<DocLine>(), new Dictionary<int, double>());
        var (pad, y) = (S(Pad), S(6));
        var inner = width - 2 * pad;
        for (var i = 0; i < Math.Min(blocks.Count, DocText.MaxBlocks) && lines.Count < MaxDocLines; i++)
        {
            var block = blocks[i];
            if (block.Kind is DocText.Kind.Heading or DocText.Kind.Subheading) anchors[i] = Math.Max(0, y - S(4));
            y = DocBlock(block, i, (pad, inner), y, lines);
        }

        // the deepest heading once per layout, not per compose (each scroll tick recomposes)
        var last = anchors.Count > 0 ? anchors.Values.Max() : 0;
        _doc = new DocLayout(page, width, _fonts.Epoch, lines, y + S(24), anchors, last);
        return _doc;
    }

    // One block's lines from y; returns where the next block starts
    private double DocBlock(DocText.Block block, int index, (double Pad, double Inner) room, double y, List<DocLine> lines)
    {
        var (pad, inner) = room;
        if (!Assert(inner > 0)) return y;
        switch (block.Kind)
        {
            case DocText.Kind.Title:
                return y; // the panel's title shows it
            case DocText.Kind.Heading:
                y += S(14);
                y = Words(block.Text.ToUpperInvariant(), (pad, inner), y, lines, index, _fonts.Big, Title);
                lines.Add(new DocLine(y + S(2), 1, pad, "", null, null, index, inner, Line: true));
                return y + S(12);
            case DocText.Kind.Subheading:
                y += S(8);
                return Words(block.Text, (pad, inner), y, lines, index, _fonts.Header, Title) + S(4);
            case DocText.Kind.Bullet or DocText.Kind.Numbered:
                var nested = block.Level is > 0 and < 8 && block.Kind == DocText.Kind.Bullet;
                var indent = S(18) * (nested ? 2 : 1);
                var dot = nested ? "–" : "•";
                var mark = block.Kind == DocText.Kind.Bullet ? dot : block.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
                lines.Add(new DocLine(y, _fonts.TextRow, pad + indent - S(16), mark, _fonts.Text, Accent, index));
                return Words(block.Text, (pad + indent, inner - indent), y, lines, index, _fonts.Text, null) + S(3);
            case DocText.Kind.Code:
                return Code(block, index, (pad, inner), y, lines);
            case DocText.Kind.Table:
                return Table(block, index, (pad, inner), y, lines);
            case DocText.Kind.Flow:
                return Flow(block, index, (pad, inner), y, lines);
            case DocText.Kind.Rule:
                lines.Add(new DocLine(y + S(8), 1, pad, "", null, null, index, inner, Line: true));
                return y + S(18);
            default:
                return Words(block.Text, (pad, inner), y, lines, index, _fonts.Text, Soft) + S(9);
        }
    }

    private double Words(string text, (double X, double W) column, double y, List<DocLine> lines, int index, CairoFont font, Rgba? color)
    {
        if (!NotNull(text)) return y;
        var row = font == _fonts.Header ? _fonts.HeaderRow : _fonts.TextRow;
        if (font == _fonts.Big) row = S(32);
        foreach (var line in DocWrap(text, font, column.W).Bounded(MaxDocLines))
        {
            lines.Add(new DocLine(y, row, column.X, line, font, color, index));
            y += row;
        }

        return y;
    }

    // A dark band with the lines in monospace; lines wider than the band are cut at its edge
    private double Code(DocText.Block block, int index, (double Pad, double Inner) room, double y, List<DocLine> lines)
    {
        var code = block.Lines ?? [];
        if (!Assert(room.Inner > 0)) return y;
        var (inset, row) = (S(12), _fonts.MonoRow);
        List<string> shown = []; // a line too wide for the block goes on below, indented: nothing is cut off
        foreach (var line in code.Bounded(4096)) shown.AddRange(CodeRows(line, room.Inner - 2 * inset));
        var height = shown.Count * row + 2 * S(10) + S(14);
        lines.Add(new DocLine(y, height, room.Pad, "", null, null, index, room.Inner, Fill: true));
        var at = y + S(10) + S(14);
        foreach (var line in shown.Bounded(8192))
        {
            lines.Add(new DocLine(at, row, room.Pad + inset, line, _fonts.Mono, null, index));
            at += row;
        }

        return y + height + S(10);
    }

    // One code line in rows that fit: broken after a space, comma or slash where one is near, the rest indented deeper
    private List<string> CodeRows(string line, double width)
    {
        List<string> rows = [];
        if (!Assert(width > 0) || _canvas.TextWidth(_fonts.Mono, line) <= width) return [line];
        var indent = new string(' ', line.Length - line.TrimStart().Length + 4);
        var start = 0; // where the next row begins in the line; rows after the first are indented
        for (var i = 0; i < 32 && start < line.Length; i++)
        {
            var lead = i == 0 ? "" : indent;
            var rest = lead + line[start..].TrimStart();
            var skipped = line.Length - start - (rest.Length - lead.Length);
            if (_canvas.TextWidth(_fonts.Mono, rest) <= width)
            {
                rows.Add(rest);
                break;
            }

            var fit = Math.Max(lead.Length + 1, _canvas.Cut(_fonts.Mono, rest, width));
            var cut = rest.LastIndexOfAny([' ', ',', '/'], Math.Min(fit, rest.Length) - 1);
            var at = cut > fit / 2 ? cut + 1 : fit;
            rows.Add(rest[..at].TrimEnd());
            start += skipped + at - lead.Length;
        }

        return rows;
    }

    // Columns as wide as their longest text allows (each at least a sixth), cells wrapped, the head on a band
    private double Table(DocText.Block block, int index, (double Pad, double Inner) room, double y, List<DocLine> lines)
    {
        var rows = block.Rows ?? [];
        if (rows.Length == 0 || !Assert(room.Inner > 0)) return y;
        var count = rows.Max(r => r.Length);
        var longest = Enumerable.Range(0, count).Select(c => Math.Max(4.0, rows.Max(r => c < r.Length ? r[c].Length : 0))).ToArray();
        var share = longest.Select(l => Math.Max(1.0 / 6, Math.Min(0.6, l / longest.Sum()))).ToArray();
        var widths = share.Select(s => room.Inner * s / share.Sum()).ToArray();
        for (var r = 0; r < Math.Min(rows.Length, 512); r++)
        {
            var font = r == 0 ? _fonts.Header : _fonts.Text;
            Rgba? color = null;
            if (r == 0) color = Faint; // the head row
            var cells = rows[r].Select((text, c) => DocWrap(text, font, widths[c] - S(12))).ToArray();
            var height = Math.Max(1, cells.Max(c => c.Count)) * _fonts.TextRow + S(8);
            if (r == 0) lines.Add(new DocLine(y, height, room.Pad, "", null, null, index, room.Inner, Fill: true));
            var x = room.Pad;
            for (var c = 0; c < Math.Min(cells.Length, DocText.MaxCells); c++)
            {
                var at = y + S(4);
                foreach (var text in cells[c].Bounded(64))
                {
                    lines.Add(new DocLine(at, _fonts.TextRow, x + S(6), text, font, color, index));
                    at += _fonts.TextRow;
                }

                x += widths[c];
            }

            y += height;
            lines.Add(new DocLine(y, 1, room.Pad, "", null, null, index, room.Inner, Line: true));
        }

        return y + S(12);
    }

    // A diagram: one step per line, numbered boxes of one width read top to bottom, an arrow down between them
    private const int MaxFlowSteps = 32;

    private double Flow(DocText.Block block, int index, (double Pad, double Inner) room, double y, List<DocLine> lines)
    {
        if (block.Lines is not { } rows || !Assert(room.Inner > 0)) return y;
        var steps = new List<string>();
        foreach (var row in rows.Bounded(64))
            foreach (var part in row.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Bounded(16))
                if (part is not ("v" or "↓")) steps.Add(DocText.Strip(DocText.Plain(part)));
        var (font, inset, number) = (_fonts.Text, S(12), S(28));
        var (height, gap) = (_fonts.TextRow + 2 * S(8), S(26));
        var widest = 0.0;
        foreach (var step in steps.Bounded(steps.Count)) widest = Math.Max(widest, _canvas.TextWidth(font, step));
        var w = Math.Min(room.Inner, widest + number + 2 * inset);
        y += S(4);
        for (var i = 0; i < Math.Min(steps.Count, MaxFlowSteps); i++)
        {
            if (i > 0)
            {
                lines.Add(new DocLine(y, gap, room.Pad + w / 2 - S(6), "↓", _fonts.Header, Accent, index));
                y += gap;
            }

            lines.Add(new DocLine(y, height, room.Pad, "", null, null, index, w, Framed: true));
            lines.Add(new DocLine(y, height, room.Pad + inset, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), _fonts.Header, Accent, index));
            lines.Add(new DocLine(y, height, room.Pad + inset + number, Fit(steps[i], w - number - 2 * inset, font), font, null, index));
            y += height;
        }

        return y + S(14);
    }

    // The outline beside the text: each heading, the one in view lit; a click scrolls to it
    private void DocOutline(OptionPage page, double x, double top, double w, double bottom)
    {
        if (page.Doc is not { } blocks || _doc is not { } layout || layout.Page != page.Id || !Assert(w > 0)) return;
        var pad = S(Pad);
        var y = top + pad;
        Spaced(x + pad, y, S(18), _fonts.Small, T("docs-contents").ToUpperInvariant(), S(2), Faint);
        y += S(30);
        var outline = DocText.Outline(blocks);
        var current = outline.FindLastIndex(o => layout.Anchors.TryGetValue(o.Block, out var at) && at <= _scroll + S(8));
        for (var i = 0; i < Math.Min(outline.Count, 256) && y + _fonts.TextRow < bottom - pad; i++)
        {
            var (block, text, sub) = outline[i];
            var indent = sub ? S(14) : 0;
            var font = sub ? _fonts.Text : _fonts.Header;
            var rows = Wrap(text, font, w - 2 * pad - indent); // a long heading wraps rather than being cut
            var h = Math.Min(rows.Count, 3) * _fonts.TextRow;
            var lit = i == current;
            if (lit) _canvas.Fill(x + pad - S(8), y + S(3), S(2), h - S(6), Accent);
            var idle = sub ? Faint : Soft;
            for (var r = 0; r < Math.Min(rows.Count, 3); r++)
                _canvas.Text(x + pad + indent, y + r * _fonts.TextRow, _fonts.TextRow, font, rows[r], lit ? Title : idle);
            var to = layout.Anchors.GetValueOrDefault(block);
            _map.Hits.Add(new Hit(x + pad - S(8), y, w - 2 * pad + S(8), h, (_, _) => (_scroll, _dirty) = (to, true),
                Glow: SoftGlow, Group: KeyGroup));
            y += h + S(2);
        }
    }
}

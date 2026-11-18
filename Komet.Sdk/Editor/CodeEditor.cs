using Komet.Hud;

namespace Komet.Sdk;

// The code editor: draws the shown document (gutter with changed-line marks, folds, selection, find matches, bracket
// pair, problem squiggles with their message at the line's end, caret) and takes its keys, typing and mouse
// (CodeEditor.Input). IntelliSense (completion, doc pane, signature help) is its part.
internal sealed partial class CodeEditor(IIdeHost host) : IIdeRegion
{
    private const int MaxRows = 128;
    private readonly List<Row> _rows = [];
    private HudUi? _ui;
    private double _sourceTop, _sourceBottom;
    private int _hoverLine = -1;
    private string _hoverWord = "";
    private double _hoverX, _hoverY, _caretX, _caretY;
    private bool _selecting;

    public CodeCompletion Completion { get; } = new();

    // The original's lines: a line that differs is marked in the gutter (null: no marks)
    public string[]? Baseline { get; set; }

    // The find widget's matches (start offsets, one length) and the current one
    public List<int> Matches { get; } = [];
    public int MatchLength { get; set; }
    public int Match { get; set; } = -1;

    private ShaderDocument? Document => host.Active;

    public void Draw(HudUi ui, double bottom, ShaderDocument document)
    {
        _rows.Clear();
        _ui = ui;
        _hoverLine = Math.Min(_hoverLine, document.Lines.Length);
        (_sourceTop, _sourceBottom) = (ui.Y, bottom);
        Source(ui, bottom, document);
        Completion.Draw(ui, _caretX, _caretY, bottom);
    }

    private void Source(HudUi ui, double bottom, ShaderDocument document)
    {
        var lines = document.Lines;
        var offsets = document.Offsets;
        _ = document.Folds; // the fold table before Hidden asks it
        var visible = Math.Max(1, (int)((bottom - ui.Y) / ui.F.BodyRow));
        var active = document.LineAt(document.Caret);
        if (document.Reveal)
        {
            document.FirstLine = Math.Clamp(document.FirstLine, Math.Max(0, active - visible + 1), active);
            document.Reveal = false;
        }

        document.FirstLine = Math.Clamp(document.FirstLine, 0, Math.Max(0, lines.Length - visible));
        (_caretX, _caretY) = (ui.X, ui.Y);
        var gutter = ui.TextW(ui.F.Mono, CodeText.Gutter(0));
        var drawn = 0;
        for (var index = document.FirstLine;
             index < Math.Min(lines.Length, ShaderPaths.MaxSource) && drawn < Math.Min(visible, MaxRows);
             index++)
        {
            if (document.Hidden(index)) continue;

            _rows.Add(new Row(index, offsets[index], lines[index], ui.X + gutter, ui.Y));
            DrawLine(ui, document, index, active, gutter);
            drawn++;
        }
    }

    private void DrawLine(HudUi ui, ShaderDocument document, int index, int active, double gutter)
    {
        var text = document.Lines[index];
        var offset = document.Offsets[index];
        if (index == active) ui.Fill(ui.X, ui.Y, ui.W, ui.F.BodyRow, HudUi.SelectedBack);

        Changed(ui, index, text);
        Found(ui, gutter, text, offset);
        Selection(ui, gutter, text, offset, document.SelectionStart, document.SelectionEnd);
        Brackets(ui, document, gutter, text, offset);
        if (document.Folds.ContainsKey(index))
        {
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Small, document.Collapsed(index) ? "+" : "−", HudUi.Link);
        }

        var marked = host.Diagnostics.At(index, out var issue);
        if (marked) ui.Fill(ui.X, ui.Y, Math.Max(1, ui.Px(1)) * 2, ui.F.BodyRow, issue.Error ? HudUi.Err : HudUi.Warn);

        Squiggles(ui, gutter, text, host.Diagnostics.On(index));

        var y = ui.Y;
        CodeText.Line(ui, text, CodeText.Gutter(index));
        if (host.Inactive(document, index))
        {
            ui.Fill(ui.X + gutter, y, ui.W - gutter, ui.F.BodyRow, host.InactiveTint); // a branch the preprocessor drops
        }
        if (marked) Message(ui, y, gutter + CodeText.Width(ui, text), issue, Own(host.Diagnostics.On(index)));

        if (index != active) return;

        var column = Math.Clamp(document.Caret - offset, 0, text.Length);
        var x = gutter + CodeText.Width(ui, text[..column]);
        (_caretX, _caretY) = (ui.X + x, ui.Y);
        if (x < ui.W && !document.ReadOnly)
        {
            ui.Fill(ui.X + x, ui.Y - ui.F.BodyRow, Math.Max(1, ui.Px(2)), ui.F.BodyRow, HudUi.Accent);
        }
    }

    private static void Selection(HudUi ui, double gutter, string row, int offset, int start, int end)
    {
        if (start == end || offset >= end || offset + row.Length < start) return;

        var left = Math.Clamp(start - offset, 0, row.Length);
        var right = Math.Clamp(end - offset, 0, row.Length);
        var x = gutter + CodeText.Width(ui, row[..left]);
        var width = CodeText.Width(ui, row[left..right]);
        if (end > offset + row.Length) width += ui.TextW(ui.F.Mono, " ");

        if (x < ui.W) ui.Fill(ui.X + x, ui.Y, Math.Min(width, ui.W - x), ui.F.BodyRow, HudUi.SelectionBack);
    }

    // The bracket at or before the caret and its pair
    private static void Brackets(HudUi ui, ShaderDocument document, double gutter, string line, int offset)
    {
        var pairs = document.Brackets;
        var caret = document.Caret;
        var position = caret > 0 && pairs.ContainsKey(caret - 1) ? caret - 1 : -1;
        if (pairs.ContainsKey(caret)) position = caret;

        if (position < 0 || !pairs.TryGetValue(position, out var pair)) return;

        Bracket(ui, position, gutter, line, offset);
        Bracket(ui, pair, gutter, line, offset);
    }

    private static void Bracket(HudUi ui, int position, double gutter, string line, int offset)
    {
        if (position < offset || position >= offset + line.Length) return;

        var column = position - offset;
        var x = gutter + CodeText.Width(ui, line[..column]);
        var width = CodeText.Width(ui, line[column..(column + 1)]);
        ui.Fill(ui.X + x, ui.Y, Math.Max(1, width), ui.F.BodyRow, HudUi.BracketBack);
    }

    // A line that differs from the original: a bar at the gutter's left
    private void Changed(HudUi ui, int index, string text)
    {
        if (Baseline is { } original && (index >= original.Length || original[index] != text))
        {
            ui.Fill(ui.X + ui.Px(2), ui.Y, Math.Max(1, ui.Px(2)), ui.F.BodyRow, HudUi.Link with { A = 0.7 });
        }
    }

    // The find widget's matches on this row, the current one stronger
    private void Found(HudUi ui, double gutter, string row, int offset)
    {
        if (MatchLength <= 0) return;

        for (var i = 0; i < Math.Min(Matches.Count, 4096); i++)
        {
            var start = Matches[i];
            if (start < offset || start > offset + row.Length) continue;

            var column = start - offset;
            var end = Math.Min(row.Length, column + MatchLength);
            var x = gutter + CodeText.Width(ui, row[..column]);
            var w = Math.Max(ui.Px(2), CodeText.Width(ui, row[column..end]));
            ui.Fill(ui.X + x, ui.Y, w, ui.F.BodyRow, StudioColors.Gold with { A = i == Match ? 0.55 : 0.25 });
        }
    }

    // A problem's message after the line's code, in its colour on a faint ground
    // The line's first own problem as a bubble behind the code, "(+n)" when the line has more of its own
    private static void Message(HudUi ui, double y, double end, Diagnostic issue, int own)
    {
        var x = ui.X + end + ui.Px(24);
        var room = ui.X + ui.W - x - ui.Px(8);
        if (room < ui.Px(60)) return;

        var color = issue.Error ? HudUi.Err : HudUi.Warn;
        var more = own > 1 ? "  (+" + (own - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : "";
        var text = ui.Fit(ui.F.Small, issue.Message + more, room - ui.Px(12));
        ui.Fill(x, y + ui.Px(2), ui.TextW(ui.F.Small, text) + ui.Px(12), ui.F.BodyRow - ui.Px(4), color with { A = 0.1 });
        ui.Text(x + ui.Px(6), y, ui.F.BodyRow, ui.F.Small, text, color);
    }

    private static int Own(ReadOnlySpan<Diagnostic> issues)
    {
        var own = 0;
        for (var i = 0; i < Math.Min(issues.Length, 128); i++)
        {
            own += issues[i].Inherited ? 0 : 1;
        }

        return own;
    }

    // One mark per problem under its place (the identifier at its column, else the line): a red or yellow wave for
    // the author's own, a thin grey line for a warning inherited from vanilla
    private static void Squiggles(HudUi ui, double gutter, string source, ReadOnlySpan<Diagnostic> issues)
    {
        var pixel = Math.Max(1, ui.Px(1));
        var baseline = ui.Y + ui.F.BodyRow - pixel * 3;
        for (var n = 0; n < Math.Min(issues.Length, 32); n++)
        {
            var issue = issues[n];
            var (from, to) = Span(source, issue.Column);
            var start = gutter + CodeText.Width(ui, source[..from]);
            var end = Math.Min(ui.W, gutter + CodeText.Width(ui, source[..to]));
            if (issue.Inherited)
            {
                ui.Fill(ui.X + start, baseline + pixel, Math.Max(0, end - start), pixel, HudUi.Faint with { A = 0.7 });
                continue;
            }

            var color = issue.Error ? HudUi.Err : HudUi.Warn;
            for (var i = 0; i < 2048 && start + i * pixel * 2 < end; i++)
            {
                var x = start + i * pixel * 2;
                ui.Fill(ui.X + x, baseline + i % 2 * pixel, Math.Min(pixel * 2, end - x), pixel, color);
            }
        }
    }

    // The characters a problem marks: the identifier at its column (1-based), the trimmed line when the column says
    // nothing (0, in the indent or past the code)
    private static (int From, int To) Span(string source, int column)
    {
        var lead = source.Length - source.TrimStart().Length;
        var last = source.TrimEnd().Length;
        var at = column - 1;
        if (at < lead || at >= last) return (lead, Math.Max(lead, last));

        var end = at;
        for (var i = 0; i < 256 && end < last && (char.IsLetterOrDigit(source[end]) || source[end] is '_' or '.'); i++)
        {
            end++;
        }

        return end > at ? (at, end) : (lead, last);
    }

    // The problem under the mouse as a tooltip, over everything
    public void DrawHover(HudUi ui, double bottom)
    {
        if (_hoverLine < 0) return;

        if (!host.Diagnostics.At(_hoverLine, out var issue))
        {
            var issues = host.Diagnostics.On(_hoverLine);
            if (issues.Length > 0)
            {
                Tip(ui, bottom, IdeText.L("inherited-from-vanilla", issues[0].Message), HudUi.Faint);
            }
            else if (host.Describe(_hoverWord) is { } about)
            {
                Tip(ui, bottom, about, HudUi.Soft);
            }

            return;
        }

        var width = Math.Min(ui.W * 0.55, ui.Px(440));
        var x = Math.Clamp(_hoverX + ui.Px(12), ui.X, ui.X + ui.W - width);
        var y = Math.Clamp(_hoverY + ui.Px(10), ui.Y, Math.Max(ui.Y, bottom - ui.F.SmallRow));
        ui.Panel(x, y, width, ui.F.SmallRow, HudUi.PopupBack);
        ui.Text(x + ui.Px(7), y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, issue.Message, width - ui.Px(14)),
            issue.Error ? HudUi.Err : HudUi.Warn);
    }

    private void Tip(HudUi ui, double bottom, string text, Rgba color)
    {
        var width = Math.Min(ui.W * 0.6, ui.TextW(ui.F.Small, text) + ui.Px(14));
        var x = Math.Clamp(_hoverX + ui.Px(12), ui.X, ui.X + ui.W - width);
        var y = Math.Clamp(_hoverY + ui.Px(10), ui.Y, Math.Max(ui.Y, bottom - ui.F.SmallRow));
        ui.Panel(x, y, width, ui.F.SmallRow, HudUi.PopupBack);
        ui.Text(x + ui.Px(7), y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, text, width - ui.Px(14)), color);
    }

    // The identifier around a column of a line
    private static string WordAt(string line, int column)
    {
        var start = Math.Clamp(column, 0, line.Length);
        var end = start;
        for (var i = 0; i < 128 && start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'); i++)
        {
            start--;
        }

        for (var i = 0; i < 128 && end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'); i++)
        {
            end++;
        }

        return line[start..end];
    }

    // The character column at x pixels into a row's code
    private int ColumnAt(Row row, double x)
    {
        if (_ui is not { } ui) return 0;

        var line = row.Text;
        var left = 0;
        var right = line.Length;
        for (var i = 0; i < 16 && left < right; i++)
        {
            var middle = (left + right) / 2;
            var before = CodeText.Width(ui, line[..middle]);
            var after = CodeText.Width(ui, line[..(middle + 1)]);
            if (x < (before + after) / 2)
            {
                right = middle;
            }
            else
            {
                left = middle + 1;
            }
        }

        return left;
    }

    // The drawn row at y; inside the source area a y past the rows takes the nearest
    private bool RowAt(double y, bool clamp, out Row row)
    {
        row = default;
        if (_rows.Count == 0 || _ui is not { } ui) return false;

        var index = (int)Math.Floor((y - _rows[0].Y) / ui.F.BodyRow);
        if (!clamp && (index < 0 || index >= _rows.Count)) return false;

        row = _rows[Math.Clamp(index, 0, _rows.Count - 1)];
        return true;
    }

    private readonly record struct Row(int Line, int Offset, string Text, double TextX, double Y);
}

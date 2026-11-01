using System.Text.RegularExpressions;

namespace Komet.Core;

// The in-game docs' Markdown, the subset the reader draws: a title (#), headings (## ###), paragraphs, bullets (- and
// two-space nested), numbered items, fenced code, ```flow diagrams, tables, rules. Inline **bold**, `code` and [links](url) are read as
// their text. Anything else is a paragraph.
internal static class DocText
{
    public const int MaxBlocks = 4096, MaxCells = 8, MaxLines = 1 << 16;

    // A `code` span inside running text, kept marked so the reader can colour it
    public const char CodeOpen = '\u0001', CodeClose = '\u0002';

    public enum Kind
    {
        Title,
        Heading,
        Subheading,
        Paragraph,
        Bullet,
        Numbered,
        Code,
        Table,
        Rule,
        Flow // a ```flow block: lines of boxes joined by ->, a line "v" an arrow down
    }

    // One block; Level: a bullet's depth (0, 1) or a numbered item's number; Rows: a table's cells, the header first
    public sealed record Block(Kind Kind, string Text, int Level = 0, string[]? Lines = null, string[][]? Rows = null);

    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex Number = new(@"^(\d{1,3})\.\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    public static List<Block> Parse(string markdown)
    {
        List<Block> blocks = [];
        if (!NotNull(markdown)) return blocks;
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();
        var i = 0;
        for (var guard = 0; guard < MaxLines && i < lines.Length && blocks.Count < MaxBlocks; guard++) i = Step(lines, i, blocks, paragraph);
        Flush(blocks, paragraph);
        return blocks;
    }

    // The block that starts at line i; returns the line after it
    private static int Step(string[] lines, int i, List<Block> blocks, List<string> paragraph)
    {
        var line = lines[i];
        var trimmed = line.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            Flush(blocks, paragraph);
            return Fence(lines, i, blocks);
        }

        if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsSeparator(lines[i + 1]))
        {
            Flush(blocks, paragraph);
            return Table(lines, i, blocks);
        }

        if (trimmed.Length == 0) Flush(blocks, paragraph);
        else if (Single(line, trimmed) is { } block)
        {
            Flush(blocks, paragraph);
            blocks.Add(block);
        }
        else if (blocks.Count > 0 && paragraph.Count == 0 && blocks[^1].Kind is Kind.Bullet or Kind.Numbered && line.StartsWith("  ", StringComparison.Ordinal))
            blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + Plain(trimmed) }; // a list item's next line
        else paragraph.Add(trimmed);
        return i + 1;
    }

    // A line that is a block of its own, or null
    private static Block? Single(string line, string trimmed)
    {
        if (trimmed.StartsWith("### ", StringComparison.Ordinal)) return new Block(Kind.Subheading, Strip(Plain(trimmed[4..])));
        if (trimmed.StartsWith("## ", StringComparison.Ordinal)) return new Block(Kind.Heading, Strip(Plain(trimmed[3..])));
        if (trimmed.StartsWith("# ", StringComparison.Ordinal)) return new Block(Kind.Title, Strip(Plain(trimmed[2..])));
        if (trimmed is "---" or "***") return new Block(Kind.Rule, "");
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            return new Block(Kind.Bullet, Plain(trimmed[2..]), line.StartsWith("  ", StringComparison.Ordinal) ? 1 : 0);
        var number = Number.Match(trimmed);
        return number.Success ? new Block(Kind.Numbered, Plain(number.Groups[2].Value), int.Parse(number.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    private static void Flush(List<Block> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0) return;
        blocks.Add(new Block(Kind.Paragraph, Plain(string.Join(' ', paragraph))));
        paragraph.Clear();
    }

    // A fenced block up to its closing fence; returns the line after it
    private static int Fence(string[] lines, int start, List<Block> blocks)
    {
        var code = lines.Skip(start + 1).TakeWhile(l => !l.Trim().StartsWith("```", StringComparison.Ordinal)).Take(4096)
            .Select(l => l.TrimEnd().Replace("\t", "    ", StringComparison.Ordinal)).ToList();
        if (lines[start].Trim()[3..].Trim() == "flow")
        {
            blocks.Add(new Block(Kind.Flow, "", Lines: [.. code.Select(l => l.Trim()).Where(l => l.Length > 0)]));
            return start + code.Count + 2;
        }

        var indent = code.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        blocks.Add(new Block(Kind.Code, string.Join('\n', code), Lines: [.. code.Select(l => l.Length >= indent ? l[indent..] : l)]));
        return start + code.Count + 2; // the fences
    }

    private static int Table(string[] lines, int start, List<Block> blocks)
    {
        var body = lines.Skip(start + 2).TakeWhile(l => l.Trim().StartsWith('|')).Take(512).ToList();
        List<string[]> rows = [Cells(lines[start]), .. body.Select(Cells)];
        var width = rows.Max(r => r.Length);
        blocks.Add(new Block(Kind.Table, "", Rows: [.. rows.Select(r => r.Concat(Enumerable.Repeat("", width - r.Length)).ToArray())]));
        return start + 2 + body.Count;
    }

    private static string[] Cells(string line)
    {
        var cells = line.Trim().Trim('|').Split('|').Select(c => Plain(c.Trim())).ToArray();
        return [.. cells.Take(MaxCells)];
    }

    private static bool IsSeparator(string line) =>
        line.Trim().StartsWith('|') && line.Replace("|", "", StringComparison.Ordinal).Trim().Trim('-', ':', ' ').Length == 0;

    // **bold**, `code` and [text](url) as their text
    internal static string Plain(string text)
    {
        if (!NotNull(text)) return "";
        return Inline.Replace(text, Kept);
    }

    private static string Kept(Match m)
    {
        if (m.Groups[1].Success) return m.Groups[1].Value;
        return m.Groups[2].Success ? CodeOpen + m.Groups[2].Value + CodeClose : m.Groups[3].Value;
    }

    // The text without code marks (headings, the outline, measuring)
    public static string Strip(string text) =>
        NotNull(text) ? text.Replace(CodeOpen.ToString(), "", StringComparison.Ordinal).Replace(CodeClose.ToString(), "", StringComparison.Ordinal) : "";

    // The headings a reader can jump to: index of the block and its text
    public static List<(int Block, string Text, bool Sub)> Outline(List<Block> blocks)
    {
        List<(int, string, bool)> outline = [];
        if (!NotNull(blocks)) return outline;
        for (var i = 0; i < Math.Min(blocks.Count, MaxBlocks); i++)
            if (blocks[i].Kind is Kind.Heading or Kind.Subheading)
                outline.Add((i, blocks[i].Text, blocks[i].Kind == Kind.Subheading));
        return outline;
    }
}

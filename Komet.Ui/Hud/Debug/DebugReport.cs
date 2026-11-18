using System.Text;

namespace Komet.Hud;

// A protocol as data: head lines, then numbered sections with a title and their lines. The Protocol tab draws it, Text
// writes the file and clipboard format ("[n] TITLE ====" heads, a blank line before each).
internal sealed class DebugReport
{
    public const int MaxWidth = 100, MaxSections = 1024, MaxLines = 1 << 16;
    private readonly List<string> _head = [];
    private readonly List<Part> _sections = [];
    private int _lines;
    private string? _text;

    public static DebugReport Empty { get; } = new();

    public IReadOnlyList<string> Head => _head;
    public IReadOnlyList<Part> Sections => _sections;

    // Head, section heads with their blank line, section lines: the rows Text writes
    public int Lines => _lines;

    public void AddHead(string line)
    {
        foreach (var part in Split(line).Bounded(MaxLines))
        {
            if (_head.Count < MaxLines)
            {
                _head.Add(part);
                _lines++;
            }
        }

        _text = null;
    }

    public void Begin(int number, string title)
    {
        if (!Assert(_sections.Count < MaxSections)) return;

        _sections.Add(new Part(number, title, []));
        (_lines, _text) = (_lines + 2, null);
    }

    // Lines before any section go to the head
    public void Add(string line)
    {
        if (_sections.Count == 0)
        {
            AddHead(line);
            return;
        }

        var lines = _sections[^1].Lines;
        foreach (var part in Split(line).Bounded(MaxLines))
        {
            if (lines.Count < MaxLines)
            {
                lines.Add(part);
                _lines++;
            }
        }

        _text = null;
    }

    // Every line and title through the filter (Anonymize), in place
    public DebugReport Map(System.Func<string, string> filter)
    {
        for (var i = 0; i < Math.Min(_head.Count, MaxLines); i++)
        {
            _head[i] = filter(_head[i]);
        }

        for (var s = 0; s < Math.Min(_sections.Count, MaxSections); s++)
        {
            var (number, title, lines) = _sections[s];
            _sections[s] = new Part(number, filter(title), lines);
            for (var i = 0; i < Math.Min(lines.Count, MaxLines); i++)
            {
                lines[i] = filter(lines[i]);
            }
        }

        _text = null;
        return this;
    }

    public static string Heading(Part section)
    {
        var title = $"[{section.Number}] {section.Title.ToUpperInvariant()} ";
        return title + new string('=', Math.Max(4, MaxWidth - title.Length));
    }

    public string Text()
    {
        if (_text is { } done) return done;

        var text = new StringBuilder();
        foreach (var line in _head.Bounded(MaxLines))
        {
            _ = text.Append(line).Append('\n');
        }

        foreach (var section in _sections.Bounded(MaxSections))
        {
            _ = text.Append('\n').Append(Heading(section)).Append('\n');
            foreach (var line in section.Lines.Bounded(MaxLines))
            {
                _ = text.Append(line).Append('\n');
            }
        }

        return _text = text.ToString();
    }

    private static string[] Split(string line) =>
        line.Contains('\n', StringComparison.Ordinal) ? line.Split('\n') : [line];

    public readonly record struct Part(int Number, string Title, List<string> Lines);
}

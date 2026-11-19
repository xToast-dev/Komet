namespace Komet.Sdk;

// One open file: its text on disk and the draft, caret and selection, undo history, and the line, bracket and fold
// tables derived from the draft (rebuilt when it changes). No UI.
internal sealed class ShaderDocument
{
    private const int MaxSource = ShaderPaths.MaxSource, HistoryLimit = 64;
    private readonly HashSet<int> _collapsed = [];

    private readonly List<Edit> _undo = [], _redo = [];
    private Dictionary<int, int> _brackets = [], _folds = [];
    private string[] _lines = [""];
    private string? _linesOf, _bracketsOf;
    private int[] _offsets = [0];

    public ShaderDocument(string path, string disk, string text, bool readOnly, int caret = 0, int firstLine = 0)
    {
        (Path, Disk, Text, ReadOnly) = (path, disk, text, readOnly);
        Caret = Anchor = Math.Clamp(caret, 0, text.Length);
        FirstLine = Math.Max(0, firstLine);
    }

    public string Path { get; }
    public string Disk { get; set; }
    public string Text { get; private set; }
    public bool ReadOnly { get; }
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    public int FirstLine { get; set; }

    // Bumped on every text change: views and checks compare it instead of the text
    public int Version { get; private set; }

    // The caret moved: the view scrolls it into sight once
    public bool Reveal { get; set; }

    public bool Dirty => !ReferenceEquals(Text, Disk) && Text != Disk;
    public string Name => System.IO.Path.GetFileName(Path);
    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);
    public bool HasSelection => Anchor != Caret;
    public string Selected => Text[SelectionStart..SelectionEnd];

    public string[] Lines
    {
        get
        {
            EnsureLines();
            return _lines;
        }
    }

    public int[] Offsets
    {
        get
        {
            EnsureLines();
            return _offsets;
        }
    }

    public Dictionary<int, int> Brackets
    {
        get
        {
            EnsureBrackets();
            return _brackets;
        }
    }

    public Dictionary<int, int> Folds
    {
        get
        {
            EnsureBrackets();
            return _folds;
        }
    }

    public void Move(int at, bool select)
    {
        Caret = Math.Clamp(at, 0, Text.Length);
        if (!select) Anchor = Caret;

        Reveal = true;
    }

    public void Select(int anchor, int caret)
    {
        Move(anchor, false);
        Move(caret, true);
    }

    // Replaces a range and puts the caret after it; false for a read-only file, an empty edit or a draft too large
    public bool Replace(int start, int end, string text)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        if (ReadOnly || text.Length > MaxSource - Text.Length + end - start || (start == end && text.Length == 0))
        {
            return false;
        }

        Remember(_undo);
        _redo.Clear();
        SetText(Text.Remove(start, end - start).Insert(start, text));
        Move(start + text.Length, false);
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);

    public bool Redo() => Restore(_redo, _undo);

    public int LineAt(int offset)
    {
        EnsureLines();
        var found = Array.BinarySearch(_offsets, Math.Clamp(offset, 0, Text.Length));
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    public int LineOffset(int line)
    {
        EnsureLines();
        return _offsets[Math.Clamp(line, 0, _offsets.Length - 1)];
    }

    public int LineStart(int at) => at <= 0 ? 0 : Text.LastIndexOf('\n', Math.Min(at, Text.Length) - 1) + 1;

    public int LineEnd(int at) =>
        Text.IndexOf('\n', Math.Clamp(at, 0, Text.Length)) is var end and >= 0 ? end : Text.Length;

    public static bool IsWord(char ch) => char.IsAsciiLetterOrDigit(ch) || ch == '_';

    public int WordStart()
    {
        var start = Caret;
        for (var i = 0; i < MaxSource && start > 0 && IsWord(Text[start - 1]); i++)
        {
            start--;
        }

        return start;
    }

    // Folding: a collapsed block hides the lines after its first up to its last
    public bool Collapsed(int line) => _collapsed.Contains(line);

    public void ToggleFold(int line)
    {
        if (!Folds.TryGetValue(line, out var end) || _collapsed.Remove(line)) return;

        _ = _collapsed.Add(line);
        var caretLine = LineAt(Caret);
        if (caretLine > line && caretLine <= end) Move(LineOffset(line), false);

        FirstLine = Math.Min(FirstLine, line);
    }

    public bool Hidden(int line)
    {
        if (_collapsed.Count == 0) return false;

        var folds = Folds;
        foreach (var start in _collapsed.Bounded(MaxSource))
        {
            if (folds.TryGetValue(start, out var end) && line > start && line <= end) return true;
        }

        return false;
    }

    private void SetText(string text)
    {
        Text = text;
        Version++;
    }

    private void Remember(List<Edit> history)
    {
        if (history.Count == HistoryLimit) history.RemoveAt(0);

        history.Add(new Edit(Text, Caret, Anchor));
    }

    private bool Restore(List<Edit> from, List<Edit> to)
    {
        if (from.Count == 0 || ReadOnly) return false;

        Remember(to);
        var edit = from[^1];
        from.RemoveAt(from.Count - 1);
        SetText(edit.Text);
        Move(edit.Anchor, false);
        Move(edit.Caret, true);
        return true;
    }

    private void EnsureLines()
    {
        if (ReferenceEquals(_linesOf, Text)) return;

        _linesOf = Text;
        _lines = Text.Split('\n');
        _offsets = new int[_lines.Length];
        var offset = 0;
        for (var i = 0; i < Math.Min(_lines.Length, MaxSource + 1); i++)
        {
            _offsets[i] = offset;
            offset += _lines[i].Length + 1;
        }
    }

    private void EnsureBrackets()
    {
        if (ReferenceEquals(_bracketsOf, Text)) return;

        _bracketsOf = Text;
        _brackets = ShaderBracketMatcher.Build(Text);
        _folds = ShaderCodeFolding.Build(Text);
        _ = _collapsed.RemoveWhere(line => !_folds.ContainsKey(line));
    }

    private readonly record struct Edit(string Text, int Caret, int Anchor);
}

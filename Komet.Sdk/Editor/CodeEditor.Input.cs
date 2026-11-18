using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The editor's input: caret keys and editing, clipboard and history shortcuts, typing with auto pairs and completion,
// mouse selection, fold toggles, Ctrl+click to a definition or include, and its context menu entries
internal sealed partial class CodeEditor
{
    // A key for the editor; true: taken
    public bool Key(KeyEvent args, GlKeys key)
    {
        if (Document is not { } document) return false;

        if (Completion.Open && args is { CtrlPressed: false, ShiftPressed: false } && Completion.Key(document, key)) return true;

        if (key == GlKeys.F2)
        {
            host.Run("rename-symbol");
            return true;
        }

        if (args.CtrlPressed) return Shortcut(document, key, args.ShiftPressed);

        if (!document.ReadOnly || key is GlKeys.Left or GlKeys.Right or GlKeys.Up or GlKeys.Down or GlKeys.Home or
                GlKeys.End or GlKeys.PageUp or GlKeys.PageDown)
        {
            EditKey(document, key, args.ShiftPressed);
        }
        else
        {
            host.Status.Set(L("readonly-typing")); // typing on a read-only file says why nothing happens
        }

        return true;
    }

    private bool Shortcut(ShaderDocument document, GlKeys key, bool shift)
    {
        switch (key)
        {
            case GlKeys.A: document.Select(0, document.Text.Length); break;
            case GlKeys.C: Copy(document); break;
            case GlKeys.X: Cut(document); break;
            case GlKeys.V: Insert(host.Api.Input.ClipboardText ?? ""); break;
            case GlKeys.Slash: ToggleLineComments(document); break;
            case GlKeys.Z when !shift: _ = document.Undo(); break;
            case GlKeys.Z or GlKeys.Y: _ = document.Redo(); break;
            case GlKeys.Home: document.Move(0, shift); break;
            case GlKeys.End: document.Move(document.Text.Length, shift); break;
            case GlKeys.Space or GlKeys.Enter: Completion.Suggest(document, true); break;
            default: return false;
        }

        return true;
    }

    private void Copy(ShaderDocument document)
    {
        if (document.HasSelection) host.Api.Input.ClipboardText = document.Selected;
    }

    private void Cut(ShaderDocument document)
    {
        if (document is { HasSelection: true, ReadOnly: false })
        {
            host.Api.Input.ClipboardText = document.Selected;
            _ = document.Replace(document.SelectionStart, document.SelectionEnd, "");
        }
    }

    private void EditKey(ShaderDocument document, GlKeys key, bool shift)
    {
        Completion.Close();
        var (caret, anchor) = (document.Caret, document.Anchor);
        var start = document.LineStart(caret);
        var end = document.LineEnd(caret);
        var page = Math.Max(1, _rows.Count - 1);
        switch (key)
        {
            case GlKeys.Left: document.Move(!shift && caret != anchor ? Math.Min(anchor, caret) : caret - 1, shift); break;
            case GlKeys.Right: document.Move(!shift && caret != anchor ? Math.Max(anchor, caret) : caret + 1, shift); break;
            case GlKeys.Home: document.Move(start, shift); break;
            case GlKeys.End: document.Move(end, shift); break;
            case GlKeys.Up when start > 0: document.Move(Math.Min(start - 1, document.LineStart(start - 1) + caret - start), shift); break;
            case GlKeys.Down when end < document.Text.Length: document.Move(Math.Min(document.LineEnd(end + 1), end + 1 + caret - start), shift); break;
            case GlKeys.PageUp: MoveLines(document, -page, shift); break;
            case GlKeys.PageDown: MoveLines(document, page, shift); break;
            case GlKeys.BackSpace: Delete(document, -1); break;
            case GlKeys.Delete: Delete(document, 1); break;
            case GlKeys.Tab: Insert("    "); break;
            case GlKeys.Enter: NewLine(document, start); break;
        }
    }

    private static void MoveLines(ShaderDocument document, int lines, bool shift)
    {
        var line = document.LineAt(document.Caret);
        var column = document.Caret - document.LineOffset(line);
        var target = Math.Clamp(line + lines, 0, document.Lines.Length - 1);
        document.Move(Math.Min(document.LineOffset(target) + column, document.LineEnd(document.LineOffset(target))),
            shift);
        document.FirstLine = Math.Max(0, document.FirstLine + lines);
    }

    private static void Delete(ShaderDocument document, int direction)
    {
        var other = document.HasSelection
            ? document.Anchor
            : Math.Clamp(document.Caret + direction, 0, document.Text.Length);
        _ = document.Replace(Math.Min(other, document.Caret), Math.Max(other, document.Caret), "");
    }

    // A new line keeps the current one's indent
    private void NewLine(ShaderDocument document, int start)
    {
        var indent = start;
        for (var i = start; i < Math.Min(document.Caret, ShaderPaths.MaxSource) && document.Text[i] is ' ' or '\t'; i++)
        {
            indent++;
        }

        Insert("\n" + document.Text[start..indent]);
    }

    // Typed or pasted text at the caret: one bracket or quote opens its pair (or steps over the closing one)
    public void Insert(string text)
    {
        if (Document is { ReadOnly: true })
        {
            host.Status.Set(L("readonly-typing"));
            return;
        }

        if (Document is not { } document || text.Length > ShaderPaths.MaxSource) return;

        if (ShaderAutoPairs.ForInput(document.Text, document.Anchor, document.Caret, text) is { } pair &&
            pair.Text.Length - (pair.End - pair.Start) <= ShaderPaths.MaxSource - document.Text.Length)
        {
            if (!pair.Skip) _ = document.Replace(pair.Start, pair.End, pair.Text);

            document.Move(pair.Caret, false);
        }
        else
        {
            _ = document.Replace(document.SelectionStart, document.SelectionEnd, text.ReplaceLineEndings("\n"));
        }

        Completion.Suggest(document);
        Completion.Track(document);
    }

    // Ctrl+/: comments the selected lines out, or in when all of them are comments
    private static void ToggleLineComments(ShaderDocument document)
    {
        if (document.ReadOnly) return;

        var first = document.LineStart(document.SelectionStart);
        var lastOffset = document.SelectionEnd;
        var last = document.LineEnd(lastOffset);
        if (lastOffset > first && lastOffset == document.LineStart(lastOffset) && document.HasSelection)
        {
            last = document.LineEnd(lastOffset - 1);
        }

        var lines = document.Text[first..last].Split('\n');
        var uncomment = false;
        for (var i = 0; i < Math.Min(lines.Length, ShaderPaths.MaxSource); i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            uncomment = lines[i].AsSpan().TrimStart().StartsWith("//", StringComparison.Ordinal);
            if (!uncomment) break;
        }

        for (var i = 0; i < Math.Min(lines.Length, ShaderPaths.MaxSource); i++)
        {
            lines[i] = ToggleLine(lines[i], uncomment);
        }

        _ = document.Replace(first, last, string.Join('\n', lines));
    }

    private static string ToggleLine(string line, bool uncomment)
    {
        var indent = line.Length - line.TrimStart().Length;
        if (uncomment && line.AsSpan(indent).StartsWith("//", StringComparison.Ordinal))
        {
            var remove = indent + 2;
            if (remove < line.Length && line[remove] == ' ') remove++;

            return line.Remove(indent, remove - indent);
        }

        return uncomment || string.IsNullOrWhiteSpace(line) ? line : line.Insert(indent, "// ");
    }

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        _selecting = false;
        if (Document is not { } document || button != EnumMouseButton.Left) return false;

        if (Completion.Click(document, x, y)) return true;

        var inside = _ui is { } ui && x >= ui.X && x < ui.X + ui.W && y >= _sourceTop && y < _sourceBottom;
        if (!inside || !RowAt(y, true, out var row)) return false;

        if (x < row.TextX && document.Folds.ContainsKey(row.Line))
        {
            document.ToggleFold(row.Line);
            return true;
        }

        if (keys.Ctrl && GoToDefinition(document, row, ColumnAt(row, x - row.TextX))) return true;

        Completion.Close();
        _selecting = true;
        document.Move(row.Offset + ColumnAt(row, x - row.TextX), keys.Shift);
        Completion.Track(document);
        return true;
    }

    public void Drag(double x, double y)
    {
        if (_selecting && Document is { } document && RowAt(y, true, out var row))
        {
            document.Move(row.Offset + ColumnAt(row, x - row.TextX), true);
        }
    }

    public void Up() => _selecting = false;

    public void Hover(double x, double y)
    {
        (_hoverX, _hoverY) = (x, y);
        _hoverLine = RowAt(y, false, out var row) && x >= row.TextX ? row.Line : -1;
        _hoverWord = _hoverLine >= 0 ? WordAt(row.Text, ColumnAt(row, x - row.TextX)) : "";
    }

    public void Leave()
    {
        _hoverLine = -1;
        _selecting = false;
    }

    public bool Wheel(double x, int lines)
    {
        if (Document is { } document) document.FirstLine = Scrolled(document, lines);

        Completion.Close();
        return true;
    }

    // The first line after a scroll of shown lines: a folded block counts as nothing (it used to eat the turns)
    internal static int Scrolled(ShaderDocument document, int lines)
    {
        var (at, step, count) = (document.FirstLine, Math.Sign(lines), document.Lines.Length);
        var left = Math.Abs(lines);
        var next = at + step;
        for (var walked = 0; walked < ShaderPaths.MaxSource && left > 0 && next >= 0 && next < count; walked++)
        {
            if (!document.Hidden(next)) (at, left) = (next, left - 1);

            next += step;
        }

        return at;
    }

    public bool Context(double x, double y, ContextMenu menu)
    {
        if (Document is not { } document || !RowAt(y, false, out var row)) return false;

        if (!document.HasSelection) document.Move(row.Offset + ColumnAt(row, x - row.TextX), false);

        menu.Add(L("copy") + "    Strg C", () => Copy(document));
        if (!document.ReadOnly)
        {
            menu.Add(L("cut") + "    Strg X", () => Cut(document));
            menu.Add(L("paste") + "    Strg V", () => Insert(host.Api.Input.ClipboardText ?? ""));
            menu.Add(L("undo") + "    Strg Z", () => _ = document.Undo());
            menu.Add(L("completion") + "    Strg Leertaste", () => Completion.Suggest(document, true));
        }

        menu.Add(L("definition") + "    F12", GoToDefinition);
        if (!document.ReadOnly) menu.Add(L("rename-symbol") + "    F2", () => host.Run("rename-symbol"));
        menu.Add(L("find-references") + "    Strg Umschalt F", () => host.Run("search-word"));
        menu.Add(L("select-all") + "    Strg A", () => document.Select(0, document.Text.Length));
        return true;
    }

    // F12: the definition of the symbol at the caret, or the file an #include line names
    private void GoToDefinition()
    {
        if (Document is not { } document) return;

        var line = document.LineAt(document.Caret);
        var text = document.Lines[line];
        var column = Math.Clamp(document.Caret - document.Offsets[line], 0, Math.Max(0, text.Length - 1));
        if (!GoToDefinition(document, new Row(line, document.Offsets[line], text, 0, 0), column))
        {
            host.Status.Set(L("definition-not-found"));
        }
    }

    private bool GoToDefinition(ShaderDocument document, Row row, int column)
    {
        if (ShaderPaths.IncludeName(row.Text) is { } include &&
            ShaderPaths.IncludePath(include, document.Path, host.Root) is { } path)
        {
            host.Open(path);
            return true;
        }

        if (!SymbolAt(row.Text, column, out var symbol)) return false;

        if (row.Text.TrimStart().StartsWith("uniform ", StringComparison.Ordinal) &&
            row.Text.Contains(" " + symbol, StringComparison.Ordinal) && host.JumpToEngine(symbol))
        {
            return true; // on its declaration: where the game sets it
        }

        if (!ShaderSymbolNavigator.TryFindDefinition(document.Text, symbol, out var offset))
        {
            return InIncludes(document, symbol);
        }

        document.Move(offset, false);
        document.FirstLine = document.LineAt(offset);
        return true;
    }

    // A definition in the file's includes (and theirs, breadth first, at most MaxIncludes files): opens it there
    private bool InIncludes(ShaderDocument document, string symbol)
    {
        const int maxIncludes = 16;
        var queue = new Queue<(string Text, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((document.Text, document.Path));
        for (var visited = 0; visited < maxIncludes && queue.Count > 0; visited++)
        {
            var (text, from) = queue.Dequeue();
            foreach (var line in text.Split('\n').Bounded(ShaderPaths.MaxSource))
            {
                if (ShaderPaths.IncludeName(line) is not { } name ||
                    ShaderPaths.IncludePath(name, from, host.Root) is not { } path || !seen.Add(path) ||
                    !StudioLink.Read(path, out var included, out _))
                {
                    continue;
                }

                if (ShaderSymbolNavigator.TryFindDefinition(included, symbol, out var at))
                {
                    host.Open(path, included.AsSpan(0, at).Count('\n'));
                    return true;
                }

                queue.Enqueue((included, path));
            }
        }

        return false;
    }

    // The identifier at the caret, for finding references
    public string? SymbolAtCaret()
    {
        if (Document is not { } document) return null;

        var line = document.LineAt(document.Caret);
        return SymbolAt(document.Lines[line], document.Caret - document.Offsets[line], out var symbol) ? symbol : null;
    }

    private static bool SymbolAt(string line, int column, out string symbol)
    {
        symbol = "";
        if (column > 0 && column == line.Length) column--;

        if (line.Length == 0 || column < 0 || column >= line.Length || !ShaderDocument.IsWord(line[column]))
        {
            return false;
        }

        var start = column;
        var end = column + 1;
        for (var i = 0; i < 128 && start > 0 && ShaderDocument.IsWord(line[start - 1]); i++)
        {
            start--;
        }

        for (var i = 0; i < 128 && end < line.Length && ShaderDocument.IsWord(line[end]); i++)
        {
            end++;
        }

        symbol = line[start..end];
        return true;
    }
}

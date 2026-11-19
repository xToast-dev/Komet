using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// IntelliSense: the words for the prefix before the caret (snippets first, then the file's symbols, the game's include
// symbols when the file includes them, GLSL), a doc pane for the chosen one, and the signature of the call the caret is
// in with the current argument in bold
internal sealed class CodeCompletion
{
    private const int MaxSuggestions = 12;
    private readonly List<ShaderSymbol> _items = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _selected, _prefix;
    private double _x, _y, _w, _row;

    public bool Open { get; private set; }

    // The function whose call the caret is in and the argument's index, null outside a call
    private (ShaderSymbol Symbol, int Argument)? Signature { get; set; }

    // A snippet was accepted and added lines on top: what it added, for the status
    public string Added { get; private set; } = "";

    public void Close() => Open = false;

    // What the last snippet added on top, once ("" when nothing)
    public string TakeAdded()
    {
        var added = Added;
        Added = "";
        return added;
    }

    public void Suggest(ShaderDocument document, bool explicitRequest = false)
    {
        Open = false;
        if (document.HasSelection || document.ReadOnly || (!ShaderPaths.IsStage(document.Path) &&
                                                           !document.Path.EndsWith(".ash", StringComparison.Ordinal)))
        {
            return;
        }

        var start = document.WordStart();
        if (start > 0 && document.Text[start - 1] == '#') start--;

        var line = document.Lines[document.LineAt(document.Caret)];
        if (line.Contains("//", StringComparison.Ordinal) || (start == document.Caret && !explicitRequest)) return;

        _prefix = document.Caret - start;
        var prefix = document.Text.AsSpan(start, _prefix);
        (_selected, Open) = (0, false);
        _items.Clear();
        _seen.Clear();
        Collect(document, prefix);
        Open = _items.Count > 0;
    }

    private void Collect(ShaderDocument document, ReadOnlySpan<char> prefix)
    {
        var typed = prefix.ToString();
        if (prefix.Length == 0 || prefix[0] != '#')
        {
            foreach (var snippet in ShaderDocs.Snippets.Bounded(16))
            {
                if (prefix.Length == 0 ||
                    snippet.Name.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    Array.Exists(snippet.Keys ?? [], k => k.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(snippet);
                }
            }
        }

        var fog = document.Text.Contains("#include " + ShaderDocs.FogInclude, StringComparison.Ordinal) ||
                  document.Text.Contains("#include \"" + ShaderDocs.FogInclude, StringComparison.Ordinal);
        var pool = Pool(document, fog);
        foreach (var contains in (ReadOnlySpan<bool>)[false, true])
        {
            for (var i = 0; i < Math.Min(pool.Count, ShaderDocs.MaxSymbols) && _items.Count < MaxSuggestions; i++)
            {
                var name = pool[i].Name.AsSpan();
                var starts = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if ((contains ? !starts && name.Contains(prefix, StringComparison.OrdinalIgnoreCase) : starts) &&
                    !name.SequenceEqual(prefix))
                {
                    Add(pool[i]);
                }
            }
        }
    }

    private static List<ShaderSymbol> Pool(ShaderDocument document, bool fog)
    {
        var pool = ShaderDocs.FileSymbols(document.Text);
        foreach (var symbol in ShaderDocs.Engine.Bounded(32))
        {
            if (fog || symbol.Source != ShaderDocs.FogInclude) pool.Add(symbol);
        }

        pool.AddRange(ShaderDocs.Glsl);
        pool.AddRange(ShaderDocs.Komet);
        pool.AddRange(ShaderDocs.Includes);
        return pool;
    }

    private void Add(ShaderSymbol symbol)
    {
        if (_items.Count < MaxSuggestions && _seen.Add(symbol.Name)) _items.Add(symbol);
    }

    // The signature help: the call around the caret, when its function is known
    public void Track(ShaderDocument document)
    {
        Signature = null;
        if (document.ReadOnly) return;

        var line = document.Lines[document.LineAt(document.Caret)];
        var column = Math.Clamp(document.Caret - document.LineOffset(document.LineAt(document.Caret)), 0, line.Length);
        var (depth, at, commas) = (0, -1, 0);
        for (var k = 1; k <= Math.Min(column, 512); k++)
        {
            var i = column - k;
            var c = line[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(' && depth-- == 0)
            {
                at = i;
                break;
            }
            else if (c == ',' && depth == 0)
            {
                commas++;
            }
        }

        if (at <= 0) return;

        var end = at;
        var start = end;
        for (var i = 0; i < 64 && start > 0 && ShaderDocument.IsWord(line[start - 1]); i++)
        {
            start--;
        }

        var name = line[start..end];
        var pool = Pool(document, true);
        var symbol = pool.Find(s => s.Kind == 'f' && s.Name == name);
        Signature = symbol is null ? null : (symbol, commas);
    }

    // The list under the caret's row, the chosen entry's doc beside it; the signature over the row
    public void Draw(HudUi ui, double x, double y, double bottom)
    {
        if (!Open && Signature is { } call) DrawSignature(ui, x, y, call.Symbol, call.Argument);

        if (!Open) return;

        var count = Math.Min(_items.Count, MaxSuggestions);
        (_row, _w) = (ui.F.BodyRow + ui.Px(2), Math.Min(ui.W * 0.45, ui.Px(300)));
        var docW = Math.Min(ui.Px(300), Math.Max(0, ui.X + ui.W - (x + _w) - ui.Px(4)));
        _x = Math.Clamp(x - ui.TextW(ui.F.Mono, "x") * _prefix, ui.X, Math.Max(ui.X, ui.X + ui.W - _w - docW));
        var h = count * _row + ui.Px(4);
        _y = y + ui.F.BodyRow + h > bottom ? Math.Max(ui.Y, y - h) : y + ui.F.BodyRow;
        ui.Panel(_x, _y, _w, h, HudUi.PopupBack);
        for (var i = 0; i < Math.Min(count, MaxSuggestions); i++)
        {
            Item(ui, _items[i], _y + ui.Px(2) + i * _row, i == _selected);
        }

        if (docW > ui.Px(120)) Doc(ui, _items[Math.Clamp(_selected, 0, count - 1)], _x + _w, _y, docW);
    }

    private void Item(HudUi ui, ShaderSymbol item, double top, bool selected)
    {
        if (selected) ui.Fill(_x, top, _w, _row, HudUi.SelectedBack);

        var (glyph, color) = Glyph(item.Kind);
        ui.Text(_x + ui.Px(8), top, _row, ui.F.Strong, glyph, color);
        var nameX = _x + ui.Px(26);
        var source = Source(item);
        var sourceW = ui.TextW(ui.F.Small, source);
        ui.Text(nameX, top, _row, item.Kind == 's' ? ui.F.Body : ui.F.Mono,
            ui.Fit(ui.F.Mono, item.Name, _w - ui.Px(40) - sourceW), selected ? HudUi.Link : HudUi.Soft);
        ui.TextRight(_x + _w - ui.Px(8), top, _row, ui.F.Small, source, HudUi.Faint);
    }

    private static string Source(ShaderSymbol item)
    {
        if (item.Source.Length > 0) return item.Source;

        return item.Kind is 'k' or 't' ? "" : "GLSL";
    }

    private static string From(ShaderSymbol item)
    {
        if (item.Kind != 's') return item.Source;

        if (item.Include is { } inc) return "Snippet · " + L("snip-adds") + " #include " + inc;

        return item.Declaration is { } decl ? "Snippet · " + L("snip-declares") + " " + decl : "Snippet";
    }

    private static void Doc(HudUi ui, ShaderSymbol item, double x, double y, double w)
    {
        var column = ui.Column(x + ui.Px(10), w - ui.Px(20), y + ui.Px(6));
        var lines = item.Kind == 's' ? item.Signature.Split('\n') : [item.Signature];
        var h = (lines.Length + 3) * ui.F.SmallRow + ui.Px(16);
        ui.Panel(x, y, w, h, HudUi.PopupBack);
        foreach (var line in lines.Bounded(16))
        {
            column.Text(column.X, column.Y, ui.F.SmallRow, ui.F.Mono, column.Fit(ui.F.Mono, line, column.W),
                HudUi.Link);
            column.Y += ui.F.SmallRow;
        }

        column.Wrap(item.Doc, HudUi.Soft, ui.F.Small);
        var from = From(item);
        column.Text(column.X, column.Y, ui.F.SmallRow, ui.F.Small, column.Fit(ui.F.Small, from, column.W), HudUi.Faint);
    }

    private static void DrawSignature(HudUi ui, double x, double y, ShaderSymbol symbol, int argument)
    {
        var text = symbol.Signature;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open) return;

        var parts = text[(open + 1)..close].Split(',');
        var w = Math.Min(ui.W, ui.TextW(ui.F.Mono, text) + ui.Px(20));
        var h = ui.F.SmallRow * 2 + ui.Px(8);
        var left = Math.Clamp(x - ui.Px(40), ui.X, Math.Max(ui.X, ui.X + ui.W - w));
        var top = Math.Max(ui.Y, y - h - ui.Px(2));
        ui.Panel(left, top, w, h, HudUi.PopupBack);
        var at = left + ui.Px(10);
        at = Run(ui, at, top, text[..(open + 1)], false);
        for (var i = 0; i < Math.Min(parts.Length, 16); i++)
        {
            at = Run(ui, at, top, (i > 0 ? "," : "") + parts[i], i == argument);
        }

        _ = Run(ui, at, top, ")", false);
        ui.Text(left + ui.Px(10), top + ui.F.SmallRow, ui.F.SmallRow, ui.F.Small,
            ui.Fit(ui.F.Small, symbol.Doc, w - ui.Px(20)), HudUi.Faint);
    }

    private static double Run(HudUi ui, double x, double y, string text, bool strong)
    {
        ui.Text(x, y + ui.Px(4), ui.F.SmallRow, ui.F.Mono, text, strong ? HudUi.Link : HudUi.Soft);
        if (strong)
        {
            ui.Fill(x, y + ui.Px(4) + ui.F.SmallRow - ui.Px(2), ui.TextW(ui.F.Mono, text), Math.Max(1, ui.Px(1)),
                HudUi.Link);
        }

        return x + ui.TextW(ui.F.Mono, text);
    }

    private static (string Glyph, Rgba Color) Glyph(char kind) =>
        kind switch
        {
            'f' => ("ƒ", StudioColors.Fn),
            't' => ("T", StudioColors.Type),
            'k' => ("K", StudioColors.Keyword),
            'u' => ("U", StudioColors.Gold),
            'd' => ("#", StudioColors.Pre),
            'v' => ("V", HudUi.Ok),
            _ => ("S", StudioColors.Str)
        };

    // A click on the list: true when it took it
    public bool Click(ShaderDocument document, double x, double y)
    {
        if (!Open || _row <= 0 || x < _x || x >= _x + _w || y < _y) return false;

        var index = (int)((y - _y - 2) / _row);
        if (index < 0 || index >= Math.Min(_items.Count, MaxSuggestions)) return false;

        Accept(document, _items[index]);
        return true;
    }

    public bool Key(ShaderDocument document, GlKeys key)
    {
        switch (key)
        {
            case GlKeys.Up:
                _selected = (_selected - 1 + _items.Count) % Math.Max(1, _items.Count);
                return true;
            case GlKeys.Down:
                _selected = (_selected + 1) % Math.Max(1, _items.Count);
                return true;
            case GlKeys.Tab or GlKeys.Enter:
                Accept(document, _items[Math.Clamp(_selected, 0, _items.Count - 1)]);
                return true;
            case GlKeys.Escape:
                Open = false;
                return true;
            default: return false;
        }
    }

    private void Accept(ShaderDocument document, ShaderSymbol item)
    {
        Open = false;
        Added = "";
        var start = document.Caret - _prefix;
        if (item.Kind == 's')
        {
            Snippet(document, item, start);
            return;
        }

        var function = item.Kind == 'f';
        _ = document.Replace(start, document.Caret, item.Name + (function ? "()" : ""));
        if (function)
        {
            document.Move(document.Caret - 1, false);
            Track(document);
        }
    }

    // A snippet at the caret in the line's indent; its #include or declaration added on top when missing
    private void Snippet(ShaderDocument document, ShaderSymbol item, int start)
    {
        var lineStart = document.LineStart(start);
        var indent = lineStart;
        for (var i = lineStart; i < Math.Min(start, ShaderPaths.MaxSource) && document.Text[i] is ' ' or '\t'; i++)
        {
            indent++;
        }

        var pad = document.Text[lineStart..indent];
        _ = document.Replace(start, document.Caret, item.Signature.Replace("\n", "\n" + pad, StringComparison.Ordinal));
        List<string> add = [];
        if (item.Include is { } inc && !document.Text.Contains("#include " + inc, StringComparison.Ordinal))
        {
            add.Add("#include " + inc);
        }

        if (item.Declaration is { } decl &&
            !document.Text.Contains(item.Declaration.TrimEnd(';'), StringComparison.Ordinal))
        {
            add.Add(decl);
        }

        if (add.Count == 0) return;

        var caret = document.Caret;
        var at = document.Text.StartsWith("#version", StringComparison.Ordinal) ? document.LineEnd(0) + 1 : 0;
        var text = string.Join('\n', add) + "\n";
        _ = document.Replace(at, at, text);
        document.Move(caret + text.Length, false);
        Added = string.Join(" · ", add);
    }
}

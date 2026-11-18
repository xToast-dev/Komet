using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Rename symbol (F2): a small prompt over the code takes the new name for the word at the caret; Enter renames it in
// the shown file as one undo step. When the symbol is defined in an editable include of the project, a toast offers to
// rename it there and in the other open project files that include it. Read-only (game) files are never touched.
internal sealed class RenameSymbol(ShaderIde host)
{
    private const int MaxIncludes = 16;
    private readonly TextField _name = new(128);
    private string _symbol = "";
    private Action? _focus;

    public bool Open { get; private set; }

    public void Begin()
    {
        if (host.Active is not { } document || host.Mode != EditorMode.Mine || host.Editor.SymbolAtCaret() is not { } symbol)
        {
            host.Toast(L("rename-no-symbol"));
            return;
        }

        if (document.ReadOnly)
        {
            host.Toast(L("readonly-typing"));
            return;
        }

        (_symbol, Open) = (symbol, true);
        _name.Set(symbol, true);
        host.Focus = StudioFocus.RenameSymbol;
    }

    public void Close()
    {
        Open = false;
        if (host.Focus == StudioFocus.RenameSymbol) host.Focus = StudioFocus.Editor;
    }

    public bool Key(GlKeys key, bool ctrl)
    {
        if (key is GlKeys.Enter or GlKeys.KeypadEnter)
        {
            Apply();
            return true;
        }

        _ = _name.Key(key, ctrl, host.Api);
        return true;
    }

    public void Type(char c) => _name.Type(c);

    private void Apply()
    {
        var name = _name.Text.Trim();
        if (name == _symbol)
        {
            Close();
            return;
        }

        if (!ShaderRename.IsValidName(name))
        {
            host.Toast(L("rename-invalid", name));
            return;
        }

        Close();
        if (host.Active is not { ReadOnly: false } document) return;

        var count = Rename(document, _symbol, name);
        if (DefiningInclude(document, _symbol) is { } include)
        {
            var (from, origin) = (_symbol, document);
            host.Toast(L("rename-done", count), L("rename-in-includes"), () => RenameShared(origin, include, from, name));
            return;
        }

        host.Toast(L("rename-done", count));
    }

    // The whole-word rename in one document as one undo step; the number of replaced occurrences
    private static int Rename(ShaderDocument document, string from, string to)
    {
        if (document.ReadOnly) return 0;

        var caret = document.Caret;
        var text = ShaderRename.ReplaceWord(document.Text, from, to, out var count);
        if (count == 0 || !document.Replace(0, document.Text.Length, text)) return 0;

        document.Move(Math.Min(caret, document.Text.Length), false);
        return count;
    }

    // The editable project include (breadth first through the includes) that defines the symbol; null: none
    private string? DefiningInclude(ShaderDocument document, string symbol)
    {
        var queue = new Queue<(string Text, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((document.Text, document.Path));
        for (var visited = 0; visited < MaxIncludes && queue.Count > 0; visited++)
        {
            var (text, from) = queue.Dequeue();
            foreach (var line in text.Split('\n').Bounded(ShaderPaths.MaxSource))
            {
                if (ShaderPaths.IncludeName(line) is not { } name ||
                    ShaderPaths.IncludePath(name, from, host.Root) is not { } found)
                {
                    continue;
                }

                var path = host.CopyOf(found) ?? found;
                if (!seen.Add(path) || !StudioLink.Read(path, out var included, out _)) continue;

                if (ShaderSymbolNavigator.TryFindDefinition(included, symbol, out _))
                {
                    return Editable(path) ? path : null;
                }

                queue.Enqueue((included, path));
            }
        }

        return null;
    }

    private static bool Editable(string path) => StudioLink.ProjectOf(path) is not null && StudioLink.CanWrite(path);

    // The include and every other open, editable project file that includes it
    private void RenameShared(ShaderDocument origin, string include, string from, string to)
    {
        host.OpenExact(include);
        var total = 0;
        var files = 0;
        foreach (var document in host.Tabs.ToArray().Bounded(ShaderIde.MaxTabs + 1))
        {
            if (document == origin || document.ReadOnly || !Editable(document.Path) ||
                !(StudioLink.PathEquals(document.Path, include) || Includes(document, include)))
            {
                continue;
            }

            var count = Rename(document, from, to);
            (total, files) = (total + count, files + (count > 0 ? 1 : 0));
        }

        host.Select(origin);
        host.Toast(L("rename-shared", total, files));
    }

    private bool Includes(ShaderDocument document, string include)
    {
        foreach (var line in document.Lines.Bounded(ShaderPaths.MaxSource))
        {
            if (ShaderPaths.IncludeName(line) is { } name && ShaderPaths.IncludePath(name, document.Path, host.Root) is
                    { } found && StudioLink.PathEquals(host.CopyOf(found) ?? found, include))
            {
                return true;
            }
        }

        return false;
    }

    public void Draw(HudUi ui, double x, double y, double w)
    {
        if (!Open) return;

        var row = ui.Px(30);
        var h = row + ui.Px(10);
        ui.Panel(x, y, w, h, HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, HudUi.Accent);
        host.Hits.Block(x, y, w, h);
        var label = L("rename-label", _symbol);
        var lw = Math.Min(w * 0.45, ui.TextW(ui.F.Small, label) + ui.Px(16));
        ui.Text(x + ui.Px(10), y + ui.Px(5), row, ui.F.Small, ui.Fit(ui.F.Small, label, lw - ui.Px(12)), HudUi.Faint);
        var fw = w - lw - ui.Px(10);
        _ = _name.Draw(ui, x + lw, y + ui.Px(5) + (row - ui.F.BodyRow - ui.Px(8)) / 2, fw, L("rename-placeholder"),
            host.Focus == StudioFocus.RenameSymbol);
        _focus ??= () => host.Focus = StudioFocus.RenameSymbol;
        host.Hits.Add(x + lw, y, fw, h, _focus);
    }
}

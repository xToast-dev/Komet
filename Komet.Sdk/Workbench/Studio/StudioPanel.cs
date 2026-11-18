using System.Globalization;
using Komet.Hud;

namespace Komet.Sdk;

// A list panel. The bottom one (Strg J, its top edge drags its height): Probleme of the project's files grouped by file
// (severity, message, the renderer it fails under, [Ln, Sp]), Ausgabe (the studio's log), Inspector. The docked one is
// the left column's Suche (in all project and game shader files, Strg Umschalt F, two letters at least) or Passes
// (StudioPanel.Passes), chosen by the activity strip; search and passes are only ever asked of the docked one.
internal sealed partial class StudioPanel(ShaderIde host, bool docked = false) : IIdeRegion
{
    private const int Problems = 0;
    private const int Output = 1, Search = 2;
    private const int MaxFiles = 256, MaxHits = 30, MaxSearchFiles = 1024;
    private const double HeadHeight = 34, RowHeight = 24;

    private readonly List<(string Path, Diagnostic[] Issues)> _problems = [];
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
    private readonly List<Line> _lines = [];
    private readonly IdeRows _rows = new();
    private readonly TextField _query = new(), _replace = new();
    private bool _replacing;
    private int _tab = docked ? 2 : 0, _first, _queryVersion = -1, _seenVersion = -1, _hitCount;
    private string _hitText = "", _countText = "";
    private ShaderDocument? _seen;
    private DragRegion? _split;
    private Action? _close, _focusSearch, _focusReplace, _replaceAll;
    private (int Tab, string Key, Action Click)[]? _tabClicks;

    // A drawn row: a file heading or a jumpable line (file, line, column)
    private readonly record struct Line(string Path, int Row, int Column, string Text, Diagnostic? Issue, int Count,
        bool Heading, int At);

    public bool Shown => host.Prefs.PanelShown;
    public int Errors { get; private set; }

    public void Toggle() => host.Prefs.PanelShown = !host.Prefs.PanelShown;


    // The host shows these without naming the tab constants (cs/coupled-types counts each access)
    public void ShowProblems() => Show(Problems);

    public void ShowPasses() => Show(Passes);

    public void Show(int tab)
    {
        (_tab, _first) = (tab, 0);
        if (docked)
        {
            host.ShowLeft(tab == Search ? LeftView.Search : LeftView.Passes);
        }
        else
        {
            host.Prefs.PanelShown = true;
        }
    }

    public void BeginSearch(string? text = null)
    {
        Show(Search);
        if (text is { Length: > 0 })
        {
            _query.Set(text);
        }
        else if (host.Active is { HasSelection: true } document && !document.Selected.Contains('\n', StringComparison.Ordinal))
        {
            _query.Set(document.Selected);
        }

        _query.SelectAll();
        (_replacing, host.Focus) = (false, StudioFocus.Search);
    }

    // The errors of a file, or of every file under a folder
    public int ErrorsIn(string path)
    {
        if (_errors.TryGetValue(path, out var count)) return count;

        var sum = 0;
        foreach (var (file, errors) in _errors.Bounded(MaxFiles * 2))
        {
            sum += StudioLink.Within(file, path) ? errors : 0;
        }

        return sum;
    }

    // The active project's files checked again (the open drafts as they are)
    public void Refresh()
    {
        _problems.Clear();
        _errors.Clear();
        Errors = 0;
        if (host.Project is { } project)
        {
            var folder = Path.Join(StudioLink.ProjectsFolder, project);
            foreach (var file in Files(folder).Bounded(MaxFiles))
            {
                Check(file);
            }
        }

        if (host.Current is { ReadOnly: false } current && StudioLink.ProjectOf(current.Path) != host.Project)
        {
            Check(current.Path);
        }

        _seenVersion = -1;
    }

    private static string[] Files(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*", ShaderPaths.Recursive(true)).Where(f => !StudioLink.Hidden(f)).Take(MaxFiles).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Check(string path)
    {
        var open = FindTab(path);
        string text;
        if (open is not null)
        {
            text = open.Text;
        }
        else if (!StudioLink.Read(path, out text, out _))
        {
            return;
        }

        var issues = host.Diagnostics.Classified(path, text).Where(i => !i.Inherited).ToArray(); // inherited: grey in the editor only
        var errors = ShaderDiagnostics.Errors(issues);
        _errors[path] = errors;
        Errors += errors;
        if (issues.Length > 0 && _problems.Count < MaxFiles) _problems.Add((path, issues));
    }

    private ShaderDocument? FindTab(string path) =>
        host.Tabs.FirstOrDefault(tab => StudioLink.PathEquals(tab.Path, path));

    // Per frame: the shown file's problems follow its edits
    public bool Poll()
    {
        var current = host.Current;
        if (current is null || (current == _seen && current.Version == _seenVersion)) return false;

        (_seen, _seenVersion) = (current, current.Version);
        if (current.ReadOnly) return false;

        var at = _problems.FindIndex(p => StudioLink.PathEquals(p.Path, current.Path));
        Errors -= _errors.GetValueOrDefault(current.Path);
        if (at >= 0) _problems.RemoveAt(at);

        Check(current.Path);
        return true;
    }

    public void Type(char c)
    {
        if (_tab == Passes)
        {
            _passQuery.Type(c);
            return;
        }

        if (_replacing)
        {
            _replace.Type(c);
            return;
        }

        _query.Type(c);
        _first = 0;
    }

    public bool Key(GlKeys key, bool ctrl)
    {
        if (_replacing && _tab != Passes)
        {
            if (key is GlKeys.Enter or GlKeys.KeypadEnter) ReplaceAll();
            else if (key == GlKeys.Up) _replacing = false;
            else _ = _replace.Key(key, ctrl, host.Api);

            return true;
        }

        if (key is GlKeys.Tab && _tab == Search)
        {
            _replacing = true;
            return true;
        }

        if (key is GlKeys.Enter or GlKeys.KeypadEnter or GlKeys.Down)
        {
            host.Focus = StudioFocus.Editor;
            return true;
        }

        _ = (_tab == Passes ? _passQuery : _query).Key(key, ctrl, host.Api);
        (_first, _passFirst) = (0, 0);
        return true;
    }

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        var index = _rows.At(x, y);
        if (index < 0 || index >= _lines.Count || button != EnumMouseButton.Left) return false;

        var line = _lines[index];
        host.OpenExact(line.Path, line.Row);
        if (host.Current is { } document && line.Column > 0)
        {
            document.Move(document.LineOffset(line.Row) + line.Column - 1, false);
        }

        host.Focus = StudioFocus.Editor;
        return false;
    }

    public bool Wheel(double x, int lines)
    {
        _passFirst = Math.Max(0, _passFirst + (_tab == Passes ? lines : 0));
        _first = Math.Max(0, _first + lines);
        return true;
    }

    private void Actions(HudUi ui)
    {
        _close ??= () => host.Prefs.PanelShown = false;
        _focusSearch ??= () => (_replacing, host.Focus) = (false, StudioFocus.Search);
        _focusReplace ??= () => (_replacing, host.Focus) = (true, StudioFocus.Search);
        _replaceAll ??= ReplaceAll;
        _tabClicks ??= [(Problems, "problems", () => Show(Problems)), (Output, "output", () => Show(Output)),
            (Inspector, "inspector-tab", ShowInspector)];
        _split ??= new DragRegion((_, y) => host.Prefs.Panel = Math.Clamp((host.Height - y) / Math.Max(0.1, ui.Px(1)),
            90, 2000));
    }

    private static string CountText(int count) => count == 0 ? "" : count.ToString(CultureInfo.InvariantCulture);
}

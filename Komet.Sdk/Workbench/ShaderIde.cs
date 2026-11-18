using System.Globalization;
using Komet.Options;
using Komet.Rendering;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Where keys and typing go
internal enum StudioFocus
{
    Editor,
    Tree,
    Filter,
    Rename,
    Find,
    Replace,
    Search,
    Values,
    RenameSymbol
}

// What the editor shows of a copy: the project's text, the game's original, or both side by side
internal enum EditorMode
{
    Mine,
    Original,
    Diff,
    Engine
}

// Komet Shader Studio, the developer screen's one tool, full screen: a top bar (search, Auto, Testen with the state),
// the Vintagestory folder as a tree, the editor with its tabs, the bottom panel (Probleme, Ausgabe, Suche) and the right
// column (Vorschau, Einstellungen, Spiel, Frame). It owns the open files and the build; files, projects, modules and
// builds go through StudioLink. The parts are this class's partial files.
internal sealed partial class ShaderIde : IDevTool, IIdeHost, IDisposable
{
    internal const int MaxTabs = 8;
    private const int MaxLog = 60;
    private const long BuildDelayMs = 1200, UndoMs = 30_000, SlowMs = 3000, FullMs = 30_000;
    private readonly StudioWatch _watch = new();
    private long _nextFull;

    // Counts the times the disk was read again: what was parsed from a project's files is good until it moves
    public int DiskVersion { get; private set; }

    private readonly ICoreClientAPI _capi;
    private readonly Action _gamePreview;
    private readonly List<ShaderDocument> _tabs = [];
    private readonly List<(string Time, string Text, bool Error)> _log = [];
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private ShaderDocument? _current, _original;
    private int _seenVersion = -1;
    private long _buildAt, _nextRefresh;
    private bool _busy, _unbuilt, _shown, _redraw;
    private (TrashToken Token, long At)? _undo;

    public ShaderIde(ICoreClientAPI capi, Action gamePreview)
    {
        (_capi, _gamePreview) = (capi, gamePreview);
        Link = new StudioLink(capi);
        Prefs = StudioPrefs.Load();

        Editor = new CodeEditor(this);
        Find = new FindWidget(this);
        Rename = new RenameSymbol(this);
        Diff = new SideDiff(this);
        Tree = new StudioTree(this);
        Panel = new StudioPanel(this);
        Left = new StudioPanel(this, true);
        Side = new StudioSide(this);
        Palette = new StudioPalette(this);
        Modal = new StudioModal(this);
        Side.Model.Changed += ModelChanged;
        ShaderDiagnostics.IsSet = (path, name) => Origin(path, name).Set; // one event: the tab, SETTINGS, the FRAME bar, badges and notices follow
        InitInput();
    }

    public string Id => "ide";
    public string Title => L("title");
    public double RefreshMs => 0;
    public bool Scrolls => false;
    public bool FullScreen => true;
    public bool Peeking { get; set; }
    public bool IsShown => _shown;

    public ICoreClientAPI Api => _capi;
    public IdeStatus Status { get; } = new();
    public ShaderDiagnostics Diagnostics { get; } = new();
    public StudioLink Link { get; }
    public StudioPrefs Prefs { get; }
    public CodeEditor Editor { get; }
    private FindWidget Find { get; }
    private RenameSymbol Rename { get; }
    private SideDiff Diff { get; }
    public StudioTree Tree { get; }
    public StudioPanel Panel { get; }
    public StudioPanel Left { get; }
    public StudioSide Side { get; }
    private StudioPalette Palette { get; }
    public StudioModal Modal { get; }
    private ContextMenu Context { get; } = new();
    public StudioHits Hits { get; } = new();
    public MenuControls? Controls { get; private set; }
    public StudioFocus Focus { get; set; } = StudioFocus.Editor;
    public EditorMode Mode { get; set; }
    public IReadOnlyList<ShaderDocument> Tabs => _tabs;
    public ShaderDocument? Current => _current;
    public IReadOnlyList<(string Time, string Text, bool Error)> Log => _log;
    public string Root => StudioLink.DataRoot;

    // The editor's document: the original in the Original view
    public ShaderDocument? Active => Mode switch
    {
        EditorMode.Original when _original is not null => _original,
        EditorMode.Engine when _engine is not null => _engine,
        _ => _current
    };

    // The project the screen works on: the open file's, else the last chosen
    public string? Project
    {
        get;
        private set
        {
            field = value;
            StudioLink.Editing(value);
        }
    }

    public bool Auto => Prefs.Auto;
    public void Skin(MenuControls controls) => Controls = controls;

    // The world ends: unsaved drafts go to their files (not built), the studio's state is kept
    public void Dispose()
    {
        foreach (var tab in _tabs.Bounded(MaxTabs + 1))
        {
            if (tab is { Dirty: true, ReadOnly: false } && StudioLink.Write(tab.Path, tab.Text)) tab.Disk = tab.Text;
        }

        Persist();
        Gone();
    }

    // The screen goes: nothing watches the folder, the side lets go of the game, the session's changes are undone
    private void Gone()
    {
        _watch.Dispose();
        StudioLink.Listening(false); // "Game runs: Vanilla" stays as chosen
        EndSession();
    }

    public void Shown(bool shown)
    {
        _shown = shown;
        Context.Close();
        Editor.Leave();
        Peeking = false;
        if (shown)
        {
            GameValues.Ensure(_capi); // the GAME VALUES list and a project's new declarations read them
            _watch.Start(StudioLink.ProjectsFolder);
            Opened();
            return;
        }

        Gone();
        Persist();
    }

    // The screen comes up: the last tabs once, the project, the tree at the active file
    private void Opened()
    {
        if (_tabs.Count == 0)
        {
            foreach (var path in Prefs.Tabs.Bounded(MaxTabs))
            {
                Open(path);
            }
        }

        if (Project is null && Prefs.Project.Length > 0 &&
            Directory.Exists(Path.Join(StudioLink.ProjectsFolder, Prefs.Project)))
        {
            Project = Prefs.Project;
        }

        Project ??= Array.Find(StudioLink.Projects(), p => p.Active)?.Name;
        Relist();
        Side.Refresh();
    }

    // The files changed: the tree lists them again, the panel's lists with it when asked
    private void Relist(bool panel = false)
    {
        Tree.Refresh();
        if (panel) RefreshPanel();
    }

    // The side panel's calls in one place each (cs/coupled-types counts every call site)
    private void RefreshPanel() => Panel.Refresh();

    private void ShowPasses() => Left.ShowPasses();

    private void Search(string? word) => Left.BeginSearch(word);

    private void Persist()
    {
        Prefs.Tabs = [.. _tabs.Select(t => t.Path).Take(MaxTabs)];
        Prefs.Project = Project ?? "";
        Prefs.Open = Tree.OpenFolders();
        Prefs.Save();
    }

    public void Run(string command) => RunCommand(command);

    // Per frame: the debounced build, toasts and the slow refreshes; allocation-free while nothing is due
    public bool Poll()
    {
        var now = Environment.TickCount64;
        SnapshotWatch();
        var taken = Status.Take();
        var redraw = _redraw || taken;
        _redraw = false;
        if (taken && Status.Text.Length > 0)
        {
            Toast(Status.Text); // the editor's messages: the studio has no status line
            Status.Set("");
        }

        if (Editor.Completion.Added.Length > 0) Toast(Editor.Completion.TakeAdded() + " " + L("added"));

        if (_current is { } document && document.Version != _seenVersion)
        {
            _seenVersion = document.Version;
            if (document is { Dirty: true, ReadOnly: false }) Changed(document.Path);

            Find.Refresh();
            redraw = true;
        }

        if (_buildAt > 0 && now >= _buildAt)
        {
            _buildAt = 0;
            BuildNow();
            redraw = true;
        }

        if (now >= _nextRefresh && _shown)
        {
            // the folders and every project file's problems are read again only after a change on disk (or every
            // FullMs for what the watcher does not see); it used to be every SlowMs, a hitch each time
            _nextRefresh = now + SlowMs;
            if (_watch.Take() || !_watch.Working || now >= _nextFull)
            {
                _nextFull = now + FullMs;
                DiskVersion++;
                Relist(true);
                redraw = true;
            }
        }

        redraw |= EnginePoll();
        var side = Side.Poll(now);
        var toast = ToastPoll(now);
        var panel = Panel.Poll();
        var test = TestPoll(now);
        var model = ModelPoll(now) || test;
        AutosavePoll(now);
        return redraw || side || toast || panel || model || BuildPoll(now);
    }

    public void Redraw() => _redraw = true;

    // A project file changed: Auto builds it soon, else the state says Ungetestet
    private void Changed(string path)
    {
        if (StudioLink.ProjectOf(path) is not { } project) return;

        _ = _changed.Add(path);
        Project = project;
        if (Prefs.Auto)
        {
            _buildAt = Environment.TickCount64 + BuildDelayMs;
        }
        else
        {
            _unbuilt = true;
        }
    }

    public void SetProject(string? project)
    {
        Project = project;
        Side.Refresh();
        Redraw();
    }

    public void AddLog(string text, bool error = false)
    {
        _log.Insert(0, (DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text, error));
        if (_log.Count > MaxLog) _log.RemoveAt(_log.Count - 1);

        Redraw();
    }

    // A deletion that Strg+Z undoes within 30 s
    public void Deleted(TrashToken token) => _undo = (token, Environment.TickCount64);

    public bool UndoDelete()
    {
        if (_undo is not { } undo || Environment.TickCount64 - undo.At > UndoMs) return false;

        _undo = null;
        Restore(undo.Token);
        return true;
    }

    public void Restore(TrashToken token)
    {
        if (Link.Restore(token) is not { } back)
        {
            Toast(L("exists", StudioLink.Name(token.Original)));
            return;
        }

        AddLog(StudioLink.Display(token.Trashed) + " → " + StudioLink.Display(back));
        Toast(L("restored", StudioLink.Name(back)));
        Tree.Reveal(back);
        if (!Directory.Exists(back)) Open(back);
    }

    // Hover in content pixels, for the parts' hover grounds
    public bool Hot(double x, double y, double w, double h) =>
        _mouseX >= x && _mouseX < x + w && _mouseY >= y && _mouseY < y + h;
}

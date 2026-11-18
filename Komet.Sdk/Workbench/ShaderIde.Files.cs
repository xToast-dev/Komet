using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The open files: tabs (eight, the oldest goes), the Original view's document, copying a game shader into the project,
// the build, the toast and the palette's commands
internal sealed partial class ShaderIde
{
    private long _projectsAt;
    private string _toastText = "", _toastAction = "";
    private Action? _toastRun;
    private long _toastUntil;

    // The projects, listed at most every half second
    public ShaderProject[] Projects
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _projectsAt > 500) (field, _projectsAt) = (StudioLink.Projects(), now);

            return field;
        }
    } = [];

    public void ProjectsChanged()
    {
        _projectsAt = 0;
        NoticesStale();
        Redraw();
    }

    public ShaderProject? ProjectInfo(string? name)
    {
        if (name is null) return null;

        foreach (var project in Projects.Bounded(StudioLink.MaxProjects))
        {
            if (project.Name == name) return project;
        }

        return null;
    }

    public void Open(string path, int line = -1) => OpenFile(CopyOf(path) ?? path, line);

    public void OpenExact(string path, int line = -1) => OpenFile(path, line);

    private void OpenFile(string path, int line)
    {
        Context.Close();
        var document = _tabs.Find(t => StudioLink.PathEquals(t.Path, path));
        if (document is null)
        {
            if (!StudioLink.Read(path, out var text, out var error))
            {
                Toast(error.Length > 0 ? error : StudioLink.Name(path));
                return;
            }

            document = new ShaderDocument(path, text, text, !StudioLink.CanWrite(path));
            _tabs.Add(document);
            if (_tabs.Count > MaxTabs)
            {
                _tabs.RemoveAt(_tabs.FindIndex(t => t != document && !t.Dirty) is var old and >= 0 ? old : 0);
            }
        }

        Select(document);
        if (line >= 0) document.Move(document.LineOffset(Math.Clamp(line, 0, document.Lines.Length - 1)), false);
    }

    public void Select(ShaderDocument document)
    {
        if (_current != document)
        {
            (_current, _original, _engine, Mode) = (document, null, null, EditorMode.Mine);
            Diff.Close();
            Editor.Completion.Close();
            Find.Refresh();
        }

        Focus = StudioFocus.Editor;
        Tree.Select(document.Path, true);
        if (StudioLink.ProjectOf(document.Path) is { } project && project != Project) SetProject(project);

        Redraw();
    }

    // A tab closes; its unsaved draft is written first when Komet may write it
    public void CloseTab(ShaderDocument document)
    {
        if (document is { Dirty: true, ReadOnly: false })
        {
            _ = StudioLink.Write(document.Path, document.Text);
            document.Disk = document.Text;
        }

        var at = _tabs.IndexOf(document);
        _ = _tabs.Remove(document);
        if (_current == document)
        {
            _current = null;
            if (_tabs.Count > 0) Select(_tabs[Math.Clamp(at - 1, 0, _tabs.Count - 1)]);
        }

        Redraw();
    }

    // A tab whose file moved or went: follows it, or closes
    public void Moved(string from, string? to)
    {
        var count = _tabs.Count;
        for (var k = 0; k < Math.Min(count, MaxTabs + 1); k++)
        {
            var i = count - 1 - k;
            var tab = _tabs[i];
            if (!StudioLink.Within(tab.Path, from)) continue;

            if (to is not null)
            {
                var path = StudioLink.PathEquals(tab.Path, from) ? to : Path.Join(to, Path.GetRelativePath(from, tab.Path));
                var moved = new ShaderDocument(path, tab.Disk, tab.Text, !StudioLink.CanWrite(path), tab.Caret);
                _tabs[i] = moved;
                _current = _current == tab ? moved : _current;
            }
            else
            {
                _tabs.RemoveAt(i);
                _current = _current == tab ? null : _current;
            }
        }

        _current ??= _tabs.Count > 0 ? _tabs[^1] : null;
        Redraw();
    }

    // Meins, Original or Vergleich of the shown copy
    public void SetMode(EditorMode mode)
    {
        Diff.Close();
        Mode = mode;

        if (mode == EditorMode.Engine)
        {
            ShowEngine();
            Redraw();
            return;
        }

        if (mode != EditorMode.Mine && _current is { } copy && StudioLink.GameFileOf(copy.Path) is { } game)
        {
            if (_original?.Path != game && StudioLink.Read(game, out var text, out _))
            {
                _original = new ShaderDocument(game, text, text, true);
            }

            if (mode == EditorMode.Diff && _original is { } original)
            {
                Diff.Show(L("original"), original.Text, L("mine"), copy.Text, [], live: true);
            }
        }

        Redraw();
    }

    // A game shader into the project; its copy opens (the existing one when there is)
    public void CopyIn(string gameFile)
    {
        if (Project is null)
        {
            Toast(L("no-project"));
            return;
        }

        if (StudioLink.CopyIn(gameFile, Project) is not { } copy)
        {
            Toast(L("write-refused"));
            return;
        }

        var game = _tabs.Find(t => StudioLink.PathEquals(t.Path, gameFile));
        if (game is not null) _ = _tabs.Remove(game);

        Toast(StudioLink.Name(gameFile) + " → " + Project);
        Changed(copy);
        Relist();
        Open(copy);
    }

    // The frame module that draws the shown file's program
    private ShaderModule? ModuleOf(ShaderDocument? document)
    {
        if (document is null) return null;

        var program = Path.GetFileNameWithoutExtension(StudioLink.GameFileOf(document.Path) ?? document.Path);
        return Array.Find(Side.Modules, module => Array.IndexOf(module.Programs, program) >= 0);
    }

    // Every vanilla shader and include (all domains) into the active pack as its own copies, laid out as a pack wants
    // them; copies the pack has already stay as they are. Then one build of everything.
    public void ExportVanilla()
    {
        if (Project is not { } project)
        {
            Toast(L("no-project"));
            return;
        }

        var (added, kept, failed) = (0, 0, 0);
        foreach (var path in ShaderAssets.Files().Bounded(ShaderAssets.FileLimit))
        {
            var had = StudioLink.CopyPath(project, path) is not null; // only an existing copy has a path
            if (StudioLink.CopyIn(path, project) is not { } copy)
            {
                failed++;
                continue;
            }

            if (had)
            {
                kept++;
            }
            else
            {
                added++;
                Changed(copy);
            }
        }

        var text = L("export-vanilla-done", added, project, kept);
        AddLog(failed > 0 ? text + " · " + L("export-vanilla-failed", failed) : text, failed > 0);
        Toast(text);
        Relist();
        Tree.Refresh();
    }

    public void Toast(string text, string action = "", Action? run = null)
    {
        (_toastText, _toastAction, _toastRun) = (text, action, run);
        _toastUntil = Environment.TickCount64 + (run is null ? 1800 : 6000);
        Redraw();
    }

    private bool ToastPoll(long now)
    {
        if (_toastUntil == 0 || now < _toastUntil) return false;

        (_toastUntil, _toastText, _toastRun) = (0, "", null);
        return true;
    }

    private void DrawToast(Hud.HudUi ui)
    {
        if (_toastText.Length == 0) return;

        var pad = ui.Px(16);
        var actionW = _toastAction.Length > 0 ? ui.TextW(ui.F.Small, Caps(_toastAction)) + ui.Px(24) : 0;
        var w = Math.Min(ui.W * 0.6, ui.TextW(ui.F.Body, _toastText) + 2 * pad + actionW);
        var h = ui.Px(36);
        var (x, y) = (ui.X + (ui.W - w) / 2, _height - h - ui.Px(28));
        ui.Panel(x, y, w, h, Hud.HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, Hud.HudUi.Divider);
        ui.Text(x + pad, y, h, ui.F.Body, ui.Fit(ui.F.Body, _toastText, w - 2 * pad - actionW));
        if (_toastRun is { } run && actionW > 0)
        {
            var ax = x + w - pad - actionW + ui.Px(12);
            _ = ui.Spaced(ax, y, h, ui.F.Small, Caps(_toastAction), ui.Px(1), Hud.HudUi.Link);
            Hits.Add(ax - ui.Px(6), y, actionW, h, () =>
            {
                (_toastUntil, _toastText, _toastRun) = (0, "", null);
                run();
            });
        }
    }

    private bool _buildQueued;

    // Writes the project's changed drafts, builds them, records the result. The build reloads the game's programs, so it
    // runs from the main-thread queue between two frames: a reload in the middle of a frame left the rest of that frame
    // drawing with deleted programs (one wrong picture and an OpenGL error per change)
    private void BuildNow()
    {
        _buildAt = 0;
        if (_buildQueued) return;

        _buildQueued = true;
        _capi.Event.EnqueueMainThreadTask(() =>
        {
            _buildQueued = false;
            BuildQueued();
        }, "komet-workbench-build");
    }

    private void BuildQueued()
    {
        if (Project is not { } project)
        {
            Toast(L("no-project"));
            return;
        }

        foreach (var tab in _tabs.Bounded(MaxTabs + 1))
        {
            if (tab is { Dirty: true, ReadOnly: false } && StudioLink.Write(tab.Path, tab.Text))
            {
                tab.Disk = tab.Text;
                _ = _changed.Add(tab.Path);
            }
        }

        string[] files = [.. _changed.Where(f => StudioLink.ProjectOf(f) == project).Take(StudioLink.MaxEntries)];
        _changed.Clear();
        (_busy, _unbuilt) = (true, false);
        AddLog(project + ": " + L("building"));
        var report = Link.Build(project, files);
        _busy = false;
        _pending = report.Pending;
        NoticesStale();
        Relist();
        Problems([.. files, .. report.Files], report.Issues);
        Built(project, report, files.Length);
    }

    // A build's problems reach the editor's marks and the panel's list together
    private void Problems(IReadOnlyList<string> files, BuildIssue[] issues)
    {
        Diagnostics.Built(files, issues);
        RefreshPanel();
    }

    private void Built(string project, BuildReport report, int files)
    {
        if (report.Skipped) AddLog(project + ": " + L("build-skipped"), true);

        var errors = report.Issues.Count(i => i.Error);
        if (!report.Ok || errors > 0)
        {
            AddLog(L("build-failed", project, Math.Max(1, errors)), true);
            Panel.ShowProblems();
            Toast(L("toast-failed"));
            return;
        }

        if (files > 0) AddLog(L("history-saved", files));

        AddLog(project + ": " + L("build-ok"));
        Toast(L("toast-updated"));
    }

    // A project switched on or off for the game (persisted)
    public void SetProjectOn(string name, bool on, bool tell = true)
    {
        if (!Link.SetActive(name, on)) Toast(ShaderProjects.Error);

        ProjectsChanged();
        Relist();
        if (tell)
        {
            AddLog(name + ": " + (on ? L("on-lower") : L("off-lower")));
            Toast(name + " " + (on ? L("on-lower") : L("off-lower")));
        }
    }

    private long _buildPollAt;
    private bool _pending;

    // Every half second while a build has pending states (Vulkan ports, passes): their results come in
    private bool BuildPoll(long now)
    {
        if (!_pending || Project is not { } project || now < _buildPollAt) return false;

        _buildPollAt = now + 500;
        if (Link.Poll(project) is not { } report)
        {
            _pending = false;
            return false;
        }

        _pending = report.Pending;
        Problems(report.Files, report.Issues);
        return true;
    }

    private void ToggleAuto()
    {
        Prefs.Auto = !Prefs.Auto;
        if (Prefs.Auto && _unbuilt) BuildNow();

        Toast(Prefs.Auto ? L("auto-on") : L("auto-off"));
    }
}

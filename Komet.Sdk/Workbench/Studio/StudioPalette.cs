using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Strg P: one field for everything. Commands first, with their keys, so the shortcuts are found; then projects (to
// work on, to switch on or off), the frame's modules, the files of the projects and the game's shaders.
internal sealed class StudioPalette(ShaderIde host)
{
    private const int MaxShown = 14, MaxItems = 2048;
    private readonly List<(string Text, string Key, Action Run)> _items = [];
    private readonly TextField _query = new();
    private readonly List<int> _shown = [];
    private int _selected, _version = -1;

    public bool Open { get; private set; }

    public void Show()
    {
        (Open, _selected, _version) = (true, 0, -1);
        _query.Set("");
        Collect();
    }

    private void Close()
    {
        Open = false;
        _items.Clear();
    }

    private void Collect()
    {
        _items.Clear();
        var ctrl = L("key-ctrl");
        var command = L("command");
        Add(L("test"), ctrl + " S", "test");
        Add(L("run-find"), ctrl + " F", "find");
        Add(L("run-replace"), ctrl + " H", "replace");
        Add(L("run-search"), ctrl + " " + L("key-shift") + " F", "search");
        Add(L("run-panel"), ctrl + " J", "panel");
        Add(L("run-suggest"), ctrl + " " + L("key-space"), "suggest");
        Add(L("run-undo-delete"), ctrl + " Z", "undo-delete");
        Add(L("run-close-tab"), ctrl + " W", "close-tab");
        Add(host.Auto ? L("run-auto-off") : L("run-auto-on"), command, "auto");
        Add(L("run-restore-good"), command, "restore-good");
        Add(L("run-history"), command, "history");
        if (host.Current is { } current && StudioLink.GameFileOf(current.Path) is not null)
        {
            Add(L("run-compare"), command, "compare");
        }

        if (host.Project is { } active)
        {
            Add(L("run-export", active), command,
                "export");
        }

        Add(L("run-game"), command, "game");
        Add(L("run-export-vanilla"), command, "export-vanilla");
        Projects();
        Modules();
        Files();
    }

    private void Add(string text, string key, string command) => _items.Add((text, key, () => host.Run(command)));

    private void Projects()
    {
        var word = L("project-word");
        foreach (var project in host.Projects.Bounded(StudioLink.MaxProjects))
        {
            var (name, on) = (project.Name, project.Active);
            _items.Add((name + " " + (on ? L("switch-off-lower") : L("switch-on-lower")), word,
                () => host.SetProjectOn(name, !on)));
        }

        foreach (var project in host.Projects.Bounded(StudioLink.MaxProjects))
        {
            var name = project.Name;
            _items.Add((name, word, () => host.SetProject(name)));
        }
    }

    private void Modules()
    {
        var frame = L("frame");
        foreach (var module in host.Side.Modules.Bounded(StudioSide.MaxModules))
        {
            var programs = module.Programs;
            _items.Add((module.Display, frame, () =>
            {
                var game = programs.Length > 0
                    ? ShaderAssets.InstalledPath(ShaderAssets.ProgramKey(programs[0], ".fsh"))
                    : null;
                var copy = host.Project is { } p && game is not null ? StudioLink.CopyPath(p, game) : null;
                if ((copy ?? game) is { } path) host.Open(path);
            }));
        }
    }

    private void Files()
    {
        foreach (var folder in ShaderAssets.Folders().Prepend(StudioLink.ProjectsFolder).ToArray().Bounded(7))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", ShaderPaths.Recursive(true))
                             .Bounded(MaxItems))
                {
                    var path = file;
                    if (StudioLink.Hidden(path))
                    {
                        continue;
                    }

                    _items.Add((StudioLink.Name(path), StudioLink.Display(path), () => host.OpenExact(path)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                host.AddLog(e.Message, true);
            }
        }
    }

    private void Filter()
    {
        if (_version == _query.Version) return;

        _version = _query.Version;
        _shown.Clear();
        var q = _query.Text;
        for (var i = 0; i < Math.Min(_items.Count, MaxItems) && _shown.Count < MaxShown; i++)
        {
            var (text, key, _) = _items[i];
            if (q.Length == 0 || text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _shown.Add(i);
            }
        }

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));
    }

    public void Type(char c)
    {
        _query.Type(c);
        _selected = 0;
    }

    public bool Key(GlKeys key, bool ctrl)
    {
        Filter();
        switch (key)
        {
            case GlKeys.Escape:
                Close();
                return true;
            case GlKeys.Down:
                _selected = Math.Min(_shown.Count - 1, _selected + 1);
                return true;
            case GlKeys.Up:
                _selected = Math.Max(0, _selected - 1);
                return true;
            case GlKeys.Enter or GlKeys.KeypadEnter:
                Run(_selected);
                return true;
            default:
                _ = _query.Key(key, ctrl, host.Api);
                _selected = 0;
                return true;
        }
    }

    private void Run(int shown)
    {
        if (shown < 0 || shown >= _shown.Count) return;

        var run = _items[_shown[shown]].Run;
        Close();
        run();
    }

    public void Draw(HudUi ui)
    {
        if (!Open) return;

        Filter();
        ui.Fill(ui.X, 0, ui.W, host.Height, Rgba.Black(0.45));
        host.Hits.Add(ui.X, 0, ui.W, host.Height, Close);
        var w = Math.Min(ui.Px(560), ui.W - ui.Px(40));
        var row = ui.Px(30);
        var (x, y) = (ui.X + (ui.W - w) / 2, ui.Px(80));
        var h = ui.Px(52) + Math.Max(1, _shown.Count) * row + ui.Px(8);
        ui.Panel(x, y, w, h, HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        host.Hits.Block(x, y, w, h);
        _ = _query.Draw(ui, x + ui.Px(10), y + ui.Px(10), w - ui.Px(20), L("palette-placeholder"), true);
        var top = y + ui.Px(52);
        for (var i = 0; i < Math.Min(_shown.Count, MaxShown); i++)
        {
            var (text, key, _) = _items[_shown[i]];
            var ry = top + i * row;
            if (i == _selected || host.Hot(x, ry, w, row))
            {
                ui.Fill(x + 1, ry, w - 2, row, i == _selected ? HudUi.SelectedBack : HudUi.Track with { A = 0.05 });
            }

            var kw = Math.Min(ui.TextW(ui.F.Small, key), w * 0.45);
            ui.Text(x + ui.Px(16), ry, row, ui.F.Body, ui.Fit(ui.F.Body, text, w - kw - ui.Px(48)),
                i == _selected ? Rgba.White(1) : HudUi.Soft);
            ui.TextRight(x + w - ui.Px(16), ry, row, ui.F.Small, ui.Fit(ui.F.Small, key, w * 0.45), HudUi.Faint);
            var index = i;
            host.Hits.Add(x, ry, w, row, () => Run(index));
        }
    }
}

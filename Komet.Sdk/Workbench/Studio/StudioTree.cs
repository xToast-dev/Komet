using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The explorer: ONE tree rooted at the player's Vintagestory folder, the game's assets beside its folders, drawn as
// Visual Studio's Solution Explorer (triangles, indent guides, gold folders, typed file icons, full-row selection, a
// lock on read-only entries, tags on the right). Folders list when opened; the rows are flattened only when something
// changed. Projects (ModConfig/komet/shaders/<name>) carry a star and AKTIV, AUS or n FEHLER.
internal sealed partial class StudioTree(ShaderIde host) : IIdeRegion
{
    private const int MaxRows = 4096, MaxScan = 6000;
    private const double RowHeight = 22, Indent = 16, HeadHeight = 40, ToolHeight = 30;

    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StudioEntry[]> _listed = new(StringComparer.Ordinal);
    private readonly List<Node> _rows = [];
    private readonly HashSet<string> _filtered = new(StringComparer.Ordinal);
    private readonly TextField _filter = new(), _rename = new();
    private string? _selected, _renaming, _dragFrom, _dropOn;
    private bool _stale = true, _loaded;
    private int _first, _filterVersion = -1;
    private double _x, _w, _rowsTop, _row, _pressX, _pressY;
    private Action? _newFile, _newFolder, _revealActive, _collapse, _focusFilter, _exportVanilla;

    private readonly record struct Node(string Path, string Name, int Depth, bool Folder, bool ReadOnly, bool Root);

    // Lists the open folders again (the files may have changed on disk)
    public void Refresh()
    {
        if (!_loaded)
        {
            _loaded = true;
            Load();
        }

        _listed.Clear();
        _tags.Clear();
        _marks.Clear();
        _stale = true;
    }

    private void Load()
    {
        foreach (var path in host.Prefs.Open.Bounded(256))
        {
            _ = _open.Add(path);
        }

        if (_open.Count == 0)
        {
            foreach (var path in (ReadOnlySpan<string>)
                     [StudioLink.DataRoot, Path.GetDirectoryName(StudioLink.KometFolder)!, StudioLink.KometFolder, StudioLink.ProjectsFolder])
            {
                _ = _open.Add(path);
            }
        }
    }

    public List<string> OpenFolders() => [.. _open.Take(256)];

    // Opens the folders down to a path and selects it
    public void Reveal(string path)
    {
        var folder = Path.GetDirectoryName(path);
        for (var i = 0; i < 32 && folder is not null && folder.Length >= StudioLink.DataRoot.Length; i++)
        {
            _ = _open.Add(folder);
            folder = Path.GetDirectoryName(folder);
        }

        if (StudioLink.Within(path, StudioLink.AssetsRoot)) _ = _open.Add(StudioLink.DataRoot);

        _ = _open.Add(StudioLink.DataRoot);
        (_selected, _stale) = (path, true);
        Refresh();
        ScrollTo(path);
    }

    public void Select(string path, bool reveal)
    {
        if (reveal)
        {
            Reveal(path);
            return;
        }

        _selected = path;
    }

    private StudioEntry[] Children(string folder)
    {
        if (!_listed.TryGetValue(folder, out var entries))
        {
            entries = StudioLink.List(folder);
            if (StudioLink.PathEquals(folder, StudioLink.DataRoot))
            {
                entries = [new StudioEntry(StudioLink.AssetsRoot, "assets", true, false),
                    .. entries.Where(e => !StudioLink.PathEquals(e.Path, StudioLink.AssetsRoot))];
            }

            _listed[folder] = entries;
        }

        return entries;
    }

    // The visible rows: depth first over the open folders (a stack, no recursion); with a filter, the matching files
    // and the folders that lead to them
    private void Flatten()
    {
        if (!_stale && _filterVersion == _filter.Version) return;

        if (_filterVersion != _filter.Version)
        {
            _filterVersion = _filter.Version;
            Scan();
        }

        _stale = false;
        _rows.Clear();
        if (!host.Prefs.AllFiles)
        {
            ShaderRows();
            return;
        }

        RootRows(StudioLink.DataRoot, L("root-name"));
        if (StudioLink.AssetsRoot.Length > 0) RootRows(StudioLink.AssetsRoot, "assets");
    }

    // A root and, while it is open or a filter is set, what lies under it
    private void RootRows(string root, string name)
    {
        var filtering = _filter.Text.Length > 0;
        if (filtering && !_filtered.Contains(root)) return;

        _rows.Add(new Node(root, name, 0, true, true, true));
        if (!filtering && !_open.Contains(root)) return;

        var stack = new Stack<(StudioEntry Entry, int Depth)>();
        Push(stack, root, 1);
        for (var i = 0; i < MaxRows && stack.Count > 0 && _rows.Count < MaxRows; i++)
        {
            var (entry, depth) = stack.Pop();
            if (filtering && !_filtered.Contains(entry.Path)) continue;

            _rows.Add(new Node(entry.Path, entry.Name, depth, entry.Folder, !entry.Writable, false));
            if (entry.Folder && (filtering || _open.Contains(entry.Path))) Push(stack, entry.Path, depth + 1);
        }
    }

    private void Push(Stack<(StudioEntry, int)> stack, string folder, int depth)
    {
        var children = Children(folder);
        for (var k = 0; k < Math.Min(children.Length, StudioLink.MaxEntries); k++)
        {
            stack.Push((children[children.Length - 1 - k], depth));
        }
    }

    // The filter's matches: files and folders whose name holds it, searched breadth first (bounded) over the projects
    // and the game's shaders first, then the rest of the Vintagestory folder without its bulky data (worlds, caches,
    // logs), with every folder on their way
    private void Scan()
    {
        _filtered.Clear();
        var text = _filter.Text;
        if (text.Length == 0) return;

        var queue = new Queue<string>();
        queue.Enqueue(StudioLink.KometFolder);
        foreach (var folder in ShaderAssets.Folders().Bounded(6)) queue.Enqueue(folder);
        queue.Enqueue(StudioLink.DataRoot);
        var seen = new HashSet<string>(StringComparer.Ordinal) { StudioLink.KometFolder };
        var scanned = 0;
        for (var i = 0; i < MaxScan && queue.Count > 0 && scanned < MaxScan; i++)
        {
            foreach (var entry in StudioLink.List(queue.Dequeue()).Bounded(StudioLink.MaxEntries))
            {
                scanned++;
                if (entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) Include(entry.Path);

                if (entry.Folder && !Bulky(entry.Name) && seen.Add(entry.Path)) queue.Enqueue(entry.Path);
            }
        }
    }

    private static bool Bulky(string name) => name is "Saves" or "Backups" or "Cache" or "Logs" or "Maps" or
        "Screenshots" or "Playerdata" or "BackupSaves" or "WorldEdit";

    private void Include(string path)
    {
        var at = path;
        for (var i = 0; i < 32 && at is not null && _filtered.Add(at); i++)
        {
            at = Path.GetDirectoryName(at);
            if (at is not null && StudioLink.PathEquals(at, StudioLink.AssetsRoot))
            {
                _ = _filtered.Add(at);
                break;
            }
        }
    }

    public void Draw(HudUi ui, double x, double top, double w, double h)
    {
        (_x, _w, _row) = (x, w, ui.Px(RowHeight));
        ui.Fill(x, top, w, h, host.PanelGround);
        ui.Fill(x + w - 1, top, 1, h, HudUi.Divider);
        Head(ui, x, top, w);
        var fy = top + ui.Px(HeadHeight + ToolHeight) + ui.Px(8);
        _ = _filter.Draw(ui, x + ui.Px(10), fy, w - ui.Px(20), "      " + L("filter"), host.Focus == StudioFocus.Filter);
        StudioIcons.Magnifier(ui, x + ui.Px(17), fy + (ui.F.BodyRow + ui.Px(8)) / 2, ui.Px(12), HudUi.Faint);
        host.Hits.Add(x + ui.Px(10), fy, w - ui.Px(20), ui.F.BodyRow + ui.Px(8), _focusFilter!);
        _rowsTop = Legend(ui, x, fy + ui.F.BodyRow + ui.Px(8) + ui.Px(4), w);
        Flatten();
        var shown = Math.Max(1, (int)((top + h - _rowsTop) / _row));
        _first = Math.Clamp(_first, 0, Math.Max(0, _rows.Count - shown));
        for (var i = 0; i < Math.Min(shown, 512) && _first + i < _rows.Count; i++)
        {
            Row(ui, _rows[_first + i], _rowsTop + i * _row);
        }

        host.Hits.Region(x, _rowsTop, w, top + h - _rowsTop, this);
        RenameField(ui);
    }

    // What the override mark means, once over the list: ● overrides vanilla  ○ override off
    private double Legend(HudUi ui, double x, double y, double w)
    {
        var h = ui.F.SmallRow;
        var (r, at) = (ui.Px(4), x + ui.Px(16));
        StudioIcons.Override(ui, at, y + h / 2, r, HudUi.Link, true);
        var on = ui.Fit(ui.F.Small, L("legend-override-on"), w * 0.45);
        ui.Text(at + ui.Px(9), y, h, ui.F.Small, on, HudUi.Faint);
        at += ui.Px(9) + ui.TextW(ui.F.Small, on) + ui.Px(16);
        StudioIcons.Override(ui, at, y + h / 2, r, HudUi.Faint, false);
        ui.Text(at + ui.Px(9), y, h, ui.F.Small, ui.Fit(ui.F.Small, L("legend-override-off"), x + w - at - ui.Px(20)), HudUi.Faint);
        host.Tip(x, y, w, h, L("tip-legend-override"));
        return y + h + ui.Px(4);
    }

    // The view names (SHADERS · ALL FILES) and below them the buttons: new file, new folder, show the active file,
    // collapse all, export the vanilla shaders into the pack
    private void Head(HudUi ui, double x, double top, double w)
    {
        _newFile ??= () => NewEntry(false);
        _newFolder ??= () => NewEntry(true);
        _revealActive ??= () =>
        {
            if (host.Current is { } current) Reveal(current.Path);
        };
        _collapse ??= () =>
        {
            _open.Clear();
            _ = _open.Add(StudioLink.DataRoot);
            _stale = true;
        };
        _focusFilter ??= () => host.Focus = StudioFocus.Filter;
        var h = ui.Px(HeadHeight);
        ui.Fill(x, top + h - 1, w, 1, HudUi.Divider);
        Views(ui, x + ui.Px(14), top, h);
        // the buttons get a row of their own under the view names: side by side they did not fit the column
        var b = ui.Px(26);
        var at = x + ui.Px(10);
        top += h;
        h = ui.Px(ToolHeight);
        ui.Fill(x, top + h - 1, w, 1, HudUi.Divider);
        _exportVanilla ??= host.ExportVanilla;
        ReadOnlySpan<string> tips = ["tip-new-file", "tip-new-folder", "tip-reveal", "tip-collapse", "tip-export-vanilla"];
        foreach (var (kind, click) in (ReadOnlySpan<(int, Action)>)
                 [(0, _newFile), (1, _newFolder), (2, _revealActive), (3, _collapse), (4, _exportVanilla)])
        {
            var color = host.Hot(at, top + (h - b) / 2, b, b) ? HudUi.Soft : HudUi.Faint;
            HeadIcon(ui, kind, at + ui.Px(6), top + h / 2, ui.Px(14), color);
            host.Hits.Add(at, top + (h - b) / 2, b, b, click);
            host.Tip(at, top + (h - b) / 2, b, b, L(tips[kind]));
            at += b;
        }
    }

    private static void HeadIcon(HudUi ui, int kind, double x, double cy, double s, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.3));
        switch (kind)
        {
            case 0:
                StudioIcons.File(ui, x, cy, s, ".txt");
                ui.Stroke(x + s * 0.47, cy - s * 0.1, x + s * 0.47, cy + s * 0.3, color, w);
                ui.Stroke(x + s * 0.27, cy + s * 0.1, x + s * 0.67, cy + s * 0.1, color, w);
                break;
            case 1:
                StudioIcons.Folder(ui, x, cy, s, color);
                ui.Stroke(x + s / 2, cy - s * 0.05, x + s / 2, cy + s * 0.3, color, w);
                ui.Stroke(x + s * 0.32, cy + s * 0.12, x + s * 0.68, cy + s * 0.12, color, w);
                break;
            case 4: // an arrow down onto a tray: export
                ui.Stroke(x + s / 2, cy - s / 2, x + s / 2, cy + s * 0.15, color, w);
                ui.Stroke(x + s * 0.25, cy - s * 0.1, x + s / 2, cy + s * 0.15, color, w);
                ui.Stroke(x + s * 0.75, cy - s * 0.1, x + s / 2, cy + s * 0.15, color, w);
                ui.Stroke(x, cy + s * 0.25, x, cy + s * 0.45, color, w);
                ui.Stroke(x, cy + s * 0.45, x + s, cy + s * 0.45, color, w);
                ui.Stroke(x + s, cy + s * 0.25, x + s, cy + s * 0.45, color, w);
                break;
            case 2:
                ui.Ring(x + s / 2, cy, s * 0.18, color, w);
                ui.Stroke(x + s / 2, cy - s / 2, x + s / 2, cy - s * 0.25, color, w);
                ui.Stroke(x + s / 2, cy + s * 0.25, x + s / 2, cy + s / 2, color, w);
                ui.Stroke(x, cy, x + s * 0.25, cy, color, w);
                ui.Stroke(x + s * 0.75, cy, x + s, cy, color, w);
                break;
            default:
                StudioIcons.Triangle(ui, x + s / 2, cy - s * 0.25, s * 0.6, true, color);
                StudioIcons.Triangle(ui, x + s / 2, cy + s * 0.25, s * 0.6, false, color);
                break;
        }
    }

    private void Row(HudUi ui, Node node, double y)
    {
        var selected = _selected is { } s && StudioLink.PathEquals(s, node.Path);
        if (selected)
        {
            ui.Fill(_x, y, _w - 1, _row, (host.Controls?.Style["select"] ?? HudUi.SelectionBack) with { A = 0.55 });
            ui.Fill(_x, y, Math.Max(1, ui.Px(2)), _row, HudUi.Accent);
        }
        else if (host.Hot(_x, y, _w, _row))
        {
            ui.Fill(_x, y, _w - 1, _row, HudUi.Track with { A = 0.045 });
        }

        if (_dropOn is { } drop && StudioLink.PathEquals(drop, node.Path))
        {
            TextField.Frame(ui, _x, y, _w - 1, _row, StudioLink.CanWrite(DropFolder(node)) ? HudUi.Accent : HudUi.Err);
        }

        var indent = ui.Px(Indent);
        for (var d = 0; d < Math.Min(node.Depth, 32); d++)
        {
            ui.Fill(_x + ui.Px(8) + d * indent + ui.Px(7), y, Math.Max(1, ui.Px(1)), _row, HudUi.Divider with { A = 0.09 });
        }

        var at = _x + ui.Px(8) + node.Depth * indent;
        if (node.Folder) StudioIcons.Twist(ui, at, y + _row / 2, indent, Open(node), HudUi.Faint);

        at += indent;
        Icon(ui, node, at, y + _row / 2);
        at += ui.Px(21);
        Label(ui, node, at, y, selected);
    }

    private bool Open(Node node)
    {
        if (Group(node.Path)) return Shown(node.Path);

        return _filter.Text.Length > 0 || _open.Contains(node.Path);
    }
}

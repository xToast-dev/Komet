using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The explorer's "Shaders" view (the default): the active pack and the other packs, narrowed by the filter. The game's
// own shaders come into a pack with the explorer's export button; "All files" still shows the installed ones.
// Headings are rows of their own (inert: no menu, no drag), folded like folders. "All files" is the
// whole Vintagestory tree as before; the choice is kept in studio.json.
internal sealed partial class StudioTree
{
    private const string GroupPrefix = "komet-group:";
    private const string ActiveGroup = GroupPrefix + "active", OtherGroup = GroupPrefix + "other";

    private Action? _showShaders, _showAll;

    public void ClearTags()
    {
        _tags.Clear();
        _marks.Clear();
    }

    private static bool Group(string path) => path.StartsWith(GroupPrefix, StringComparison.Ordinal);

    // SHADERS | ALL FILES in the head, the chosen one lit
    private void Views(HudUi ui, double x, double top, double h)
    {
        _showShaders ??= () => SetView(false);
        _showAll ??= () => SetView(true);
        foreach (var (all, key, click) in (ReadOnlySpan<(bool, string, Action)>)
                 [(false, "view-shaders", _showShaders), (true, "view-all", _showAll)])
        {
            var caps = Caps(L(key));
            var on = host.Prefs.AllFiles == all;
            var tw = ui.Spaced(x, top, h, ui.F.Small, caps, ui.Px(2), on ? Rgba.White(1) : HudUi.Faint);
            if (on) ui.Fill(x, top + h - ui.Px(4), tw, Math.Max(1, ui.Px(1)), HudUi.Link);

            host.Hits.Add(x - ui.Px(4), top, tw + ui.Px(8), h, click);
            host.Tip(x - ui.Px(4), top, tw + ui.Px(8), h, L(all ? "tip-view-all" : "tip-view-shaders"));
            x += tw + ui.Px(16);
        }
    }

    private void SetView(bool all)
    {
        host.Prefs.AllFiles = all;
        (_stale, _first) = (true, 0);
    }

    private void ShaderRows()
    {
        var filter = _filter.Text;
        var packs = StudioLink.List(StudioLink.ProjectsFolder).Where(e => e.Folder).ToArray();
        var active = Array.Find(packs, p => p.Name == host.Project);
        GroupRow(ActiveGroup, active is null ? L("no-active-pack") : L("active-pack", active.Name));
        if (active is not null && Shown(ActiveGroup)) Under(active.Path, 1);

        var others = packs.Where(p => p.Name != host.Project).ToArray();
        if (others.Length > 0)
        {
            GroupRow(OtherGroup, L("other-packs", others.Length));
            foreach (var pack in others.Bounded(StudioLink.MaxEntries))
            {
                if (Shown(OtherGroup) && _rows.Count < MaxRows)
                {
                    _rows.Add(new Node(pack.Path, pack.Name, 1, true, !pack.Writable, false));
                    Under(pack.Path, 2, _open.Contains(pack.Path) || filter.Length > 0);
                }
            }
        }

    }

    private bool Shown(string group) => _filter.Text.Length > 0 || !_open.Contains(group + ":shut");

    private void GroupRow(string id, string title) => _rows.Add(new Node(id, title, 0, true, true, false));

    // A pack folder's files as the tree shows folders (open ones listed), the filter's matches only while it is set
    private void Under(string folder, int depth, bool open = true)
    {
        if (!open) return;

        var filtering = _filter.Text.Length > 0;
        var stack = new Stack<(StudioEntry Entry, int Depth)>();
        Push(stack, folder, depth);
        for (var i = 0; i < MaxRows && stack.Count > 0 && _rows.Count < MaxRows; i++)
        {
            var (entry, at) = stack.Pop();
            if (filtering && !_filtered.Contains(entry.Path)) continue; // only matches and the folders leading to them

            _rows.Add(new Node(entry.Path, entry.Name, at, entry.Folder, !entry.Writable, false));
            if (entry.Folder && (filtering || _open.Contains(entry.Path))) Push(stack, entry.Path, at + 1);
        }
    }
}

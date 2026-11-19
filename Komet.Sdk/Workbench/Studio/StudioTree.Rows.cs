using System.Globalization;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// A row's icon, name (the filter's match marked), lock and tag
internal sealed partial class StudioTree
{
    private static string? _homeShort;
    private readonly Dictionary<string, int> _marks = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string, Rgba)> _tags = new(StringComparer.Ordinal);

    private double _renameX, _renameY, _renameW;

    private void Icon(HudUi ui, Node node, double x, double cy)
    {
        var s = ui.Px(15);
        if (node.Root)
        {
            StudioIcons.Home(ui, x, cy, s, HudUi.Link);
        }
        else if (node.Folder && StudioLink.IsProjectFolder(node.Path))
        {
            var active = StudioLink.Name(node.Path) == host.Project;
            StudioIcons.Folder(ui, x, cy, s, active ? HudUi.Link : StudioColors.Gold, true);
        }
        else if (node.Folder)
        {
            var dim = node.ReadOnly && !StudioLink.Ancestor(node.Path);
            StudioIcons.Folder(ui, x, cy, s, dim ? StudioColors.Gold with { A = 0.45 } : StudioColors.Gold);
        }
        else
        {
            StudioIcons.File(ui, x, cy, s, Path.GetExtension(node.Name).ToLowerInvariant());
        }
    }

    // The name has the room first: the override mark sits in a fixed column on the right, a tag (errors, an update)
    // left of it; a long name is cut in the middle, its extension always shown, the full name in the tooltip
    private void Label(HudUi ui, Node node, double x, double y, bool selected)
    {
        var (tag, tagColor) = CachedTag(node);
        var mark = CachedMark(node);
        var markW = mark >= 0 ? ui.Px(18) : 0;
        var tagW = tag.Length > 0 ? ui.TextW(ui.F.Small, tag) + ui.Px(1.5) * tag.Length + ui.Px(8) : 0;
        if (tag.Length > 0 && TagTip(tag) is { } tip) host.Tip(_x + _w - ui.Px(8) - markW - tagW, y, tagW, _row, tip);

        var locked = node is { Root: false, ReadOnly: true } && !StudioLink.Ancestor(node.Path) && !Group(node.Path);
        var lockW = locked ? ui.Px(16) : 0;
        var room = _x + _w - ui.Px(8) - markW - tagW - lockW - x;
        if (_renaming is { } renaming && StudioLink.PathEquals(renaming, node.Path))
        {
            (_renameX, _renameY, _renameW) = (x, y, Math.Max(ui.Px(60), Math.Min(ui.Px(160), room)));
            return;
        }

        var font = node.Root ? ui.F.Strong : ui.F.Body;
        var name = node.Root
            ? ui.Fit(font, node.Name, Math.Max(ui.Px(20), room))
            : MiddleFit(ui, font, node.Name, Math.Max(ui.Px(20), room));
        Mark(ui, name, x, y);
        ui.Text(x, y, _row, font, name, Ink(node, selected, locked));
        var end = x + ui.TextW(font, name);
        if (name != node.Name) host.Tip(x, y, end - x, _row, node.Name);

        if (locked)
        {
            StudioIcons.Lock(ui, end + ui.Px(6), y + _row / 2, ui.Px(9), HudUi.Faint with { A = 0.35 });
            host.Tip(end, y, ui.Px(18), _row,
                L(StudioLink.IsGameShader(node.Path) ? "tip-lock-vanilla" : "tip-lock-readonly"));
        }

        if (tag.Length > 0)
        {
            var tw = ui.TextW(ui.F.Small, tag) + ui.Px(1.5) * Math.Max(0, tag.Length - 1);
            _ = ui.Spaced(_x + _w - ui.Px(8) - markW - tw, y, _row, ui.F.Small, tag, ui.Px(1.5), tagColor);
        }

        OverrideMark(ui, mark, _x + _w - ui.Px(8) - markW, y, markW);
    }

    // The override mark: a filled dot while the copy overrides vanilla, a hollow ring while its override is off
    private void OverrideMark(HudUi ui, int mark, double x, double y, double w)
    {
        if (mark < 0) return;

        StudioIcons.Override(ui, x + w / 2, y + _row / 2, ui.Px(4), mark == 1 ? HudUi.Link : HudUi.Faint, mark == 1);
        host.Tip(x, y, w, _row, L(mark == 1 ? "tip-tag-replaced" : "tip-tag-override-off"));
    }

    // A name cut in the middle to a width: its start, "…", its extension ("clouds….vsh"); as is when it fits
    private static string MiddleFit(HudUi ui, CairoFont font, string name, double width)
    {
        if (ui.TextW(font, name) <= width) return name;

        var ext = Path.GetExtension(name);
        ext = ext.Length is > 0 and <= 8 ? ext : "";
        var stem = name[..^ext.Length];
        for (var i = 0; i < Math.Min(stem.Length - 1, 256); i++)
        {
            var keep = stem.Length - 1 - i;
            var cut = stem[..keep] + "…" + ext;
            if (ui.TextW(font, cut) <= width) return cut;
        }

        return "…" + ext;
    }

    // What a badge means and what to do about it
    private static string? TagTip(string tag)
    {
        foreach (var key in (ReadOnlySpan<string>)
                 ["tag-active", "tag-off", "tag-update", "tag-overlaid", "tag-pass", "tag-copy"])
        {
            if (tag == Caps(L(key))) return L("tip-" + key);
        }

        return null;
    }

    // White for the selected row, the root and projects (the active one in the title colour), faint for read-only
    // and trashed entries
    private Rgba Ink(Node node, bool selected, bool locked)
    {
        if (selected || node.Root) return Rgba.White(1);

        if (StudioLink.IsProjectFolder(node.Path))
        {
            return StudioLink.Name(node.Path) == host.Project ? HudUi.Link : Rgba.White(1);
        }

        var trashed = StudioLink.Within(node.Path, StudioLink.TrashFolder) &&
                      !StudioLink.PathEquals(node.Path, StudioLink.TrashFolder);
        return locked || trashed ? HudUi.Faint : HudUi.Soft;
    }

    // The filter's match in a file name, on a gold ground
    private void Mark(HudUi ui, string name, double x, double y)
    {
        var text = _filter.Text;
        var at = text.Length == 0 ? -1 : name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + text.Length > name.Length) return;

        var from = x + ui.TextW(ui.F.Body, name[..at]);
        ui.Fill(from, y + ui.Px(3), ui.TextW(ui.F.Body, name.Substring(at, text.Length)), _row - ui.Px(6),
            StudioColors.Gold with { A = 0.3 });
    }

    // -1 no override, 0 its override is off, 1 it overrides vanilla; kept until the next refresh as the tags are
    private int CachedMark(Node node)
    {
        if (!_marks.TryGetValue(node.Path, out var mark))
        {
            var (copy, on) = node.Folder ? (false, false) : host.OverrideState(node.Path);
            mark = on ? 1 : 0;
            mark = copy ? mark : -1;
            if (_marks.Count < MaxRows) _marks[node.Path] = mark;
        }

        return mark;
    }

    // Tags ask the projects and the files: kept until the next refresh
    private (string Text, Rgba Color) CachedTag(Node node)
    {
        if (!_tags.TryGetValue(node.Path, out var tag))
        {
            tag = Tag(node);
            if (_tags.Count < MaxRows) _tags[node.Path] = tag;
        }

        return tag;
    }

    // The tag on the right: the root's path, a project's state, a file's errors or what it is
    private (string Text, Rgba Color) Tag(Node node)
    {
        if (node.Root) return (HomeShort(StudioLink.DataRoot), HudUi.Faint);

        if (node.Folder && StudioLink.IsProjectFolder(node.Path))
        {
            var name = StudioLink.Name(node.Path);
            var errors = host.Panel.ErrorsIn(node.Path);
            if (errors > 0)
            {
                return (errors.ToString(CultureInfo.InvariantCulture) + " " + Caps(L("errors-word")), HudUi.Err);
            }

            return host.ProjectInfo(name) is { Active: true }
                ? (Caps(L("tag-active")), HudUi.Ok)
                : (Caps(L("tag-off")), HudUi.Faint);
        }

        return node.Folder ? ("", HudUi.Faint) : FileTag(node);
    }

    private (string Text, Rgba Color) FileTag(Node node)
    {
        var errors = host.Panel.ErrorsIn(node.Path);
        if (errors > 0) return (errors.ToString(CultureInfo.InvariantCulture), HudUi.Err);

        if (StudioLink.GameFileOf(node.Path) is not null)
        {
            if (StudioLink.OriginalChanged(node.Path) is not null) return (Caps(L("tag-update")), StudioColors.Gold);

            if (StudioLink.OverlaidBy(node.Path) is not null) return (Caps(L("tag-overlaid")), StudioColors.Gold);

            return ("", HudUi.Faint); // on or off shows as the override mark
        }

        if (StudioLink.ProjectOf(node.Path) is { } project &&
            StudioLink.Within(node.Path, Path.Join(StudioLink.ProjectsFolder, project, "passes")))
        {
            return (Caps(L("tag-pass")), HudUi.Faint);
        }

        return host.HasCopy(node.Path)
            ? (Caps(L("tag-copy")), HudUi.Link)
            : ("", HudUi.Faint);
    }

    private static string HomeShort(string path)
    {
        if (_homeShort is not null) return _homeShort;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _homeShort = home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..].Replace('\\', '/')
            : path;
        return _homeShort;
    }

    private void RenameField(HudUi ui)
    {
        if (_renaming is null || _renameW <= 0) return;

        var h = ui.F.BodyRow + ui.Px(8);
        var y = _renameY + (_row - h) / 2;
        ui.Fill(_renameX - ui.Px(4), y, _renameW, h, host.PanelGround);
        _ = _rename.Draw(ui, _renameX - ui.Px(4), y, _renameW, "", true);
        host.Hits.Block(_renameX - ui.Px(4), y, _renameW, h);
    }

    private void ScrollTo(string path)
    {
        Flatten();
        var index = _rows.FindIndex(r => StudioLink.PathEquals(r.Path, path));
        if (index < 0 || _row <= 0) return;

        var shown = Math.Max(1, (int)((host.Height - _rowsTop) / _row));
        if (index < _first || index >= _first + shown) _first = Math.Max(0, index - shown / 2);
    }

    // Where a drop on a row lands: the folder itself, or a file's folder
    private static string DropFolder(Node node) =>
        node.Folder ? node.Path : Path.GetDirectoryName(node.Path) ?? node.Path;
}

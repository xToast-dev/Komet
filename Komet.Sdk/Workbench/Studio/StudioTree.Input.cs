using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The tree's mouse (select, open, drag to move), keys (arrows like Visual Studio, Enter, F2, Entf), filter and rename
internal sealed partial class StudioTree
{
    private const double DragStart = 6;

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        if (RowAt(y) is not { } node || button != EnumMouseButton.Left) return false;

        host.Focus = StudioFocus.Tree;
        if (Group(node.Path))
        {
            Toggle(node.Path + ":shut"); // a heading folds its list
            return true;
        }

        (_selected, _pressX, _pressY, _dragFrom) = (node.Path, x, y, null);
        if (node.Folder)
        {
            Toggle(node.Path);
            if (StudioLink.IsProjectFolder(node.Path)) host.SetProject(StudioLink.Name(node.Path));
        }
        else
        {
            host.Open(node.Path);
            host.Focus = StudioFocus.Tree;
        }

        _dragFrom = node.Root || node.ReadOnly || StudioLink.Fixed(node.Path) ? null : node.Path;
        _dropOn = null;
        return true;
    }

    public void Drag(double x, double y)
    {
        if (_dragFrom is null || Math.Abs(x - _pressX) + Math.Abs(y - _pressY) < DragStart) return;

        _dropOn = RowAt(y)?.Path;
    }

    public void Up()
    {
        if (_dragFrom is { } from && _dropOn is { } on && _rows.Find(r => r.Path == on) is { Path.Length: > 0 } node &&
            !StudioLink.PathEquals(from, on))
        {
            MoveTo(from, DropFolder(node));
        }

        (_dragFrom, _dropOn) = (null, null);
    }

    public bool Wheel(double x, int lines)
    {
        _first = Math.Max(0, _first + lines);
        return true;
    }

    private Node? RowAt(double y)
    {
        if (_row <= 0 || y < _rowsTop) return null;

        var index = _first + (int)((y - _rowsTop) / _row);
        return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    private void Toggle(string folder)
    {
        _ = _open.Remove(folder) || _open.Add(folder);
        _stale = true;
    }

    public bool Key(GlKeys key, bool ctrl)
    {
        if (host.Focus == StudioFocus.Rename) return RenameKey(key, ctrl);

        if (host.Focus == StudioFocus.Filter)
        {
            if (key is GlKeys.Enter or GlKeys.Down or GlKeys.KeypadEnter)
            {
                host.Focus = StudioFocus.Tree;
                return true;
            }

            _ = _filter.Key(key, ctrl, host.Api);
            _first = 0;
            return true;
        }

        return TreeKey(key);
    }

    private bool TreeKey(GlKeys key)
    {
        Flatten();
        var at = _selected is null ? -1 : _rows.FindIndex(r => StudioLink.PathEquals(r.Path, _selected));
        var node = at >= 0 ? _rows[at] : (Node?)null;
        switch (key)
        {
            case GlKeys.Up or GlKeys.Down when _rows.Count > 0:
                _selected = _rows[Math.Clamp(at + (key == GlKeys.Down ? 1 : -1), 0, _rows.Count - 1)].Path;
                ScrollTo(_selected);
                return true;
            case GlKeys.Right when node is { Folder: true } open:
                _stale |= _open.Add(open.Path);
                return true;
            case GlKeys.Left when node is { Folder: true } shut && _open.Contains(shut.Path):
                Toggle(shut.Path);
                return true;
            case GlKeys.Left when node is { Root: false } child:
                _selected = Path.GetDirectoryName(child.Path) ?? _selected;
                return true;
            case GlKeys.Enter or GlKeys.KeypadEnter when node is { Folder: false } file:
                host.Open(file.Path);
                return true;
            case GlKeys.F2 when node is { } renamed:
                BeginRename(renamed.Path);
                return true;
            case GlKeys.Delete when node is { } deleted:
                Delete(deleted.Path);
                return true;
            default: return true;
        }
    }

    public void Type(char c)
    {
        if (host.Focus == StudioFocus.Rename)
        {
            _rename.Type(c);
            return;
        }

        _filter.Type(c);
        _first = 0;
    }

    private void BeginRename(string path)
    {
        if (!StudioLink.CanWrite(path) || StudioLink.Fixed(path))
        {
            host.Toast(L("is-readonly", StudioLink.Name(path)));
            return;
        }

        (_renaming, _selected) = (path, path);
        _rename.Set(StudioLink.Name(path), true);
        host.Focus = StudioFocus.Rename;
    }

    private bool RenameKey(GlKeys key, bool ctrl)
    {
        switch (key)
        {
            case GlKeys.Enter or GlKeys.KeypadEnter:
                EndRename(true);
                return true;
            case GlKeys.Escape:
                EndRename(false);
                return true;
            default:
                _ = _rename.Key(key, ctrl, host.Api);
                return true;
        }
    }

    // Commits (or drops) the rename; a name with '/' moves the entry there
    public void EndRename(bool commit)
    {
        if (_renaming is not { } path) return;

        _renaming = null;
        host.Focus = StudioFocus.Tree;
        var name = _rename.Text.Trim();
        if (!commit || name.Length == 0 || name == StudioLink.Name(path)) return;

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            var target = Path.GetFullPath(Path.Join(Path.GetDirectoryName(path), name));
            MoveTo(path, Path.GetDirectoryName(target)!, Path.GetFileName(target));
            return;
        }

        if (StudioLink.Rename(path, name) is not { } renamed)
        {
            host.Toast(L("exists", name));
            return;
        }

        Renamed(path, renamed);
    }

    private void Renamed(string from, string to)
    {
        if (StudioLink.IsProjectFolder(to) && host.Project == StudioLink.Name(from))
        {
            host.SetProject(StudioLink.Name(to));
        }

        host.Moved(from, to);
        host.ProjectsChanged();
        Reveal(to);
    }

    private void MoveTo(string from, string folder, string? name = null)
    {
        if (!StudioLink.CanWrite(folder) || StudioLink.Within(folder, from))
        {
            host.Toast(L("is-readonly", StudioLink.Display(folder)));
            return;
        }

        if (StudioLink.Move(from, folder, name) is not { } moved)
        {
            host.Toast(L("is-readonly", StudioLink.Name(from)));
            return;
        }

        host.Toast(L("moved", StudioLink.Name(from)));
        Renamed(from, moved);
    }
}

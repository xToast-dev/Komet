using System.Globalization;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The tree's context menu and its file operations: new file, folder and project, delete to the Papierkorb (Strg Z or
// the toast's Rückgängig brings it back), restore, empty the trash, copy a game shader into the project
internal sealed partial class StudioTree
{
    private const string FragmentTemplate =
        "#version 330 core\nuniform sampler2D scene;\nin vec2 uv;\nout vec4 outColor;\n\nvoid main() {\n    outColor = texture(scene, uv);\n}\n";

    public bool Context(double x, double y, ContextMenu menu)
    {
        if (RowAt(y) is not { } node) return false;

        if (Group(node.Path))
        {
            return false; // a heading of the Shaders view: nothing to do with it
        }

        _selected = node.Path;
        var path = node.Path;
        var trash = StudioLink.TrashFolder;
        if (!node.Folder) menu.Add(L("open") + "    Enter", () => host.Open(path));

        if (StudioLink.Within(path, trash) && !StudioLink.PathEquals(path, trash))
        {
            if (host.Link.Trashed(path) is { } token) menu.Add(L("restore"), () => host.Restore(token));

            menu.Add(L("delete-forever") + " …    " + L("key-del"), () => Delete(path));
        }
        else
        {
            Entries(menu, node);
        }

        menu.Add(L("copy-path"), () =>
        {
            host.Api.Input.ClipboardText = path;
            host.Toast(L("path-copied"));
        });
        return true;
    }

    private void Entries(ContextMenu menu, Node node)
    {
        var path = node.Path;
        if (StudioLink.GameFileOf(path) is not null)
        {
            menu.Add(L("open-original"), () =>
            {
                host.Open(path);
                host.SetMode(EditorMode.Original); // the vanilla view of the same file, not a second entry
            });
        }

        if (StudioLink.IsGameShader(path) && !node.Folder)
        {
            menu.Add(L("open-original"), () => host.OpenExact(path));
            if (host.CopyOf(path) is { } copy)
            {
                menu.Add(L("edit-my-version"), () => host.Open(copy));
            }
            else
            {
                menu.Add(L("copy-into", host.Project ?? L("project-word")), () => host.CopyIn(path));
            }
        }

        if (StudioLink.IsProjectFolder(path)) ProjectEntries(menu, StudioLink.Name(path));

        if (StudioLink.PathEquals(path, StudioLink.TrashFolder) && Children(path).Length > 0)
        {
            menu.Add(L("empty-trash") + " …", EmptyTrash);
        }

        if (node.Folder && StudioLink.CanWrite(path) && !StudioLink.PathEquals(path, StudioLink.TrashFolder))
        {
            menu.Add(L("new-file"), () => NewEntry(false, path));
            menu.Add(L("new-folder"), () => NewEntry(true, path));
        }

        if (StudioLink.PathEquals(path, StudioLink.ProjectsFolder)) menu.Add(L("new-project"), NewProject);

        if (StudioLink.CanWrite(path) && !StudioLink.Fixed(path))
        {
            menu.Add(L("rename") + "    F2", () => BeginRename(path));
            menu.Add(L("delete") + "    " + L("key-del"), () => Delete(path));
        }
    }

    private void ProjectEntries(ContextMenu menu, string name)
    {
        var on = host.ProjectInfo(name) is { Active: true };
        menu.Add(L("make-active"), () => host.SetProject(name));
        menu.Add(on ? L("switch-off") : L("switch-on"), () => host.SetProjectOn(name, !on));
        menu.Add(L("export-mod") + " …", () => host.Modal.Export(name));
    }

    // Where a new entry goes: the selected folder (or the file's), else the project's passes/
    private string Target(string? folder)
    {
        var at = folder ?? _selected;
        if (at is not null && !Directory.Exists(at)) at = Path.GetDirectoryName(at);

        if (at is null || !StudioLink.CanWrite(at) || StudioLink.Within(at, StudioLink.TrashFolder))
        {
            at = host.Project is { } project ? Path.Join(StudioLink.ProjectsFolder, project, "passes") : null;
        }

        return at ?? StudioLink.ProjectsFolder;
    }

    private void NewEntry(bool folder, string? into = null)
    {
        var at = Target(into);
        var template = folder ? "" : NewFragment(at);
        var stem = folder ? L("new-folder-name") : L("new-file-name");
        string? made = null;
        for (var n = 1; n < 100 && made is null; n++)
        {
            var name = stem + n.ToString(CultureInfo.InvariantCulture) + (folder ? "" : ".fsh");
            if (!Path.Exists(Path.Join(at, name)))
            {
                made = StudioLink.Create(at, name, folder, template);
            }
        }

        if (made is null)
        {
            host.Toast(L("is-readonly", StudioLink.Display(at)));
            return;
        }

        Reveal(made);
        if (!folder) host.Open(made);

        BeginRename(made);
    }

    private static string NewFragment(string folder) =>
        StudioLink.ProjectOf(folder) is { } project &&
        StudioLink.Within(folder, Path.Join(StudioLink.ProjectsFolder, project, ShaderProjects.PassesFolder))
            ? ShaderProjects.PassTemplate
            : FragmentTemplate;

    private void NewProject()
    {
        var stem = L("new-project-name");
        for (var n = 1; n < 100; n++)
        {
            var name = n == 1 ? stem : stem + " " + n.ToString(CultureInfo.InvariantCulture);
            if (Directory.Exists(Path.Join(StudioLink.ProjectsFolder, name))) continue;

            if (StudioLink.CreateProject(name) is { } folder)
            {
                host.ProjectsChanged();
                host.SetProject(name);
                Reveal(Path.Join(folder, "shaders"));
                BeginRename(folder);
            }

            return;
        }
    }

    // Into the Papierkorb with a Rückgängig; inside it, gone for good after a question
    private void Delete(string path)
    {
        if (!StudioLink.CanWrite(path) || StudioLink.Fixed(path))
        {
            host.Toast(L(StudioLink.Fixed(path) ? "stays" : "is-readonly", StudioLink.Name(path)));
            return;
        }

        if (StudioLink.Within(path, StudioLink.TrashFolder))
        {
            var name = StudioLink.Name(path);
            host.Modal.Confirm(L("delete-forever"), L("gone-for-good", name),
                L("delete"), () =>
                {
                    _ = host.Link.Delete(path);
                    host.Moved(path, null);
                    host.Toast(L("deleted-forever", name));
                    Refresh();
                });
            return;
        }

        if (host.Link.Delete(path) is not { } token)
        {
            host.Toast(L("is-readonly", StudioLink.Name(path)));
            return;
        }

        Deleted(path, token);
    }

    private void Deleted(string path, TrashToken token)
    {
        host.Moved(path, null);
        host.Deleted(token);
        host.Focus = StudioFocus.Tree;
        host.AddLog(StudioLink.Display(path) + " → " + StudioLink.Display(token.Trashed));
        host.Toast(L("in-trash", StudioLink.Name(path)), L("undo"),
            () => host.UndoDelete());
        if (StudioLink.IsProjectFolder(path))
        {
            host.ProjectsChanged();
            if (host.Project == StudioLink.Name(path))
            {
                host.SetProject(Array.Find(StudioLink.Projects(), p => p.Name != StudioLink.Name(path))?.Name);
            }
        }

        _selected = null;
        Refresh();
    }

    private void EmptyTrash()
    {
        var entries = Children(StudioLink.TrashFolder);
        var count = entries.Length;
        host.Modal.Confirm(L("empty-trash"), L(count == 1 ? "trash-one-gone" : "trash-gone", count), L("empty"), () =>
        {
            foreach (var entry in entries.Bounded(StudioLink.MaxEntries))
            {
                _ = host.Link.Delete(entry.Path);
                host.Moved(entry.Path, null);
            }

            host.Toast(L("trash-emptied"));
            Refresh();
        });
    }
}

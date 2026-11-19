namespace Komet.Sdk;

// The files through StudioFiles: listing and reading anything under the Vintagestory folder and the game's assets,
// writing only inside ModConfig/komet/ (and ModConfig/komet.json). Deleting moves to ModConfig/komet/.papierkorb/ and
// hands back a token that restores it.
internal sealed partial class StudioLink
{
    // A file's text with \n line ends; false with the reason when it is unreadable or too large for the editor
    // Any text file opens (shaders, .json, .txt, configs, logs …) up to a megabyte; binary files are refused
    public const int MaxText = 1 << 20;
    private readonly Dictionary<string, TrashToken> _trash = new(StringComparer.Ordinal);

    public static bool CanWrite(string path) => !InstalledShaders.Packed(path) && StudioFiles.CanWrite(path);

    public static StudioEntry[] List(string folder) => [.. StudioFiles.List(folder).Where(e => !Hidden(e.Path))];

    // A project's bookkeeping (.komet/: the originals kept at copy time, their hashes): never a file of its own in the
    // explorer, the problems, the search or the palette; the original shows as the Vanilla view of its copy
    public static bool Hidden(string path) =>
        Within(path, ProjectsFolder) &&
        path.Replace('\\', '/').Contains("/" + ShaderProjects.MetaFolder, StringComparison.Ordinal);

    public static bool Read(string path, out string text, out string error)
    {
        (text, error) = ("", "");
        var read = InstalledShaders.Packed(path) ? InstalledShaders.Read(path, out error) : StudioFiles.Read(path);
        if (read is null)
        {
            if (error.Length == 0) error = StudioFiles.Error;
            return false;
        }

        if (read.Length > MaxText)
        {
            error = IdeText.L("large");
            return false;
        }

        if (read.Contains('\0', StringComparison.Ordinal))
        {
            error = IdeText.L("binary");
            return false;
        }

        text = read.ReplaceLineEndings("\n");
        return true;
    }

    public static bool Write(string path, string text) => StudioFiles.Write(path, text);

    // A new file (with its text) or folder in a writable folder; its path, null when refused
    public static string? Create(string folder, string name, bool isFolder, string text = "")
    {
        var path = Path.Join(folder, name);
        if (!StudioFiles.Create(path, isFolder)) return null;

        return isFolder || text.Length == 0 || StudioFiles.Write(path, text) ? path : null;
    }

    public static string? Rename(string path, string name) => StudioFiles.Rename(path, name);

    // To another writable folder, under its name or a new one; the new path, null when refused
    public static string? Move(string path, string folder, string? name = null)
    {
        if (StudioFiles.Move(path, folder) is not { } moved) return null;

        return name is null || name == Name(moved) ? moved : StudioFiles.Rename(moved, name) ?? moved;
    }

    // Into the trash (remembered so the tree can restore it); inside the trash it is gone for good
    public TrashToken? Delete(string path)
    {
        if (StudioFiles.Delete(path) is not { } token) return null;

        if (_trash.Count < MaxEntries) _trash[StudioFiles.Full(token.Trashed)] = token;

        return token;
    }

    // Back where it was; its path, null when that place is taken now
    public string? Restore(TrashToken token)
    {
        if (!StudioFiles.Restore(token)) return null;

        _ = _trash.Remove(StudioFiles.Full(token.Trashed));
        return token.Original;
    }

    public TrashToken? Trashed(string path) => _trash.GetValueOrDefault(StudioFiles.Full(path));
}

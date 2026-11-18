
namespace Komet.Sdk;

// A problem a build found in a file, on the editor's line (0-based) and column (1-based)
internal readonly record struct BuildIssue(string File, int Line, int Column, bool Error, string Message, string Renderer);

// A build as the screen shows it: good, its problems, skipped (reloads held back), states still pending, files
internal sealed record BuildReport(bool Ok, BuildIssue[] Issues, bool Skipped, bool Pending, string[] Files);

// A good build's copy of a file (history): when, and where its text lies
internal readonly record struct StudioSnapshot(DateTime At, string Path);

// The studio's one way to the files, the projects, the frame's modules and the build: the backend's services
// (Komet.Sdk/Backend: StudioFiles, ShaderProjects, ShaderModules, ShaderBuild) behind the names the
// screen uses. Everything the screen writes goes through here, so the write safety holds in one place.
internal sealed partial class StudioLink(ICoreClientAPI capi)
{
    public const int MaxEntries = 512, MaxProjects = 64;

    public static string DataRoot => StudioFiles.DataRoot;
    public static string AssetsRoot => StudioFiles.GameAssets;
    public static string KometFolder => StudioFiles.KometRoot;
    public static string ProjectsFolder => StudioFiles.Full(ShaderProjects.Root);
    public static string TrashFolder => StudioFiles.TrashRoot;

    // Inside folder (or the folder itself)
    public static bool Within(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    // The project a path belongs to, null outside the projects folder
    public static string? ProjectOf(string path)
    {
        if (!Within(path, ProjectsFolder) || path.Length <= ProjectsFolder.Length + 1) return null;

        var rest = Path.GetRelativePath(ProjectsFolder, path);
        var cut = rest.IndexOfAny(['/', '\\']);
        return cut < 0 ? rest : rest[..cut];
    }

    public static bool IsProjectFolder(string path) =>
        ProjectOf(path) is { } name && PathEquals(path, Path.Join(ProjectsFolder, name));

    public static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A folder that is read-only itself but holds writable things (the way down to ModConfig/komet)
    public static bool Ancestor(string folder) => PathEquals(folder, DataRoot);

    // Folders the studio keeps: never renamed, moved or deleted
    public static bool Fixed(string path) =>
        PathEquals(path, DataRoot) || PathEquals(path, KometFolder) || PathEquals(path, ProjectsFolder) ||
               PathEquals(path, TrashFolder) || PathEquals(path, AssetsRoot) ||
               PathEquals(path, StudioFiles.SettingsFile);

    // The game shader a project copy stands for (shaders/x.fsh, includes/x.fsh in a project), else null
    public static string? GameFileOf(string path)
    {
        return ShaderProjects.GameFileOf(path) is { } key ? ShaderAssets.InstalledPath(key) : null;
    }

    // A game shader's asset path for the backend ("shaders/x.fsh", "shaderincludes/x.ash"), null for anything else
    public static string? Relative(string gameFile) => ShaderAssets.FromDisk(gameFile);

    public static bool IsGameShader(string path) => Relative(path) is not null;

    public static string Name(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    // The path from the Vintagestory folder, its parts for the breadcrumb ("assets" for the game's)
    public static string Display(string path)
    {
        if (Within(path, AssetsRoot))
        {
            return Path.Join("assets", Path.GetRelativePath(AssetsRoot, path)).Replace('\\', '/');
        }

        return Within(path, DataRoot) ? Path.GetRelativePath(DataRoot, path).Replace('\\', '/') : path;
    }
}

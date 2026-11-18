namespace Komet.Sdk;

// The domains shipped with Vintage Story. Keep existing game keys unqualified so project history and picks still work.
internal static class ShaderAssets
{
    public const int FileLimit = 512;
    private static readonly string[] Domains = ["game", "survival", "creative"];
    private static readonly string[] Categories = ["shaders", "shaderincludes"];

    // The asset the game loaded: assets/survival and assets/creative are folders of the "game" domain, not domains
    public static AssetLocation Location(string relative) => new("game", relative);

    // A key as the game's asset key reads it: the folder before the path dropped ("survival/shaders/x.fsh" -> "shaders/x.fsh")
    public static string AssetKey(string key) => Split(key, out _, out var relative) ? relative : key;

    public static string Key(string? domain, string relative) =>
        domain is null or "game" ? relative : domain + "/" + relative;

    public static bool Split(string key, out string domain, out string relative)
    {
        domain = "game";
        relative = key;
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return false;

        var first = key[..slash];
        if (Domains.Contains(first, StringComparer.Ordinal))
        {
            domain = first;
            relative = key[(slash + 1)..];
        }

        if (!relative.StartsWith("shaders/", StringComparison.Ordinal) &&
            !relative.StartsWith("shaderincludes/", StringComparison.Ordinal)) return false;

        return IsSource(relative) && !relative.Contains(':', StringComparison.Ordinal) &&
               !relative.Contains('\\', StringComparison.Ordinal) &&
               !relative.Split('/').Any(part => part is "" or "." or "..");
    }

    public static bool IsSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".vsh" or ".fsh" or ".gsh" or ".ash" or ".glsl";

    public static string? FromDisk(string path)
    {
        if (StudioFiles.GameAssets.Length == 0) return null;

        var relative = Path.GetRelativePath(StudioFiles.GameAssets, path).Replace('\\', '/');
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !Domains.Contains(relative[..slash], StringComparer.Ordinal)) return null;

        return Split(relative, out var domain, out var asset) ? Key(domain, asset) : null;
    }

    public static string? InstalledPath(string key) =>
        StudioFiles.GameAssets.Length > 0 && Split(key, out var domain, out var path)
            ? Path.Join(StudioFiles.GameAssets, domain, path.Replace('/', Path.DirectorySeparatorChar))
            : null;

    public static string[] Folders() =>
        StudioFiles.GameAssets.Length == 0
            ? []
            : [.. Domains.SelectMany(domain => Categories.Select(category =>
                Path.Join(StudioFiles.GameAssets, domain, category))).Where(Directory.Exists)];

    public static string[] Files() =>
        [.. Folders().SelectMany(ShaderProjects.Files).Take(FileLimit)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal)];

    public static string ProgramKey(string program, string ext)
    {
        var relative = "shaders/" + program + ext;
        foreach (var domain in Domains.Bounded(Domains.Length))
        {
            var key = Key(domain, relative);
            if (File.Exists(InstalledPath(key))) return key;
        }

        return relative;
    }
}

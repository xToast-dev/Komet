namespace Komet.Sdk;

// Where shader sources live and what they are: the game's read-only shaders and includes, the working copies under the
// user's pack folder, and which game asset a file stands for. No state.
internal static class ShaderPaths
{
    public const int MaxSource = 48 * 1024;

    private static string GameShaderFolder => Path.Join(GamePaths.AssetsPath, "game", "shaders");
    private static string GameRoot => Path.GetDirectoryName(GameShaderFolder)!;
    private static string IncludeFolder => Path.Join(GameRoot, "shaderincludes");

    public static bool IsStage(string path) =>
        ShaderAssets.IsSource(path);

    public static bool IsJson(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public static EnumerationOptions Recursive(bool recurse) =>
        new()
        {
            RecurseSubdirectories = recurse, MaxRecursionDepth = 4,
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
        };

    // The name an #include line names, between quotes or angle brackets
    public static string? IncludeName(string line)
    {
        var start = line.IndexOfAny(['"', '<']);
        if (start < 0) return null;

        var end = line.IndexOfAny(['"', '>'], start + 1);
        return end > start ? line[(start + 1)..end].Trim() : null;
    }

    // Where an include resolves for a file: beside it, in the project root, in the game's includes or shaders
    public static string? IncludePath(string include, string file, string root)
    {
        var normalized = include.Replace('/', Path.DirectorySeparatorChar);
        foreach (var candidate in (ReadOnlySpan<string>)
                 [
                     Path.Join(Path.GetDirectoryName(file) ?? root, normalized), Path.Join(root, normalized),
                     Path.Join(IncludeFolder, normalized), Path.Join(GameShaderFolder, normalized)
                 ])
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

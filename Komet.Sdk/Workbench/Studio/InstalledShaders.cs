using System.IO.Compression;

namespace Komet.Sdk;

// The shader files of the installed mods for the search: folder mods by their real paths, zipped ones as
// "<zip>!/<entry>" (read-only, read from the archive). Scanned once a session; the mods do not change while running.
internal static class InstalledShaders
{
    private const string Separator = "!/";
    private const int MaxFiles = 2048;

    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal); // file → mod name
    private static readonly Dictionary<string, string> Roots = new(StringComparer.Ordinal); // file → mod root
    private static List<string>? _files;

    public static bool Packed(string path) => path.Contains(".zip" + Separator, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Files(ICoreClientAPI api)
    {
        if (_files is not null) return _files;

        _files = [];
        foreach (var mod in api.ModLoader.Mods.Bounded(1024))
        {
            if (mod.SourcePath is not { Length: > 0 } source || mod.SourceType is not (EnumModSourceType.Folder or EnumModSourceType.ZIP)) continue;

            var name = mod.Info?.Name ?? Path.GetFileNameWithoutExtension(source);
            try
            {
                foreach (var file in Listed(mod.SourceType, source).Bounded(MaxFiles))
                {
                    if (_files.Count >= MaxFiles) break;

                    _files.Add(file);
                    (Owners[file], Roots[file]) = (name, source);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                api.Logger.Warning("Komet studio: the shaders of {0} could not be read: {1}", name, e.Message);
            }
        }

        return _files;
    }

    private static IEnumerable<string> Listed(EnumModSourceType type, string source)
    {
        if (type == EnumModSourceType.Folder)
        {
            var root = Path.Join(source, "assets");
            return Directory.Exists(root)
                ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(ShaderPaths.IsStage)]
                : [];
        }

        using var zip = ZipFile.OpenRead(source);
        return [.. zip.Entries.Select(e => e.FullName.Replace('\\', '/'))
            .Where(e => e.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && ShaderPaths.IsStage(e))
            .Select(e => source + Separator + e)];
    }

    // The mod's name and the file's folder inside it, for the hit's heading
    public static string? Folder(string path)
    {
        if (!Owners.TryGetValue(path, out var name) || !Roots.TryGetValue(path, out var root)) return null;

        var inner = Packed(path) ? path[(root.Length + Separator.Length)..] : Path.GetRelativePath(root, path).Replace('\\', '/');
        var cut = inner.LastIndexOf('/');
        return name + "/" + (cut > 0 ? inner[..(cut + 1)] : "");
    }

    // A zipped entry's text; null with the reason when the archive or entry cannot be read
    public static string? Read(string path, out string error)
    {
        error = "";
        var at = path.IndexOf(".zip" + Separator, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var (archive, inner) = (path[..(at + 4)], path[(at + 4 + Separator.Length)..]);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(inner, StringComparison.Ordinal));
            if (entry is null || entry.Length > StudioLink.MaxText)
            {
                error = IdeText.L(entry is null ? "mod-entry-missing" : "large");
                return null;
            }

            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = e.Message;
            return null;
        }
    }
}

using System.Globalization;

namespace Komet.Sdk;

// The Shader Studio's file tree: the player's Vintagestory data folder (GamePaths.DataPath) and the game's assets (read
// only). Paths are full paths on disk. Everything below the data folder is writable (the folder itself and the game's
// install are not); no link may lead a write out of it. Delete moves into ModConfig/komet/.papierkorb/
// and returns a TrashToken that Restore takes back.
//
// API: List(dir), Read(path), CanWrite(path), Write(path, text), Create(path, folder), Rename(path, name),
// Move(path, intoFolder), Delete(path) -> TrashToken?, Restore(token), Error (the last refusal or failure).
// Any thread for List/Read/CanWrite; writes from the main thread (the projects' state follows them).
internal static class StudioFiles
{
    private const int MaxEntries = 4096, MaxText = 4 << 20, MaxDepth = 64;
    private const string TrashName = ".papierkorb";

    private static readonly StringComparison PathCase =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
    };

    // The last refusal or failure, for the Ausgabe panel
    public static string Error { get; private set; } = "";

    public static string DataRoot => Full(GamePaths.DataPath);
    public static string KometRoot => Path.Join(Full(GamePaths.ModConfig), "komet");
    public static string TrashRoot => Path.Join(KometRoot, TrashName);
    public static string SettingsFile => Path.Join(Full(GamePaths.ModConfig), "komet.json");
    public static string GameAssets => Full(GamePaths.AssetsPath ?? "");

    // A folder's entries, folders first, then by name; empty when it cannot be read
    public static StudioEntry[] List(string folder)
    {
        if (!Inside(folder, out var full) || !Directory.Exists(full)) return [];

        try
        {
            var entries = new DirectoryInfo(full).EnumerateFileSystemInfos("*", Shallow).Take(MaxEntries)
                .Select(info => new StudioEntry(info.FullName, info.Name, info is DirectoryInfo, CanWrite(info.FullName)))
                .OrderBy(e => e.Folder ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
            return [.. entries];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            return [];
        }
    }

    // A file's text; null when it is missing, too large or unreadable
    public static string? Read(string path)
    {
        if (!Inside(path, out var full) || !File.Exists(full)) return null;

        try
        {
            return new FileInfo(full).Length <= MaxText ? File.ReadAllText(full) : Refused("too large");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(e.Message);
        }
    }

    // Everything below the Vintagestory data folder (the folder itself stays); the game's install stays read only
    public static bool CanWrite(string path)
    {
        if (!NotNull(path) || path.Length == 0) return false;

        var full = Full(path);
        return full.Length > 0 && Under(full, DataRoot) && !string.Equals(full, DataRoot, PathCase);
    }

    // Writes a file atomically (a temporary beside it, then moved over)
    public static bool Write(string path, string text)
    {
        if (!Writable(path, out var full) || !NotNull(text)) return false;

        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, text);
            File.Move(temporary, full, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TryDelete(temporary);
            return Failed(e.Message);
        }
    }

    // A new empty file or folder; false when something is there already
    public static bool Create(string path, bool folder)
    {
        if (!Writable(path, out var full)) return false;

        if (File.Exists(full) || Directory.Exists(full)) return Failed("exists");

        try
        {
            if (folder)
            {
                _ = Directory.CreateDirectory(full);
            }
            else
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(e.Message);
        }
    }

    // A new name in the same folder; the new full path, null when refused
    public static string? Rename(string path, string name)
    {
        if (!NotNull(name) || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name is "." or "..")
        {
            Error = "invalid name";
            return null;
        }

        var target = Path.Join(Path.GetDirectoryName(Full(path)) ?? "", name);
        return Relocate(path, target) ? target : null;
    }

    // Into another folder; the new full path, null when refused
    public static string? Move(string path, string folder)
    {
        var target = Path.Join(Full(folder), Path.GetFileName(Full(path).TrimEnd(Path.DirectorySeparatorChar)));
        return Relocate(path, target) ? target : null;
    }

    // Into the trash; the token takes it back. Inside the trash it is gone for good (the UI confirms first).
    public static TrashToken? Delete(string path)
    {
        if (!Writable(path, out var full) || !(File.Exists(full) || Directory.Exists(full))) return null;

        if (Under(full, TrashRoot))
        {
            _ = Erase(full);
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = Path.Join(TrashRoot, stamp + "-" + Path.GetFileName(full));
        return Relocate(full, target) ? new TrashToken(full, target) : null;
    }

    // Back where it was; false when that place is taken now
    public static bool Restore(TrashToken token) => NotNull(token) && Relocate(token.Trashed, token.Original);

    // A full path below the data folder or the game's assets (game assets read only)
    private static bool Inside(string path, out string full)
    {
        full = NotNull(path) && path.Length > 0 ? Full(path) : "";
        return full.Length > 0 && (Under(full, DataRoot) || (GameAssets.Length > 0 && Under(full, GameAssets)));
    }

    internal static bool Under(string full, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
            PathCase) || full.StartsWith(prefix, PathCase);
    }

    internal static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static bool Relocate(string from, string to)
    {
        if (!Writable(from, out var source) || !Writable(to, out var target)) return false;

        if (File.Exists(target) || Directory.Exists(target) || Under(target, source)) return Failed("target exists");

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(e.Message);
        }
    }

    private static bool Writable(string path, out string full)
    {
        full = NotNull(path) ? Full(path) : "";
        if (full.Length == 0 || !CanWrite(full))
        {
            return Failed("not writable: " + path);
        }

        // no link may lead the write out of the data folder
        var at = full;
        for (var depth = 0; depth < MaxDepth && at.Length > DataRoot.Length && Under(at, DataRoot); depth++)
        {
            if ((File.Exists(at) || Directory.Exists(at)) &&
                File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
            {
                return Failed("link: " + at);
            }

            at = Path.GetDirectoryName(at) ?? "";
        }

        return true;
    }

    private static bool Erase(string full)
    {
        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, true);
            }
            else
            {
                File.Delete(full);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(e.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    private static bool Failed(string why)
    {
        Error = why;
        return false;
    }

    private static string? Refused(string why)
    {
        Error = why;
        return null;
    }
}

// One entry of the tree: full path, shown name, folder or file, whether the Studio may write it
internal sealed record StudioEntry(string Path, string Name, bool Folder, bool Writable);

// A deleted entry in the trash: where it was and where it lies now
internal sealed record TrashToken(string Original, string Trashed);

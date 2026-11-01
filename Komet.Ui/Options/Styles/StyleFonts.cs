using System.Runtime.InteropServices;

namespace Komet.Options;

// Makes a style's .ttf/.otf known to Cairo, which selects fonts by family name: fontconfig on Linux (the game's own
// fonts.conf names assets/game/fonts the same way), GDI private fonts on Windows. Elsewhere the game's font stands in.
// A font from the assets is written to the cache first, as both need a file.
internal static partial class StyleFonts
{
    private const int MaxFonts = 64, MaxBytes = 32 << 20, FrPrivate = 0x10;
    private static readonly HashSet<string> Known = []; // full paths registered in this process
    private static bool _noFontconfig, _noGdi;

    public static string CacheFolder => Path.Join(GamePaths.Cache, "komet-fonts");

    // true when the file is (or already was) registered
    public static bool Register(string path, ILogger? logger)
    {
        if (!NotNull(path)) return false;
        if (Known.Contains(path)) return true;
        if (Known.Count >= MaxFonts || !File.Exists(path)) return false;
        var done = OperatingSystem.IsWindows() ? Gdi(path) : Fontconfig(path);
        if (done) _ = Known.Add(path);
        else logger?.Warning("Komet styles: the font file {0} could not be registered; the game's font stands in", path);
        return done;
    }

    // An asset font's bytes as a cache file of the same name, rewritten only when its size changed
    public static string? Cached(string name, byte[] data, ILogger? logger)
    {
        if (!NotNull(name) || !NotNull(data) || !Assert(data.Length <= MaxBytes) || data.Length == 0) return null;
        var path = Path.Join(CacheFolder, Path.GetFileName(name));
        try
        {
            _ = Directory.CreateDirectory(CacheFolder);
            if (!File.Exists(path) || new FileInfo(path).Length != data.Length) File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.Warning("Komet styles: could not write the font {0}: {1}", path, e.Message);
            return null;
        }
    }

    private static bool Fontconfig(string path)
    {
        if (_noFontconfig) return false;
        try
        {
            return FcConfigAppFontAddFile(IntPtr.Zero, path) != 0; // the current configuration, the one Cairo asks
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _noFontconfig = true;
            return false;
        }
    }

    private static bool Gdi(string path)
    {
        if (_noGdi) return false;
        try
        {
            return AddFontResourceEx(path, FrPrivate, IntPtr.Zero) > 0 || Fontconfig(path); // a Cairo built on fontconfig
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _noGdi = true;
            return Fontconfig(path);
        }
    }

    [LibraryImport("libfontconfig.so.1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int FcConfigAppFontAddFile(IntPtr config, string file);

    [LibraryImport("gdi32.dll", EntryPoint = "AddFontResourceExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AddFontResourceEx(string name, uint flags, IntPtr reserved);
}

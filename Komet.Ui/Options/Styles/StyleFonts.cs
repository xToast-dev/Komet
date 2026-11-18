using System.Runtime.InteropServices;
using System.Text;
using Context = Cairo.Context;
using Format = Cairo.Format;
using FontSlant = Cairo.FontSlant;
using FontWeight = Cairo.FontWeight;
using ImageSurface = Cairo.ImageSurface;

namespace Komet.Options;

// Makes a style's .ttf/.otf known to Cairo, which selects fonts by family name: fontconfig on Linux (the game's own
// fonts.conf names assets/game/fonts the same way), GDI private fonts on Windows. Elsewhere the game's font stands in.
// A font from the assets is written to the cache first, as both need a file.
// A style's family stands back for the game's font (the player's choice, Chinese or Thai ones per language) where it
// cannot draw the language: always for scripts no style font ships (Cairo on Windows draws a missing glyph with a
// placeholder, its index tells nothing), else when one of the language's letters has no glyph in it.
internal static partial class StyleFonts
{
    private const int GlyphSize = 24; // cairo_glyph_t: unsigned long index (4 or 8 bytes, little-endian), double x, y
    private const int MaxGlyphs = 256;

    // Letters of a locale's script beyond ASCII, one family's glyphs tested per locale
    private const string Cyrillic = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдежзийклмнопрстуфхцчшщъыьэюяЇїІіЄєҐґЎў",
        Greek = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩαβγδεζηθικλμνξοπρστυφχψωάέήίόύώ",
        Latin = "àáâäãåāăąçćčďđèéêëēęěğìíîïīıłńňñòóôöõøőřśşšťùúûüůűýÿźżžßÀÁÄÇÉÖÜŁŚŻ",
        Vietnamese = "ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ";

    private const int MaxFonts = 64, MaxBytes = 32 << 20, FrPrivate = 0x10;

    // Locales whose script no style font covers: the game picks a system font for them (EngineOptions.LocalFonts)
    private static readonly string[] OwnScripts = ["zh", "ja", "ko", "th", "ar", "he", "fa", "hi", "ka", "hy"];

    private static readonly Dictionary<string, bool> Reading = new(StringComparer.Ordinal);
    private static IntPtr _cairo;
    private static bool _noCairo;
    private static readonly HashSet<string> Known = []; // full paths registered in this process
    private static bool _noFontconfig, _noGdi;

    private static TextToGlyphs? _toGlyphs;
    private static GlyphFree? _free;

    private static string CacheFolder => Path.Join(GamePaths.Cache, "komet-fonts");

    // true when the file is (or already was) registered
    public static bool Register(string path, ILogger? logger)
    {
        if (!NotNull(path)) return false;

        if (Known.Contains(path)) return true;

        if (Known.Count >= MaxFonts || !File.Exists(path)) return false;

        var done = OperatingSystem.IsWindows() ? Gdi(path) : Fontconfig(path);
        if (done)
        {
            _ = Known.Add(path);
        }
        else
        {
            logger?.Warning("Komet styles: the font file {0} could not be registered; the game's font stands in", path);
        }

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

    // Whether family draws the locale's letters; true when nothing can tell (no Cairo to ask)
    public static bool Reads(string family, string? locale)
    {
        if (!NotNull(family)) return false;

        var lang = (locale ?? "en").Trim().ToLowerInvariant();
        if (OwnScript(lang)) return false;

        var sample = Sample(lang);
        if (sample.Length == 0) return true;

        var key = family + "\n" + lang;
        if (Reading.TryGetValue(key, out var reads)) return reads;

        try
        {
            reads = Covers(family, sample) ?? true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            (reads, _noCairo) = (true, true); // the binding finds no Cairo (tests): nothing to tell
        }

        Reading[key] = reads;
        return reads;
    }

    internal static bool OwnScript(string lang) =>
        Array.Exists(OwnScripts, s => lang == s || lang.StartsWith(s + "-", StringComparison.Ordinal));

    // English needs no test: ASCII is in every font
    internal static string Sample(string lang)
    {
        var main = lang.Split('-')[0];
        return main switch
        {
            "en" or "" => "",
            "ru" or "uk" or "be" or "bg" or "sr" or "mk" or "kk" or "mn" => Cyrillic,
            "el" => Greek,
            "vi" => Vietnamese,
            _ => Latin
        };
    }

    // null when Cairo cannot be asked
    private static bool? Covers(string family, string sample)
    {
        if (_noCairo || (_toGlyphs is null && !Bind())) return null;

        using var surface = new ImageSurface(Format.Argb32, 1, 1);
        using var context = new Context(surface);
        context.SelectFontFace(family, FontSlant.Normal, FontWeight.Normal);
        context.SetFontSize(12);
        var font = context.GetScaledFont(); // the context's own, not to destroy
        var utf8 = Encoding.UTF8.GetBytes(sample);
        var (glyphs, count) = (IntPtr.Zero, 0); // null: Cairo allocates
        if (_toGlyphs!(font.Handle, 0, 0, utf8, utf8.Length, ref glyphs, ref count, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero) != 0 ||
            glyphs == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            for (var i = 0; i < Math.Min(count, MaxGlyphs); i++)
            {
                if (Marshal.ReadInt32(glyphs, i * GlyphSize) == 0)
                {
                    return false; // glyph 0: none in the font
                }
            }

            return true;
        }
        finally
        {
            _free!(glyphs);
        }
    }

    private static bool Bind()
    {
        var (toGlyphs, free) = (Export("cairo_scaled_font_text_to_glyphs"), Export("cairo_glyph_free"));
        if (toGlyphs == IntPtr.Zero || free == IntPtr.Zero) return false;

        _toGlyphs = Marshal.GetDelegateForFunctionPointer<TextToGlyphs>(toGlyphs);
        _free = Marshal.GetDelegateForFunctionPointer<GlyphFree>(free);
        return true;
    }

    // The game's own Cairo, already loaded (the names the game's CairoSharp loads it by): the same library, the same handles
    private static IntPtr Export(string name)
    {
        if (_noCairo) return IntPtr.Zero;

        if (_cairo == IntPtr.Zero)
        {
            foreach (var lib in (ReadOnlySpan<string>)
                     ["libcairo-2", "libcairo-2.dll", "libcairo.so.2", "libcairo.2.dylib", "cairo.dll"])
            {
                if (NativeLibrary.TryLoad(lib, typeof(Context).Assembly, null, out _cairo)) break;
            }
        }

        if (_cairo != IntPtr.Zero && NativeLibrary.TryGetExport(_cairo, name, out var address)) return address;

        _noCairo = true;
        return IntPtr.Zero;
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
            return AddFontResourceEx(path, FrPrivate, IntPtr.Zero) > 0 ||
                   Fontconfig(path); // a Cairo built on fontconfig
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TextToGlyphs(IntPtr font, double x, double y, byte[] utf8, int length, ref IntPtr glyphs,
        ref int count, IntPtr clusters, IntPtr clusterCount, IntPtr clusterFlags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlyphFree(IntPtr glyphs);
}

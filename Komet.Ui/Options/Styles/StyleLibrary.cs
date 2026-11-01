using System.Globalization;
using Komet.Api;
using Komet.Hud;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Komet.Core;
using Komet.Rendering;

namespace Komet.Options;

// Every style there is, in the order a later one wins: the assets of all mods (Komet's own first), the folders under
// ModConfig/komet/styles, then KometStyles.Register. A style names its Base; the chain is merged key by key, and files
// resolve where the style that named them lives, so a style inherits its base's shader and fonts as they are.
internal sealed class StyleLibrary(ICoreClientAPI capi) : IDisposable
{
    public const string AssetPath = "config/komet-styles/", StyleFile = "style.json";
    private const int MaxDepth = 8, MaxShader = 64 << 10, MaxImage = 4 << 20, MaxFound = KometStyles.MaxStyles * 2;

    private readonly List<KometStyle> _found = [];
    private readonly Dictionary<string, StyleSheet> _sheets = [];
    private readonly List<StyleSheet> _retired = []; // a worker may still paint a retired logo: disposed with the library
    private bool _loaded;

    public static string UserFolder => Path.Join(GamePaths.ModConfig, "komet", "styles");
    public static string UserLogo => Path.Join(GamePaths.ModConfig, "komet", "logo.png");

    // The styles to choose from: one per id, in the order found
    public (string Id, string Name)[] Choices()
    {
        Load();
        List<(string, string)> choices = [];
        foreach (var style in _found.Bounded(MaxFound))
        {
            var at = choices.FindIndex(c => c.Item1 == style.Id);
            var entry = (style.Id, style.Name.Length > 0 ? ShaderPacks.Said(style.Name) : style.Id); // a lang key or the name
            if (at >= 0) choices[at] = entry;
            else choices.Add(entry);
        }

        return [.. choices];
    }

    public StyleSheet Sheet(string id)
    {
        Load();
        if (!NotNull(id)) return StyleSheet.Fallback();
        id = StyleSheet.Current(id);
        var at = Latest(id, _found.Count);
        if (at < 0) (id, at) = (StyleSheet.Pragmatic, Latest(StyleSheet.Pragmatic, _found.Count)); // a style that went away
        if (_sheets.TryGetValue(id, out var known)) return known;
        var sheet = at < 0 ? StyleSheet.Fallback() : Built(Merged(at));
        if (at >= 0) _sheets[id] = sheet; // a stand-in is not kept: the style may still come
        return sheet;
    }

    // Everything found again (a designer's edit shows without a restart); sheets drawn so far are kept until Dispose
    public void Reload()
    {
        foreach (var sheet in _sheets.Values.Bounded(MaxFound)) _retired.Add(sheet);
        _sheets.Clear();
        _found.Clear();
        _loaded = false;
        Load();
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        FromAssets();
        FromFolders();
        for (var i = 0; i < Math.Min(KometStyles.Count, KometStyles.MaxStyles); i++) Add(KometStyles.At(i));
        _loaded = _found.Count > 0; // asked before the assets were there: asked again later
    }

    private void FromAssets()
    {
        var assets = capi.Assets?.GetMany(AssetPath, null, true);
        if (assets is null || !Assert(assets.Count < 4096)) return;
        foreach (var asset in assets.Bounded(4096))
        {
            var path = asset.Location.Path;
            if (!path.EndsWith("/" + StyleFile, StringComparison.Ordinal)) continue;
            var id = path[AssetPath.Length..^(StyleFile.Length + 1)];
            if (Parsed(asset.ToText(), id, asset.Location.ToString()) is not { } style) continue;
            style.Domain = asset.Location.Domain;
            Add(style);
        }
    }

    private void FromFolders()
    {
        if (!Directory.Exists(UserFolder)) return;
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(UserFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger?.Warning("Komet styles: {0} cannot be read: {1}", UserFolder, e.Message);
            return;
        }

        Array.Sort(folders, StringComparer.Ordinal);
        foreach (var folder in folders.Bounded(KometStyles.MaxStyles))
        {
            var file = Path.Join(folder, StyleFile);
            if (!File.Exists(file)) continue;
            if (Parsed(Read(file), Path.GetFileName(folder).ToLowerInvariant(), file) is not { } style) continue;
            (style.Folder, style.Domain) = (folder, null);
            Add(style);
        }
    }

    private static string Read(string file)
    {
        try
        {
            return new FileInfo(file).Length < MaxShader ? File.ReadAllText(file) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // A file's style; its id is the folder's unless it names one. Errors name the file and the line.
    private KometStyle? Parsed(string json, string id, string where)
    {
        if (!Assert(id.Length > 0)) return null;
        try
        {
            var style = JsonConvert.DeserializeObject<KometStyle>(json);
            if (style is null) return null;
            if (style.Id.Length == 0) style.Id = id;
            return style;
        }
        catch (JsonException e)
        {
            capi.Logger?.Warning("Komet styles: {0} is not a valid style: {1}", where, e.Message);
            return null;
        }
    }

    private void Add(KometStyle style)
    {
        if (!NotNull(style) || style.Id.Length == 0) return;
        if (_found.Count < MaxFound) _found.Add(style);
        else capi.Logger?.Warning("Komet styles: more than {0} styles; '{1}' is left out", MaxFound, style.Id);
    }

    // The last style with this id found before index `before`
    private int Latest(string id, int before)
    {
        for (var k = 0; k < Math.Min(before, MaxFound); k++)
            if (_found[before - 1 - k].Id == id)
                return before - 1 - k;
        return -1;
    }

    // The style at index merged over its Base chain; a base naming the style's own id is the definition found before it
    internal KometStyle Merged(int index)
    {
        if (!Index(index, _found.Count)) return new KometStyle();
        List<int> chain = [index];
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var style = _found[chain[^1]];
            var name = style.Base is { } named ? StyleSheet.Current(named) : null;
            name ??= (style.Id is StyleSheet.Pragmatic ? null : StyleSheet.Pragmatic);
            if (name is null) break;
            var at = Latest(name, name == style.Id ? chain[^1] : _found.Count);
            if (at < 0 || chain.Contains(at))
            {
                capi.Logger?.Warning("Komet styles: '{0}' names the base '{1}', which is missing or loops", style.Id, name);
                break;
            }

            chain.Add(at);
        }

        var merged = new KometStyle { Id = _found[index].Id, Name = _found[index].Name, Author = _found[index].Author };
        for (var k = 0; k < Math.Min(chain.Count, MaxDepth + 1); k++) Over(merged, _found[chain[chain.Count - 1 - k]]);
        return merged;
    }

    // top's values over merged; its file names made absolute where top lives
    private static void Over(KometStyle merged, KometStyle top)
    {
        foreach (var (k, v) in top.Colors.Bounded(256)) merged.Colors[k] = v;
        foreach (var (k, v) in top.Sizes.Bounded(256)) merged.Sizes[k] = v;
        foreach (var (k, v) in top.Fonts.Bounded(64)) merged.Fonts[k] = v;
        foreach (var file in top.FontFiles.Bounded(16))
            if (Where(top, file) is { } font && !merged.FontFiles.Contains(font))
                merged.FontFiles.Add(font);
        if (top.Logo is { Length: > 0 } logo) merged.Logo = Where(top, logo);
        if (top.BackgroundSource is { Length: > 0 } source) (merged.BackgroundSource, merged.Background) = (source, "shader");
        else if (top.Background is { Length: > 0 } background)
            (merged.Background, merged.BackgroundSource) = (background is "blur" or "none" ? background : Where(top, background), null);
    }

    // A file a style names, as an absolute path, or an asset location ("domain:path") for a style from the assets
    internal static string? Where(KometStyle style, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Assert(name.Length < 1024)) return null;
        if (name.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(name)) return name; // already an asset
        if (style.Folder is { } folder) return Path.GetFullPath(Path.Join(folder, name));
        if (style.Domain is { } domain) return domain + ":" + AssetPath + style.Id + "/" + name;
        return Path.IsPathRooted(name) ? name : null;
    }

    private StyleSheet Built(KometStyle merged)
    {
        foreach (var font in merged.FontFiles.Bounded(32)) Font(font);
        var shader = merged.BackgroundSource;
        if (shader is null && merged.Background is { } file && file is not ("blur" or "none") && Bytes(file, MaxShader) is { } text)
            shader = System.Text.Encoding.UTF8.GetString(text);
        return StyleSheet.From(merged, shader, Logo(merged.Logo), capi.Logger);
    }

    // A font file registered; one from the assets through the cache
    private void Font(string font)
    {
        if (Path.IsPathRooted(font))
        {
            _ = StyleFonts.Register(font, capi.Logger);
            return;
        }

        if (Bytes(font, MaxImage * 8) is { } data && StyleFonts.Cached(font[(font.LastIndexOf('/') + 1)..], data, capi.Logger) is { } cached)
            _ = StyleFonts.Register(cached, capi.Logger);
    }

    // The style's logo, else the player's ModConfig/komet/logo.png, else Komet's icon
    private Cairo.ImageSurface? Logo(string? named)
    {
        var bytes = (named is null ? null : Bytes(named, MaxImage)) ?? Bytes(UserLogo, MaxImage) ??
            Bytes("komet:" + AssetPath + "logo.png", MaxImage);
        if (bytes is null) return null;
        try
        {
            using var bitmap = new BitmapExternal(bytes, bytes.Length, capi.Logger);
            return bitmap.Width > 0 && bitmap.Height > 0 ? getImageSurfaceFromAsset(bitmap) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            capi.Logger?.Warning("Komet styles: the logo {0} could not be read: {1}", named ?? UserLogo, e.Message);
            return null;
        }
    }

    // A file's bytes from disk or the assets; null when missing or bigger than max
    private byte[]? Bytes(string where, int max)
    {
        try
        {
            if (Path.IsPathRooted(where))
                return File.Exists(where) && new FileInfo(where).Length <= max ? File.ReadAllBytes(where) : null;
            var data = capi.Assets?.TryGet(new AssetLocation(where))?.Data;
            return data is { Length: > 0 } && data.Length <= max ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            capi.Logger?.Warning("Komet styles: {0} cannot be read: {1}", where, e.Message);
            return null;
        }
    }

    // A copy of the style as a folder of its own: every value written out, its shader beside it. The new folder's path.
    public string? Template(string id)
    {
        var sheet = Sheet(id);
        var (name, folder) = (sheet.Id + "-copy", Path.Join(UserFolder, sheet.Id + "-copy"));
        for (var n = 2; n < 100 && Directory.Exists(folder); n++)
            (name, folder) = (sheet.Id + "-copy" + n, Path.Join(UserFolder, sheet.Id + "-copy" + n));

        var json = new JObject
        {
            ["id"] = name, ["name"] = sheet.Name + " (" + KometText.Translate("settings-style-copy") + ")", ["base"] = sheet.Id,
            ["colors"] = new JObject(StyleSheet.ColorKeys.Select(k => new JProperty(k, Hex(sheet[k])))),
            ["sizes"] = new JObject(StyleSheet.SizeKeys.Select(k => new JProperty(k, sheet.Size(k)))),
            ["fonts"] = new JObject(StyleSheet.FontKeys.Select(k => new JProperty(k, sheet.Font(k) ?? "")))
        };
        if (sheet.Shader is not null) json["background"] = "background.fsh";
        try
        {
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Join(folder, StyleFile), json.ToString(Formatting.Indented));
            if (sheet.Shader is { } shader) File.WriteAllText(Path.Join(folder, "background.fsh"), shader);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger?.Warning("Komet styles: the copy {0} could not be written: {1}", folder, e.Message);
            return null;
        }

        Reload();
        return folder;
    }

    internal static string Hex(Rgba c) => Finite(c.A)
        ? string.Create(CultureInfo.InvariantCulture, $"#{B(c.R):X2}{B(c.G):X2}{B(c.B):X2}{B(c.A):X2}")
        : "#FFFFFFFF";

    private static int B(double channel) => (int)Math.Round(Math.Clamp(channel, 0, 1) * 255);

    public void Dispose()
    {
        foreach (var sheet in _sheets.Values.Bounded(MaxFound)) _retired.Add(sheet);
        foreach (var sheet in _retired.Bounded(MaxFound * 64)) sheet.Release();
        _retired.Clear();
        _sheets.Clear();
    }
}

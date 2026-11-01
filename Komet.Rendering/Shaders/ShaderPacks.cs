using Komet.Api;
using Newtonsoft.Json;
using System.Globalization;

namespace Komet.Rendering;

// Shader packs without code (docs/site/articles/komet-json.md): a komet-shader.json beside a mod's shaders (assets/<domain>/shaders/) or in
// ModConfig/komet/shaders/<id>/ declares settings and passes. Each pack gets a page in Komet's options; a setting reaches
// the game's shaders as a #define (reloaded on change) and/or a uniform (live), and a pass's fragment file is drawn
// through KometShaders. The player's values are kept in ModConfig/komet/shader-settings.json (ShaderState).
internal static class ShaderPacks
{
    public const string PackFile = "komet-shader.json", AssetPath = "shaders/" + PackFile;
    private const int MaxPacks = 32, MaxSettings = 40, MaxFile = 256 << 10;
    private static readonly JsonSerializerSettings Lenient = new() { NullValueHandling = NullValueHandling.Ignore };

    // komet-shader.json
    internal sealed class Pack
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("author")] public string? Author { get; set; }
        [JsonProperty("programs")] public string[] Programs { get; set; } = []; // the defines' programs; none: every program
        [JsonProperty("settings")] public List<Setting> Settings { get; set; } = [];
        [JsonProperty("passes")] public List<PassSpec> Passes { get; set; } = [];
        [JsonIgnore] public string Where { get; set; } = ""; // asset "domain:path/" or folder, with a trailing separator
        [JsonIgnore] public bool Declared { get; set; } // from a mod's config/komet.json
    }

    internal sealed class Setting
    {
        [JsonProperty("key")] public string Key { get; set; } = "";
        [JsonProperty("label")] public string? Label { get; set; }
        [JsonProperty("hint")] public string? Hint { get; set; }
        [JsonProperty("type")] public string Type { get; set; } = "switch"; // switch, slider, choice, group
        [JsonProperty("min")] public double Min { get; set; }
        [JsonProperty("max")] public double Max { get; set; } = 1;
        [JsonProperty("step")] public double Step { get; set; } = 0.1;
        [JsonProperty("unit")] public string? Unit { get; set; }
        [JsonProperty("options")] public string[] Options { get; set; } = [];
        [JsonProperty("default")] public double Default { get; set; }
        [JsonProperty("define")] public string? Define { get; set; }
        [JsonProperty("uniform")] public string? Uniform { get; set; }
    }

    internal sealed class PassSpec
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("fragment")] public string Fragment { get; set; } = "";
        [JsonProperty("order")] public int Order { get; set; }
        [JsonProperty("when")] public string? When { get; set; } // a switch's key: drawn while it is on
    }

    private static readonly List<IDisposable> Held = [];
    private static ICoreClientAPI? _api;
    private static readonly Dictionary<string, Dictionary<string, double>> Values = [];
    private static readonly Dictionary<string, Pack> ByMod = []; // "shaderpack:<id>" → the pack, for the overview page

    internal const string ModPrefix = Rendering.ModShaders.PackPrefix;

    // A pack's name, author, settings and where it came from, by the mod id its registrations carry
    internal static (string Name, string? Author, int Settings, string Where)? About(string mod)
    {
        if (!NotNull(mod) || !ByMod.TryGetValue(mod, out var pack)) return null;
        return (Said(pack.Name ?? pack.Id!), pack.Author, pack.Settings.Count(s => s.Type != "group"), pack.Where);
    }

    public static string UserFolder => Path.Join(GamePaths.ModConfig, "komet", "shaders");

    // A path as the player finds it, from VintagestoryData on (the full one fills the detail panel)
    internal static string Shown(string path) =>
        NotNull(path) && GamePaths.DataPath is { Length: > 0 } data && path.StartsWith(data, StringComparison.Ordinal)
            ? Path.GetFileName(data.TrimEnd('/', '\\')) + path[data.TrimEnd('/', '\\').Length..]
            : path;

    // Packs load once the world's assets are there (LevelFinalize), as the other mods' pages are added
    public static void Hook(ICoreClientAPI api)
    {
        _api = api;
        if (!NotNull(api) || !NotNull(api.Event)) return;
        api.Event.LevelFinalize += Loaded;
        Links.ShadersHooked(api);
    }

    public static void Unhook()
    {
        if (_api?.Event is { } events) events.LevelFinalize -= Loaded;
        Links.ShadersUnhooked();
        _api = null;
        Clear();
    }

    private static void Loaded()
    {
        if (_api is not { } api || !NotNull(api.Assets)) return;
        Links.ShadersLoaded(api); // first: the Ui's overview page heads the Shaders section
        Load(api);
    }

    // Every pack found registered with its page
    public static void Load(ICoreClientAPI api)
    {
        Clear();
        if (!NotNull(api)) return;
        var packs = new List<Pack>();
        FromAssets(api, packs);
        FromManifests(api, packs);
        FromFolders(api, packs);
        foreach (var pack in packs.Bounded(MaxPacks)) Register(api, pack);
    }

    public static void Clear()
    {
        foreach (var held in Held.Bounded(KometShaders.MaxDefines + KometShaders.MaxUniforms + KometShaders.MaxPasses)) held.Dispose();
        Held.Clear();
        Values.Clear();
        ByMod.Clear();
    }

    // One pack per mod, beside its shaders: assets/<domain>/shaders/komet-shader.json, its id the domain
    private static void FromAssets(ICoreClientAPI api, List<Pack> packs)
    {
        foreach (var asset in (api.Assets?.GetMany(AssetPath, null, true) ?? []).Bounded(MaxPacks))
        {
            if (asset.Location.Path != AssetPath || packs.Count >= MaxPacks) continue;
            if (Parsed(api, asset.ToText(), asset.Location.Domain, asset.Location.ToString()) is not { } pack) continue;
            pack.Where = asset.Location.Domain + ":shaders/";
            packs.Add(pack);
        }
    }

    // A mod declared for Komet (assets/<domain>/config/komet.json): its settings and passes are a pack named after the
    // domain, its pass files named from assets/<domain>/ ("shaders/grade.fsh")
    private static void FromManifests(ICoreClientAPI api, List<Pack> packs)
    {
        foreach (var asset in (api.Assets?.GetMany(ShaderMods.Manifest, null, true) ?? []).Bounded(MaxPacks))
        {
            if (asset.Location.Path != ShaderMods.Manifest || packs.Count >= MaxPacks || packs.Exists(p => p.Id == asset.Location.Domain)) continue;
            if (Parsed(api, asset.ToText(), asset.Location.Domain, asset.Location.ToString()) is not { } pack ||
                pack.Settings.Count + pack.Passes.Count == 0) continue;
            (pack.Id, pack.Where, pack.Declared) = (asset.Location.Domain, asset.Location.Domain + ":", true);
            if (api.ModLoader?.GetMod(pack.Id)?.Info is { } info) // name and author from modinfo.json unless komet.json says
                (pack.Name, pack.Author) = (pack.Name ?? info.Name, pack.Author ?? (info.Authors is { Count: > 0 } a ? string.Join(", ", a) : null));
            packs.Add(pack);
        }
    }

    private static void FromFolders(ICoreClientAPI api, List<Pack> packs)
    {
        if (!Directory.Exists(UserFolder)) return;
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(UserFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet shaders: {0} cannot be read: {1}", UserFolder, e.Message);
            return;
        }

        Array.Sort(folders, StringComparer.Ordinal);
        foreach (var folder in folders.Bounded(MaxPacks))
        {
            var file = Path.Join(folder, PackFile);
            if (!File.Exists(file) || packs.Count >= MaxPacks) continue;
            if (Parsed(api, Read(file), Path.GetFileName(folder).ToLowerInvariant(), file) is not { } pack) continue;
            pack.Where = folder + Path.DirectorySeparatorChar;
            packs.Add(pack);
        }
    }

    // A pack from its file; its id is the folder's unless it names one. Errors name the file and the line.
    internal static Pack? Parsed(ICoreClientAPI? api, string json, string folderId, string file)
    {
        if (!NotNull(json) || !NotNull(folderId) || json.Length == 0) return null;
        try
        {
            // "settings": null and the like keep the initializers; null items go (both threw in Register)
            var pack = JsonConvert.DeserializeObject<Pack>(json, Lenient);
            if (pack is null) return null;
            pack.Settings = [.. pack.Settings.OfType<Setting>()]; // OfType drops the nulls
            pack.Passes = [.. pack.Passes.OfType<PassSpec>()];
            pack.Programs = [.. pack.Programs.OfType<string>()];
            pack.Id = string.IsNullOrWhiteSpace(pack.Id) ? folderId : pack.Id.Trim().ToLowerInvariant();
            if (pack.Settings.Count > MaxSettings) pack.Settings.RemoveRange(MaxSettings, pack.Settings.Count - MaxSettings);
            return Assert(pack.Id.Length > 0) ? pack : null;
        }
        catch (JsonException e)
        {
            api?.Logger?.Warning("Komet shaders: {0} is not a valid komet-shader.json: {1}", file, e.Message);
            return null;
        }
    }

    // The page, defines, uniforms and passes of one pack
    private static void Register(ICoreClientAPI api, Pack pack)
    {
        if (pack.Id is not { Length: > 0 } id) return;
        var mod = ModPrefix + id;
        ByMod[mod] = pack;
        Values[id] = Stored(api, id, pack);
        var page = KometOptions.Page("shader-" + id, Said(pack.Name ?? id), Lang.Get("komet:settings-section-shaders"));
        // how it is bound in, above its settings: the mod talks to Komet itself, or it is a pack from the player's folder
        _ = page.Group(Lang.Get(pack.Declared ? "komet:settings-page-speaks" : "komet:settings-page-pack"));
        foreach (var setting in pack.Settings.Bounded(MaxSettings))
        {
            if (setting.Type == "group")
            {
                _ = page.Group(Said(setting.Label ?? setting.Key));
                continue;
            }

            if (setting.Key.Length == 0) continue;
            Row(api, page, pack, setting);
            var key = setting.Key;
            if (setting.Define is { Length: > 0 } define)
                Held.Add(KometShaders.Define(mod, define, () => Text(Value(id, key), setting.Type), pack.Programs));
            if (setting.Uniform is { Length: > 0 } uniform)
                Held.Add(KometShaders.Uniform(mod, uniform, () => (float)Value(id, key)));
        }

        foreach (var spec in pack.Passes.Bounded(KometShaders.MaxPasses)) Pass(api, pack, mod, spec);
    }

    private static void Row(ICoreClientAPI api, OptionPage page, Pack pack, Setting setting)
    {
        var (id, key, label, hint) = (pack.Id!, setting.Key, Said(setting.Label ?? setting.Key), setting.Hint is null ? null : Said(setting.Hint));
        Action<double> set = v =>
        {
            // a page left from a pack the last reload no longer found (folder or id changed): nothing to set, no throw
            if (!Values.TryGetValue(id, out var values)) return;
            values[key] = v;
            Store(api, id);
            if (setting.Define is { Length: > 0 }) KometShaders.Reload(); // a define is compiled in
        };
        _ = setting.Type switch
        {
            "slider" when setting.Max > setting.Min && setting.Step > 0 =>
                page.Slider(label, setting.Min, setting.Max, setting.Step, () => Value(id, key), set, setting.Unit ?? "", hint),
            "choice" when setting.Options.Length > 0 =>
                page.Choice(label, [.. setting.Options.Select(Said)], () => (int)Value(id, key), i => set(i), hint),
            _ => page.Switch(label, () => Value(id, key) >= 0.5, on => set(on ? 1 : 0), hint)
        };
        _ = page.Default(setting.Default);
    }

    private static void Pass(ICoreClientAPI api, Pack pack, string mod, PassSpec spec)
    {
        if (!NotNull(spec) || spec.Id.Length == 0 || spec.Fragment.Length == 0) return;
        var source = Source(api, pack, spec.Fragment);
        if (source is null)
        {
            api.Logger?.Warning("Komet shaders: pack {0} names the pass file {1}, which is not there", pack.Id, spec.Fragment);
            return;
        }

        var (id, when) = (pack.Id!, spec.When);
        var pass = new KometPass(mod, spec.Id, KometPassStage.Final, source)
        {
            Order = spec.Order,
            Enabled = when is { Length: > 0 } ? () => Value(id, when) >= 0.5 : null
        };
        foreach (var setting in pack.Settings.Bounded(MaxSettings))
            if (setting.Uniform is { Length: > 0 } uniform)
            {
                // one array per uniform, refilled per draw: ModPasses uploads it at once
                var (key, cell) = (setting.Key, new float[1]);
                pass.Uniforms[uniform] = () =>
                {
                    cell[0] = (float)Value(id, key);
                    return cell;
                };
            }

        Held.Add(KometShaders.AddPass(pass));
    }

    // A file next to the pack's json, from the assets or the folder
    private static string? Source(ICoreClientAPI api, Pack pack, string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name)) return null;
        if (pack.Where.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(pack.Where))
            return api.Assets?.TryGet(new AssetLocation(pack.Where + name))?.ToText();
        var file = Path.Join(pack.Where, name);
        return File.Exists(file) ? Read(file) : null;
    }

    // A pack's or style's text: a lang key (domain:key) in the player's language, else as written
    internal static string Said(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Assert(text.Length < 4096)) return text ?? "";
        return text.Contains(':', StringComparison.Ordinal) && Lang.GetIfExists(text) is { Length: > 0 } said ? said : text;
    }

    internal static string Text(double value, string type)
    {
        if (!Finite(value) || !NotNull(type)) return "0";
        return type is "switch" or "choice" ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0#####", CultureInfo.InvariantCulture);
    }

    private static double Value(string pack, string key)
    {
        return Values.TryGetValue(pack, out var values) && values.TryGetValue(key, out var v) ? v : 0;
    }

    private static Dictionary<string, double> Stored(ICoreClientAPI api, string id, Pack pack)
    {
        var stored = ShaderState.Of(api).Values.GetValueOrDefault(id);
        var values = new Dictionary<string, double>();
        foreach (var setting in pack.Settings.Bounded(MaxSettings))
            if (setting.Key.Length > 0 && setting.Type != "group")
                values[setting.Key] = stored is not null && stored.TryGetValue(setting.Key, out var v) && Finite(v) ? v : setting.Default;
        return values;
    }

    private static void Store(ICoreClientAPI api, string id)
    {
        if (!Values.TryGetValue(id, out var values)) return;
        ShaderState.Of(api).Values[id] = new Dictionary<string, double>(values);
        ShaderState.Store(api);
    }

    private static string Read(string file)
    {
        try
        {
            return NotNull(file) && new FileInfo(file).Length < MaxFile ? File.ReadAllText(file) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

// What the player chose for shader packs and shader mods, in one file beside Komet's other settings
// (ModConfig/komet/shader-settings.json): which are switched off, and each pack's values. The shaders folder keeps only
// packs. Files of earlier builds (shaders/off.json, shaders/<id>.values.json) move in once and are deleted.
internal static class ShaderState
{
    // An adapter's level that follows Komet's preset (ModAdapters names it too)
    public const string FollowKomet = "komet";

    private const string FileName = "shader-settings.json";
    private const int MaxOld = 64;

    internal sealed class Saved
    {
        [JsonProperty("off")] public List<string> Off { get; set; } = [];
        [JsonProperty("values")] public Dictionary<string, Dictionary<string, double>> Values { get; set; } = [];
        // Shader mods replacing game shaders the player switched on; the others are held back (ShaderMods.HoldBack)
        [JsonProperty("on")] public List<string> On { get; set; } = [];

        // Mods declared for Komet (config/komet.json) that the player switched off; the others run
        [JsonProperty("stopped")] public List<string> Stopped { get; set; } = [];

        // Per mod adapter (ModAdapters): the level ("komet" follows Komet's preset, "own" leaves the mod's file alone) and
        // the switches the player set against it
        [JsonProperty("adapters")] public Dictionary<string, AdapterChoice> Adapters { get; set; } = [];
    }

    internal sealed class AdapterChoice
    {
        [JsonProperty("level")] public string Level { get; set; } = FollowKomet;
        [JsonProperty("set")] public Dictionary<string, bool> Set { get; set; } = [];
    }

    private static Saved? _saved;

    private static string Relative => Path.Join("komet", FileName);

    public static Saved Of(ICoreAPI api)
    {
        if (_saved is { } saved) return saved;
        var read = Read(api) ?? Moved(api);
        _saved = read;
        return read;
    }

    public static void Store(ICoreAPI api)
    {
        if (!NotNull(api) || _saved is not { } saved) return;
        try
        {
            api.StoreModConfig(saved, Relative);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // a read-only file or folder
        {
            api.Logger?.Warning("Komet shaders: {0} could not be saved: {1}", FileName, e.Message);
        }
    }

    // The world ends: read anew next time (the player may have edited the file)
    public static void Forget() => _saved = null;

    private static Saved? Read(ICoreAPI api)
    {
        if (!NotNull(api) || !File.Exists(Path.Join(GamePaths.ModConfig, Relative))) return null;
        try
        {
            return api.LoadModConfig<Saved>(Relative) ?? new Saved();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet shaders: {0} could not be read, every shader is on with its defaults: {1}", FileName, e.Message);
            return new Saved();
        }
    }

    // An earlier build's files in the shaders folder: taken over, then removed so the folder holds only packs
    private static Saved Moved(ICoreAPI api)
    {
        var saved = new Saved();
        var folder = ShaderPacks.UserFolder;
        if (!Directory.Exists(folder)) return saved;
        try
        {
            var off = Path.Join(folder, "off.json");
            if (File.Exists(off))
            {
                saved.Off = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(off)) ?? [];
                File.Delete(off);
            }

            foreach (var file in Directory.GetFiles(folder, "*.values.json").Bounded(MaxOld))
            {
                var id = Path.GetFileName(file)[..^".values.json".Length];
                if (JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(file)) is { } values) saved.Values[id] = values;
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet shaders: earlier settings in {0} could not be moved: {1}", folder, e.Message);
        }

        if (saved.Off.Count > 0 || saved.Values.Count > 0)
        {
            _saved = saved;
            Store(api);
        }

        return saved;
    }
}

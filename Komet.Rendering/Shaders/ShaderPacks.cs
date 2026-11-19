using System.Globalization;
using Komet.Api;
using Komet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Rendering;

// Shader packs without code (docs/site/articles/komet-json.md): a komet-shader.json beside a mod's shaders (assets/<domain>/shaders/) or in
// ModConfig/komet/shaders/<id>/ declares settings and passes. Each pack gets a page in Komet's options; a setting reaches
// the game's shaders as a #define (reloaded on change) and/or a uniform (live), and a pass's fragment file is drawn
// through ShaderHub. The player's values are kept in ModConfig/komet/shader-settings.json (ShaderState).
internal static class ShaderPacks
{
    public const string PackFile = "komet-shader.json";
    private const string AssetPath = "shaders/" + PackFile;
    private const int MaxPacks = 32, MaxSettings = 40, MaxFile = 256 << 10;

    internal const string ModPrefix = ModShaders.PackPrefix;
    private static readonly JsonSerializerSettings Lenient = new() { NullValueHandling = NullValueHandling.Ignore };

    private static readonly List<IDisposable> Held = [];
    private static ICoreClientAPI? _api;
    private static readonly Dictionary<string, Dictionary<string, double>> Values = [];
    private static readonly Dictionary<string, Pack> ByMod = []; // "shaderpack:<id>" → the pack, for the overview page

    public static string UserFolder => Path.Join(GamePaths.ModConfig, "komet", "shaders");

    // A pack's name, author, settings and where it came from, by the mod id its registrations carry
    internal static (string Name, string? Author, int Settings, string Where)? About(string mod)
    {
        if (!NotNull(mod) || !ByMod.TryGetValue(mod, out var pack)) return null;

        return (Said(pack.Name ?? pack.Id!), pack.Author, pack.Settings.Count(s => s.Type != "group"), pack.Where);
    }

    // A path as the player finds it, from VintagestoryData on (the full one fills the detail panel)
    internal static string Shown(string path) =>
        NotNull(path) && GamePaths.DataPath is { Length: > 0 } data &&
        path.StartsWith(data, StringComparison.Ordinal)
            ? Path.GetFileName(data.TrimEnd('/', '\\')) + path[data.TrimEnd('/', '\\').Length..]
            : path;

    // Packs load once the world's assets are there (LevelFinalize), as the other mods' pages are added
    public static void Hook(ICoreClientAPI api)
    {
        _api = api;
        if (!NotNull(api.Event)) return;

        api.Event.LevelFinalize += Loaded;
        Links.ShaderUi.Hooked(api);
    }

    public static void Unhook()
    {
        if (_api?.Event is { } events) events.LevelFinalize -= Loaded;

        Links.ShaderUi.Unhooked();
        _api = null;
        Clear();
    }

    private static void Loaded()
    {
        if (_api is not { } api || !NotNull(api.Assets)) return;

        Links.ShaderUi.Loaded(api); // first: the Ui's overview page heads the Shaders section
        Load(api);
    }

    // Every pack found registered with its page
    public static void Load(ICoreClientAPI api)
    {
        Clear();

        var packs = new List<Pack>();
        FromAssets(api, packs);
        FromManifests(api, packs);
        FromFolders(api, packs);
        foreach (var pack in packs.Bounded(MaxPacks))
        {
            Register(api, pack);
        }
    }

    private static void Clear()
    {
        foreach (var held in Held.Bounded(ShaderHub.MaxDefines + ShaderHub.MaxUniforms + ShaderHub.MaxPasses))
        {
            held.Dispose();
        }

        Held.Clear();
        Values.Clear();
        ByMod.Clear();
    }

    // One pack per mod, beside its shaders: assets/<domain>/shaders/komet-shader.json, its id the domain
    private static void FromAssets(ICoreClientAPI api, List<Pack> packs)
    {
        foreach (var asset in (api.Assets?.GetMany(AssetPath) ?? []).Bounded(MaxPacks))
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
        foreach (var asset in (api.Assets?.GetMany(ShaderMods.Manifest) ?? []).Bounded(MaxPacks))
        {
            if (asset.Location.Path != ShaderMods.Manifest || packs.Count >= MaxPacks ||
                packs.Exists(p => p.Id == asset.Location.Domain))
            {
                continue;
            }

            if (Parsed(api, asset.ToText(), asset.Location.Domain, asset.Location.ToString()) is not { } pack ||
                pack.Settings.Count + pack.Passes.Count == 0)
            {
                continue;
            }

            (pack.Id, pack.Where, pack.Declared) = (asset.Location.Domain, asset.Location.Domain + ":", true);
            if (api.ModLoader?.GetMod(pack.Id)?.Info is
                { } info) // name and author from modinfo.json unless komet.json says
            {
                (pack.Name, pack.Author) = (pack.Name ?? info.Name,
                    pack.Author ?? (info.Authors is { Count: > 0 } a ? string.Join(", ", a) : null));
            }

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
            // a Shader Studio project switched off (komet.json activeShaderProjects; without the key every folder is on)
            if (!File.Exists(file) || packs.Count >= MaxPacks ||
                SwitchedOff(Path.GetFileName(folder)))
            {
                continue;
            }

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
            // ReSharper disable once RedundantEnumerableCastCall Json.NET writes null items into the annotated list
            pack.Settings = [.. pack.Settings.OfType<Setting>()]; // OfType drops the nulls
            // ReSharper disable once RedundantEnumerableCastCall as above: null items in JSON
            pack.Passes = [.. pack.Passes.OfType<PassSpec>()];
            // ReSharper disable once RedundantEnumerableCastCall as above: null items in JSON
            pack.Programs = [.. pack.Programs.OfType<string>()];
            pack.Id = string.IsNullOrWhiteSpace(pack.Id) ? folderId : pack.Id.Trim().ToLowerInvariant();
            if (pack.Settings.Count > MaxSettings)
            {
                pack.Settings.RemoveRange(MaxSettings, pack.Settings.Count - MaxSettings);
            }

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
        GameValues.Ensure(api); // a pack's passes may read komet… values
        Values[id] = Stored(api, id, pack);
        var page = OptionPages.Page("shader-" + id, Said(pack.Name ?? id), Lang.Get("komet:settings-section-shaders"));
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
            {
                Held.Add(ShaderHub.Define(mod, define, () => Text(Value(id, key), setting.Type), pack.Programs));
            }

            if (setting.Uniform is { Length: > 0 } uniform)
            {
                Held.Add(ShaderHub.Uniform(mod, uniform, () => (float)Value(id, key)));
            }
        }

        foreach (var spec in pack.Passes.Bounded(ShaderHub.MaxPasses))
        {
            Pass(api, pack, mod, spec);
        }
    }

    private static void Row(ICoreClientAPI api, OptionPage page, Pack pack, Setting setting)
    {
        var (id, key, label, hint) = (pack.Id!, setting.Key, Said(setting.Label ?? setting.Key),
            setting.Hint is null ? null : Said(setting.Hint));

        void Set(double v)
        {
            // a page left from a pack the last reload no longer found (folder or id changed): nothing to set, no throw
            if (!Values.TryGetValue(id, out var values)) return;

            values[key] = v;
            Store(api, id);
            if (setting.Define is { Length: > 0 })
            {
                ShaderHub.Reload(); // a define is compiled in
            }
        }

        _ = setting.Type switch
        {
            "slider" when setting.Max > setting.Min && setting.Step > 0 =>
                page.Slider(label, setting.Min, setting.Max, setting.Step, () => Value(id, key), Set,
                    setting.Unit ?? "", hint),
            "choice" when setting.Options.Length > 0 =>
                page.Choice(label, [.. setting.Options.Select(Said)], () => (int)Value(id, key), i => Set(i), hint),
            _ => page.Switch(label, () => Value(id, key) >= 0.5, on => Set(on ? 1 : 0), hint)
        };
        _ = page.Default(setting.Default);
    }

    private static void Pass(ICoreClientAPI api, Pack pack, string mod, PassSpec spec)
    {
        if (!NotNull(spec) || spec.Id.Length == 0 || spec.Fragment.Length == 0) return;

        var source = Source(api, pack, spec.Fragment);
        if (source is null)
        {
            api.Logger?.Warning("Komet shaders: pack {0} names the pass file {1}, which is not there", pack.Id,
                spec.Fragment);
            return;
        }

        var (id, when) = (pack.Id!, spec.When);
        var pass = new KapiPass(spec.Id, source)
        {
            Order = spec.Order,
            Enabled = when is { Length: > 0 } ? () => Value(id, when) >= 0.5 : null
        };
        foreach (var setting in pack.Settings.Bounded(MaxSettings))
        {
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
        }

        Held.Add(ShaderHub.AddPass(mod, pass));
    }

    // A file next to the pack's json, from the assets or the folder
    private static string? Source(ICoreClientAPI api, Pack pack, string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name)) return null;

        if (pack.Where.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(pack.Where))
        {
            return api.Assets?.TryGet(new AssetLocation(pack.Where + name))?.ToText();
        }

        var file = Path.Join(pack.Where, name);
        return File.Exists(file) ? Read(file) : null;
    }

    // A pack's or style's text: a lang key (domain:key) in the player's language, else as written
    internal static string Said(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Assert(text.Length < 4096)) return text ?? "";

        return text.Contains(':', StringComparison.Ordinal) && Lang.GetIfExists(text) is { Length: > 0 } said
            ? said
            : text;
    }

    internal static string Text(double value, string type)
    {
        if (!Finite(value) || !NotNull(type)) return "0";

        return type is "switch" or "choice"
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0#####", CultureInfo.InvariantCulture);
    }

    private static double Value(string pack, string key) =>
        Values.TryGetValue(pack, out var values) && values.TryGetValue(key, out var v) ? v : 0;

    private static Dictionary<string, double> Stored(ICoreClientAPI api, string id, Pack pack)
    {
        var stored = ShaderState.Of(api).Values.GetValueOrDefault(id);
        var values = new Dictionary<string, double>();
        foreach (var setting in pack.Settings.Bounded(MaxSettings))
        {
            if (setting.Key.Length > 0 && setting.Type != "group")
            {
                values[setting.Key] = stored is not null && stored.TryGetValue(setting.Key, out var v) && Finite(v)
                    ? v
                    : setting.Default;
            }
        }

        return values;
    }

    private static void Store(ICoreClientAPI api, string id)
    {
        if (!Values.TryGetValue(id, out var values)) return;

        ShaderState.Of(api).Values[id] = Delta(ShaderState.Of(api).Values.GetValueOrDefault(id), values,
            ByMod.GetValueOrDefault(ModPrefix + id));
        if (ShaderState.Of(api).Values[id].Count == 0) _ = ShaderState.Of(api).Values.Remove(id);

        ShaderState.Store(api);
    }

    // Only what differs from the pack's defaults is kept; keys the pack no longer declares stay untouched (a pack
    // update may bring them back). Earlier builds stored every value: those equal to the default leave at the next store.
    internal static Dictionary<string, double> Delta(Dictionary<string, double>? stored,
        Dictionary<string, double> values,
        Pack? pack)
    {
        var delta = stored is null ? [] : new Dictionary<string, double>(stored);
        foreach (var (key, value) in values.Bounded(MaxSettings))
        {
            var setting = pack?.Settings.Find(s => s.Key == key);
            if (setting is not null && Math.Abs(setting.Default - value) < 1e-9)
            {
                _ = delta.Remove(key);
            }
            else
            {
                delta[key] = value;
            }
        }

        return delta;
    }

    // A folder pack the SDK switched off: komet.json "activeShaderProjects" lists the ones on; without the key every
    // folder is on. Read here so a player's Komet honours it without the kit.
    private static bool SwitchedOff(string folder) =>
        SettingsStore.Section("activeShaderProjects") is JArray on &&
        !on.Values<string>().Take(MaxPacks * 4).Contains(folder, StringComparer.Ordinal);

    // Reset to defaults: the pack's values back to its komet-shader.json and the player's delta of it gone
    internal static void Reset(ICoreClientAPI api, string id)
    {
        if (ByMod.GetValueOrDefault(ModPrefix + id) is { } pack && Values.TryGetValue(id, out var values))
        {
            foreach (var setting in pack.Settings.Bounded(MaxSettings))
            {
                if (values.ContainsKey(setting.Key)) values[setting.Key] = setting.Default;
            }
        }

        _ = ShaderState.Of(api).Values.Remove(id);
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

    // komet-shader.json
    internal sealed class Pack
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("author")] public string? Author { get; set; }

        [JsonProperty("programs")]
        public string[] Programs { get; set; } = []; // the defines' programs; none: every program

        [JsonProperty("settings")] public List<Setting> Settings { get; set; } = [];
        [JsonProperty("passes")] public List<PassSpec> Passes { get; set; } = [];

        [JsonIgnore]
        public string Where { get; set; } = ""; // asset "domain:path/" or folder, with a trailing separator

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
}

using Komet.Api;
using Komet.Core;
using Komet.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Options;

// Mods that keep their own settings follow Komet through an adapter, config/komet-adapters/<mod>.json in any mod's assets
// (Komet ships SheyderMod's). It names the mod's settings file and the command that reloads it, the mod's own preset file
// for each Komet level with changes on top, the effects shown as switches, and Komet features held while the mod runs.
// A running mod gets a page under Shaders: a level ("as Komet" follows Komet's preset; "own" leaves the mod's file alone)
// and the switches. Each change writes the mod's file anew (preset, level changes, the player's switches) and runs the
// reload command, so the mod applies it at once as it would its own Save.
internal static class ModAdapters
{
    public const string FollowKomet = ShaderState.FollowKomet, Own = "own";
    private const string AssetPath = "config/komet-adapters/", Backup = ".before-komet";
    private const int MaxAdapters = 32, MaxSettings = 40, MaxHolds = 8, MaxSet = 64;
    private static readonly string[] LevelNames = [FollowKomet, "quality", "balanced", "speed", Own];

    internal sealed class Adapter
    {
        [JsonProperty("mod")] public string Mod { get; set; } = "";
        [JsonProperty("config")] public string Config { get; set; } = "";
        [JsonProperty("reload")] public string Reload { get; set; } = "";
        [JsonProperty("levels")] public Dictionary<string, Level> Levels { get; set; } = [];
        [JsonProperty("settings")] public List<Setting> Settings { get; set; } = [];
        [JsonProperty("holds")] public List<Hold> Holds { get; set; } = [];
    }

    internal sealed class Level
    {
        [JsonProperty("from")] public string? From { get; set; } // a file in the mod's config/ assets
        [JsonProperty("set")] public Dictionary<string, JToken> Set { get; set; } = [];
    }

    internal sealed class Setting
    {
        [JsonProperty("key")] public string Key { get; set; } = ""; // a dotted path in the mod's file
        [JsonProperty("label")] public string? Label { get; set; }
        [JsonProperty("hint")] public string? Hint { get; set; }
    }

    internal sealed class Hold
    {
        [JsonProperty("feature")] public string Feature { get; set; } = "";
        [JsonProperty("reason")] public string Reason { get; set; } = "";
    }

    private static readonly List<Adapter> Loaded = [];
    private static readonly List<(string Mod, FeatureHold Hold)> Held = [];
    private static readonly Dictionary<string, JObject> Written = []; // the file each mod got last, which the switches show
    private static ICoreClientAPI? _api;

    // Komet steers the mod's own settings (a config part), and whether it holds Vulkan only while the mod runs
    public static bool Steers(string mod) => NotNull(mod) && Loaded.Exists(a => a.Mod == mod && a.Config.Length > 0);

    public static bool OpenGlOnly(string mod) =>
        NotNull(mod) && Loaded.Exists(a => a.Mod == mod && a.Holds.Exists(h => h.Feature == "VulkanOnly"));

    // Komet's StartClientSide: holds for the mods that run, the rest once the world is there
    public static void Start(ICoreClientAPI api)
    {
        Stop();
        if (!NotNull(api) || api.Assets is null || api.Event is null) return;
        _api = api;
        foreach (var asset in api.Assets.GetMany(AssetPath, null, true).Bounded(MaxAdapters))
            if (Parsed(api, asset) is { } adapter) Loaded.Add(adapter);
        foreach (var asset in api.Assets.GetMany(ShaderMods.Manifest, null, true).Bounded(MaxAdapters))
            if (asset.Location.Path == ShaderMods.Manifest && Declared(api, asset) is { } adapter && !Loaded.Exists(a => a.Mod == adapter.Mod))
                Loaded.Add(adapter);
        foreach (var adapter in Loaded.Bounded(MaxAdapters))
            if (Running(api, adapter.Mod)) HoldFor(adapter);
        api.Event.LevelFinalize += Ready;
        KometSettings.PresetChanged += Follow;
    }

    public static void Stop()
    {
        if (_api?.Event is { } events) events.LevelFinalize -= Ready;
        KometSettings.PresetChanged -= Follow;
        Loaded.Clear();
        Held.Clear(); // Features drops every hold with the world
        Written.Clear();
        _api = null;
    }

    // A held-back mod started late (ShaderMods.Start): its holds, its settings and its page now
    public static void Started(ICoreClientAPI api, string mod)
    {
        if (!NotNull(api) || !NotNull(mod) || Loaded.Find(a => a.Mod == mod) is not { } adapter) return;
        HoldFor(adapter);
        Apply(api, adapter);
        Page(api, adapter);
    }

    // A running mod paused (ShaderMods.Pause): its holds end, so what it held (Vulkan-only) takes the player's choice again
    public static void Stopped(ICoreClientAPI api, string mod)
    {
        if (!NotNull(api) || !NotNull(mod)) return;
        foreach (var (_, hold) in Held.Where(h => h.Mod == mod).ToArray().Bounded(MaxHolds * MaxAdapters)) hold.Release();
        _ = Held.RemoveAll(h => h.Mod == mod);
    }

    private static void Ready()
    {
        if (_api is not { } api) return;
        foreach (var adapter in Loaded.Bounded(MaxAdapters))
        {
            if (!Running(api, adapter.Mod)) continue;
            Apply(api, adapter);
            Page(api, adapter);
        }
    }

    // Komet's preset moved: the mods that follow it take the matching level
    private static void Follow()
    {
        if (_api is not { World: not null } api) return;
        foreach (var adapter in Loaded.Bounded(MaxAdapters))
            if (Running(api, adapter.Mod) && Choice(api, adapter.Mod).Level == FollowKomet) Apply(api, adapter);
    }

    private static bool Running(ICoreAPI api, string mod)
    {
        if (api?.ModLoader is not { } loader) return false;
        return loader.IsModEnabled(mod) && !ShaderMods.Off.Contains(mod);
    }

    private static void HoldFor(Adapter adapter)
    {
        if (!NotNull(adapter.Holds)) return;
        foreach (var hold in adapter.Holds.Bounded(MaxHolds))
            if (NotNull(hold)) Held.Add((adapter.Mod, Features.HoldOff(hold.Feature, adapter.Mod, hold.Reason)));
    }

    // komet.json, the part an adapter reads: "config" (file, reload, levels, switches), "holds", and "renderer": "opengl"
    // for a mod that draws with OpenGL only (Komet then holds Vulkan only while it runs)
    internal sealed class Manifest
    {
        [JsonProperty("renderer")] public string Renderer { get; set; } = "both";
        [JsonProperty("config")] public Config? Config { get; set; }
        [JsonProperty("holds")] public List<Hold> Holds { get; set; } = [];
    }

    internal sealed class Config
    {
        [JsonProperty("file")] public string File { get; set; } = "";
        [JsonProperty("reload")] public string Reload { get; set; } = "";
        [JsonProperty("levels")] public Dictionary<string, Level> Levels { get; set; } = [];
        [JsonProperty("switches")] public List<Setting> Switches { get; set; } = [];
    }

    private static Adapter? Declared(ICoreClientAPI api, IAsset asset)
    {
        try
        {
            if (JsonConvert.DeserializeObject<Manifest>(asset.ToText()) is not { } manifest) return null;
            var mod = asset.Location.Domain;
            var adapter = new Adapter
            {
                Mod = mod, Config = manifest.Config?.File ?? "", Reload = manifest.Config?.Reload ?? "",
                Levels = manifest.Config?.Levels ?? [], Settings = manifest.Config?.Switches ?? [],
                Holds = NotNull(manifest.Holds) ? manifest.Holds : [] // "holds": null in another mod's file threw out of Start
            };
            if (string.Equals(manifest.Renderer, "opengl", StringComparison.OrdinalIgnoreCase))
                adapter.Holds.Add(new Hold { Feature = "VulkanOnly", Reason = mod + " draws with OpenGL only (komet.json)" });
            return Assert(!adapter.Config.Contains("..", StringComparison.Ordinal)) ? adapter : null;
        }
        catch (JsonException e)
        {
            api.Logger?.Warning("Komet: {0} is not valid: {1}", asset.Location, e.Message);
            return null;
        }
    }

    private static Adapter? Parsed(ICoreClientAPI api, IAsset asset)
    {
        try
        {
            var adapter = JsonConvert.DeserializeObject<Adapter>(asset.ToText());
            if (adapter is { Mod.Length: > 0, Config.Length: > 0 } && !adapter.Config.Contains("..", StringComparison.Ordinal) &&
                NotNull(adapter.Settings) && Assert(adapter.Settings.Count <= MaxSettings * 4)) return adapter;
            api.Logger?.Warning("Komet adapters: {0} names no mod or settings file", asset.Location);
        }
        catch (JsonException e)
        {
            api.Logger?.Warning("Komet adapters: {0} is not valid: {1}", asset.Location, e.Message);
        }

        return null;
    }

    private static ShaderState.AdapterChoice Choice(ICoreClientAPI api, string mod)
    {
        var all = ShaderState.Of(api).Adapters;
        if (!all.TryGetValue(mod, out var choice)) all[mod] = choice = new ShaderState.AdapterChoice();
        return choice;
    }

    // The level a choice means now: "komet" is Komet's preset (auto resolved), "own" none
    private static string? Effective(ShaderState.AdapterChoice choice)
    {
        return choice.Level switch
        {
            FollowKomet => Presets.Resolved(KometSettings.Current),
            Own => null,
            var level => level
        };
    }

    // The mod's file anew: its preset for the level, the level's changes, the player's switches; then its reload command
    private static void Apply(ICoreClientAPI api, Adapter adapter)
    {
        var choice = Choice(api, adapter.Mod);
        // "quality": null or "set": null in another mod's file threw out of LevelFinalize
        if (adapter.Config.Length == 0 || !NotNull(adapter.Levels) || Effective(choice) is not { } level ||
            !adapter.Levels.TryGetValue(level, out var spec) || !NotNull(spec) || !NotNull(spec.Set)) return;
        var file = Base(api, adapter, spec);
        foreach (var (key, value) in spec.Set.Bounded(MaxSet)) Put(file, key, value);
        foreach (var (key, on) in choice.Set.Bounded(MaxSet)) Put(file, key, on);
        var path = Path.Join(GamePaths.ModConfig, adapter.Config);
        try
        {
            // the player's own settings are kept once, before Komet first writes the file
            if (File.Exists(path) && !File.Exists(path + Backup)) File.Copy(path, path + Backup);
            File.WriteAllText(path, file.ToString(Formatting.Indented));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet adapters: {0} could not be written: {1}", adapter.Config, e.Message);
            return;
        }

        Written[adapter.Mod] = file;
        Command(api, adapter.Reload);
        api.Logger?.Notification("Komet adapters: {0} set to {1}{2}", adapter.Mod, level, choice.Set.Count > 0 ? " with the player's switches" : "");
    }

    // The mod's preset file for the level, else its current settings file, else nothing
    private static JObject Base(ICoreClientAPI api, Adapter adapter, Level spec)
    {
        try
        {
            if (spec.From is { Length: > 0 } from && api.Assets.TryGet(new AssetLocation(adapter.Mod, "config/" + from)) is { } preset)
                return JObject.Parse(preset.ToText());
            var own = Path.Join(GamePaths.ModConfig, adapter.Config);
            if (File.Exists(own)) return JObject.Parse(File.ReadAllText(own));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet adapters: the settings of {0} could not be read: {1}", adapter.Mod, e.Message);
        }

        return [];
    }

    // A dotted path (Section.Key) set in the file, its sections made where missing
    private static void Put(JObject file, string path, JToken value)
    {
        var parts = path.Split('.');
        if (parts.Length is 0 or > 8) return;
        var at = file;
        foreach (var part in parts.AsSpan(0, parts.Length - 1).Bounded(8))
        {
            if (at[part] is not JObject inner) at[part] = inner = [];
            at = inner;
        }

        at[parts[^1]] = value;
    }

    private static bool? Get(JObject? file, string path)
    {
        JToken? at = file;
        if (!NotNull(path)) return null;
        foreach (var part in path.Split('.').Bounded(8)) at = at?[part];
        return at?.Type == JTokenType.Boolean ? at.Value<bool>() : null;
    }

    private static void Command(ICoreClientAPI api, string command)
    {
        if (!NotNull(command) || command.Length < 2 || api.World?.Player is not { } player) return;
        // a client command for the local player, with every privilege: without them the mod's command refuses ("no
        // permission") and never reloads its file
        var caller = new Caller { Type = EnumCallerType.Player, Player = player, FromChatGroupId = 0, CallerPrivileges = ["*"] };
        var args = new TextCommandCallingArgs { Caller = caller };
        try
        {
            api.ChatCommands.ExecuteUnparsed(command, args, result =>
            {
                if (result?.Status == EnumCommandStatus.Error) api.Logger?.Warning("Komet adapters: {0} failed: {1}", command, result.StatusMessage);
                else api.Logger?.Notification("Komet adapters: {0}: {1}", command, result?.StatusMessage ?? "done");
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            api.Logger?.Warning("Komet adapters: {0} threw: {1}", command, e.Message);
        }
    }

    // The mod's page under Shaders: the level, then each switch. A switch shows the level's value until the player sets it.
    private static void Page(ICoreClientAPI api, Adapter adapter)
    {
        if (adapter.Config.Length == 0) return; // holds only
        var name = api.ModLoader?.GetMod(adapter.Mod)?.Info?.Name is { Length: > 0 } title ? title : adapter.Mod;
        var page = KometOptions.Page("adapter-" + adapter.Mod, name, T("section-shaders"));
        string[] levels = [.. LevelNames.Select(l => T("adapter-level-" + l))];
        _ = page.Group(T("page-takesover", name));
        _ = page.Choice(T("adapter-level"), levels, () => Math.Max(0, Array.IndexOf(LevelNames, Choice(api, adapter.Mod).Level)), i =>
        {
            if (!Index(i, LevelNames.Length)) return;
            var choice = Choice(api, adapter.Mod);
            (choice.Level, choice.Set) = (LevelNames[i], []);
            Changed(api, adapter);
        }, T("adapter-level-hint", name)).Live();
        _ = page.Group(T("adapter-effects"));
        foreach (var setting in adapter.Settings.Bounded(MaxSettings))
        {
            var key = setting.Key;
            _ = page.Switch(ShaderPacks.Said(setting.Label ?? key), () => Get(Written.GetValueOrDefault(adapter.Mod), key) ?? false, on =>
            {
                Choice(api, adapter.Mod).Set[key] = on;
                Changed(api, adapter);
            }, setting.Hint is { } hint ? ShaderPacks.Said(hint) : null).Live().EnabledWhen(() => Choice(api, adapter.Mod).Level != Own);
        }
    }

    private static void Changed(ICoreClientAPI api, Adapter adapter)
    {
        ShaderState.Store(api);
        Apply(api, adapter);
    }

    private static string T(string key, params object[] args) =>
        KometText.Translate("settings-" + key, args);
}

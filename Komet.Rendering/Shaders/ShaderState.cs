using Newtonsoft.Json;

namespace Komet.Rendering;

// What the player chose for shader packs and shader mods, in one file beside Komet's other settings
// (ModConfig/komet/shader-settings.json): which are switched off, and each pack's values. The shaders folder keeps only
// packs. Files of earlier builds (shaders/off.json, shaders/<id>.values.json) move in once and are deleted.
internal static class ShaderState
{
    // An adapter's level that follows Komet's preset (ModAdapters names it too)
    public const string FollowKomet = "komet";

    private const string FileName = "shader-settings.json";
    private const int MaxOld = 64;

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
        if (_saved is not { } saved) return;

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
        if (!File.Exists(Path.Join(GamePaths.ModConfig, Relative))) return null;

        try
        {
            return api.LoadModConfig<Saved>(Relative) ?? new Saved();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            api.Logger?.Warning("Komet shaders: {0} could not be read, every shader is on with its defaults: {1}",
                FileName, e.Message);
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
                if (JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(file)) is { } values)
                {
                    saved.Values[id] = values;
                }

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

    internal sealed class Saved
    {
        [JsonProperty("off")] public List<string> Off { get; set; } = [];

        [JsonProperty("values")] public Dictionary<string, Dictionary<string, double>> Values { get; set; } = [];

        // Shader mods the player switched off; the others run
        [JsonProperty("stopped")] public List<string> Stopped { get; set; } = [];

        // Per mod adapter (ModAdapters): the level ("komet" follows Komet's preset, "own" leaves the mod's file alone) and
        // the switches the player set against it
        [JsonProperty("adapters")] public Dictionary<string, AdapterChoice> Adapters { get; set; } = [];

        // Per shader project, what the workbench set against its defaults: overrides switched off, stage orders
        [JsonProperty("studio")] public Dictionary<string, StudioDelta> Studio { get; set; } = [];

        // A game file several active projects copy: the project whose copy the player picked
        [JsonProperty("picks")] public Dictionary<string, string> Picks { get; set; } = [];
    }

    internal sealed class StudioDelta
    {
        // Programs whose override the player switched off: the game's (vanilla) files run
        [JsonProperty("vanilla")] public List<string> Vanilla { get; set; } = [];

        // Per stage, the module ids in the order the player put them
        [JsonProperty("order")] public Dictionary<string, List<string>> Order { get; set; } = [];

        [JsonIgnore] public bool Empty => Vanilla.Count == 0 && Order.Count == 0;
    }

    internal sealed class AdapterChoice
    {
        [JsonProperty("level")] public string Level { get; set; } = FollowKomet;
        [JsonProperty("set")] public Dictionary<string, bool> Set { get; set; } = [];
    }
}

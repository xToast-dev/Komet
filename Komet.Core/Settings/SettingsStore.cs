using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Core;

// komet.json, Komet's one settings file in the game's ModConfig: the HUD's display settings and, as top-level keys, the
// player's knobs (KnobSettings). The file's object (HudSettings) is the Ui's; reading, the move from the name before
// 3.0, the copy of a broken file and writing are the store's.
internal static class SettingsStore
{
    internal const string FileName = "komet.json"; // the name before 3.0
    private const string OldFileName = "komet-hud.json";

    // Top-level keys of komet.json a module owns beside the knobs (the Shader Studio's project list): read with the file
    // (Took), written back with every save of it (Give), and set by their module through SetSection, which also merges
    // the key into the file at once. Main thread.
    private static readonly string[] SectionKeys = ["activeShaderProjects", "shaderStudio"];

    private static readonly Dictionary<string, JToken> Sections = [];
    private static bool _sectionsRead;

    // The file's object, or a new one when there is none or it is unreadable; unknown keys (PinnedMods, ShowPins of
    // earlier versions) are ignored
    public static T Load<T>(ICoreClientAPI capi) where T : class, new()
    {
        Renamed(capi.Logger);
        try
        {
            return capi.LoadModConfig<T>(FileName) ?? new T();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            if (e is JsonException)
            {
                Keep(Path.Join(GamePaths.ModConfig, FileName)); // the next save would overwrite a hand edit's typo
            }

            return new T();
        }
    }

    public static void Save(ICoreClientAPI capi, object settings)
    {
        if (!NotNull(settings)) return;

        try
        {
            capi.StoreModConfig(settings, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} not written ({1})", FileName, e.Message);
        }
    }

    // A module's key as the file holds it; null when it holds none
    public static JToken? Section(string key)
    {
        if (!_sectionsRead) ReadSections();

        return Sections.TryGetValue(key, out var token) ? token.DeepClone() : null;
    }

    // Sets (null: removes) a module's key and merges it into komet.json; false when the file could not be written
    public static bool SetSection(string key, JToken? value)
    {
        if (!Assert(Array.IndexOf(SectionKeys, key) >= 0)) return false;

        if (!_sectionsRead) ReadSections();

        if (value is null)
        {
            _ = Sections.Remove(key);
        }
        else
        {
            Sections[key] = value.DeepClone();
        }

        return Merge(key, value);
    }

    // The file was read: the module keys go from the knobs' extension data to the sections
    internal static void Took(Dictionary<string, JToken> json)
    {
        foreach (var key in SectionKeys.Bounded(SectionKeys.Length))
        {
            if (json.Remove(key, out var token)) Sections[key] = token;
        }

        _sectionsRead = true;
    }

    // The file is about to be written: the module keys go with it
    internal static void Give(Dictionary<string, JToken> json)
    {
        foreach (var key in SectionKeys.Bounded(SectionKeys.Length))
        {
            if (Sections.TryGetValue(key, out var token)) json[key] = token.DeepClone();
        }
    }

    private static void ReadSections()
    {
        _sectionsRead = true;
        if (FileObject() is not { } file) return;

        foreach (var key in SectionKeys.Bounded(SectionKeys.Length))
        {
            if (file.TryGetValue(key, out var token)) Sections[key] = token;
        }
    }

    private static JObject? FileObject()
    {
        var path = Path.Join(GamePaths.ModConfig, FileName);
        try
        {
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Merge(string key, JToken? value)
    {
        var path = Path.Join(GamePaths.ModConfig, FileName);
        var file = File.Exists(path) ? FileObject() : JObject.Parse("{}");
        if (file is null)
        {
            return false; // unreadable: the next save of the whole file writes the section
        }

        if (value is null)
        {
            _ = file.Remove(key);
        }
        else
        {
            file[key] = value.DeepClone();
        }

        var temporary = path + ".tmp";
        try
        {
            _ = Directory.CreateDirectory(GamePaths.ModConfig);
            File.WriteAllText(temporary, file.ToString(Formatting.Indented));
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A komet-hud.json from before 3.0 becomes komet.json once
    private static void Renamed(ILogger logger)
    {
        var (old, path) = (Path.Join(GamePaths.ModConfig, OldFileName), Path.Join(GamePaths.ModConfig, FileName));
        try
        {
            if (!File.Exists(old) || File.Exists(path)) return;

            File.Move(old, path);
            logger.Notification("Komet: settings moved from {0} to {1}", OldFileName, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Warning("Komet: {0} not renamed to {1} ({2}); defaults apply", OldFileName, FileName, e.Message);
        }
    }

    private static void Keep(string path)
    {
        try
        {
            File.Copy(path, path + ".broken", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no copy: the warning above names the file
        }
    }
}

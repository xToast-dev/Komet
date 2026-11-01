using Komet.Rendering;
using Newtonsoft.Json;

namespace Komet.Options;

// Komet's examples, kept as templates in its assets (config/komet-templates/<kind>-example/, a folder nothing loads) and
// written on request into the player's own folder (ModConfig/komet/styles/example, ModConfig/komet/shaders/example; a
// taken name gets a number). By default those folders stay empty: an example is there only once the player asks for it.
internal static class KometExamples
{
    public const string Style = "style", Shader = "shader", Mod = "mod", Vulkan = "vulkan";

    // A whole mod made for Komet to copy into the Mods folder: kept apart from the packs and styles Komet reads
    public static string ModFolder => Path.Join(GamePaths.ModConfig, "komet", "mod-template");
    private const string TemplatePath = "config/komet-templates/";
    private const int MaxFiles = 32, MaxNames = 100;

    // The folder the example went to, or null (no template, or it could not be written; logged)
    public static string? Create(ICoreClientAPI api, string kind)
    {
        if (!NotNull(api) || !NotNull(kind) || api.Assets is null) return null;
        var root = kind switch
        {
            Style => StyleLibrary.UserFolder,
            Mod or Vulkan => ModFolder,
            _ => ShaderPacks.UserFolder
        };
        var source = TemplatePath + kind + "-example/";
        var files = api.Assets.GetMany(source, "komet", true);
        if (files is not { Count: > 0 })
        {
            api.Logger?.Warning("Komet: the {0} example is missing from Komet's assets", kind);
            return null;
        }

        var folder = Free(root, kind switch { Mod => "mymod", Vulkan => "myshaders-dev", _ => "example" });
        try
        {
            _ = Directory.CreateDirectory(folder);
            foreach (var file in files.Bounded(MaxFiles))
            {
                // C# sources travel as .cs.txt: the game refuses a zipped mod (Komet) with .cs files outside src/
                var target = Path.Join(folder, file.Location.Path[source.Length..].Replace(".cs.txt", ".cs", StringComparison.Ordinal));
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, file.Data);
            }

            if (kind == Style) Named(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            api.Logger?.Warning("Komet: the {0} example could not be written to {1}: {2}", kind, folder, e.Message);
            return null;
        }

        api.Logger?.Notification("Komet: the {0} example is in {1}", kind, folder);
        return folder;
    }

    // A style's id is its folder's name unless style.json sets one, and the template sets "example": a second copy
    // (example-2) takes its folder's name, so it neither replaces the first nor leaves the Style page on an unknown id
    internal static void Named(string folder)
    {
        var file = Path.Join(folder, "style.json");
        if (!NotNull(folder) || !File.Exists(file)) return;
        var name = Path.GetFileName(folder);
        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
        if ((string?)json["id"] == name) return;
        json["id"] = name;
        File.WriteAllText(file, json.ToString(Formatting.Indented) + "\n");
    }

    // name, else name-2, name-3 …
    internal static string Free(string root, string name = "example")
    {
        var folder = Path.Join(root, name);
        for (var n = 2; n < MaxNames && Directory.Exists(folder); n++) folder = Path.Join(root, name + "-" + n);
        return folder;
    }
}

using System.IO.Compression;
using System.Reflection;
using HarmonyLib;

namespace Komet.Rendering;

// ShaderMods: finding shader mods by their files, and the records describing them.
internal static partial class ShaderMods
{
    private static Found? Scan(ICoreAPI api, Mod mod)
    {
        if (!NotNull(mod.SourcePath)) return null;

        try
        {
            if (mod.SourceType == EnumModSourceType.Folder)
            {
                var root = Path.Join(mod.SourcePath, "assets");
                if (!Directory.Exists(root)) return null;

                var list = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => "assets/" + Path.GetRelativePath(root, f)).ToList();
                var (replaced, own) = Tally(list, mod.Info?.ModID);
                return Made(mod, replaced, own, Declares(list), KometVersions(list), Foreign(list, mod.Info?.ModID)) is
                    { } folder
                    ? folder with { Content = HasContent(list) || Universal(mod) }
                    : null;
            }

            using var zip = ZipFile.OpenRead(mod.SourcePath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            var (zipped, owned) = Tally(entries, mod.Info?.ModID);
            return Made(mod, zipped, owned, Declares(entries), KometVersions(entries),
                Foreign(entries, mod.Info?.ModID)) is { } made
                ? made with { Content = HasContent(entries) || Universal(mod) }
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: the files of {0} could not be read: {1}", mod.Info?.ModID, e.Message);
            return null;
        }
    }

    // The install has the file: under its path, or as an include of that name (see Restore)
    private static bool Vanilla(string file) =>
        GamePaths.AssetsPath is not { Length: > 0 } assets ||
        File.Exists(Path.Join(assets, "game", file)) ||
        File.Exists(Path.Join(assets, "game", "shaderincludes", Path.GetFileName(file)));

    private static Found? Made(Mod mod, string[] replaced, string[] own, bool declared, string[] variants,
        string[] foreign)
    {
        replaced =
        [
            .. replaced.Where(Vanilla)
        ]; // a new file in the game domain (Primitive Survival's poison.fsh) replaces nothing
        if ((replaced.Length + own.Length + variants.Length + foreign.Length == 0 && !declared) ||
            !NotNull(mod.Info))
        {
            return null;
        }

        var name = mod.Info.Name is { Length: > 0 } named ? named : mod.Info.ModID;
        return new Found(mod.Info.ModID, name, mod.SourcePath, replaced, own, declared)
            { Variants = variants, Elsewhere = foreign };
    }

    // Shader files under another mod's domain (not game, not the mod's own) as domain:category/file
    internal static string[] Foreign(IEnumerable<string> paths, string? owner)
    {
        List<string> foreign = [];
        var own = owner?.ToLowerInvariant() ?? "";
        foreach (var raw in paths.Bounded(MaxEntries))
        {
            if (raw.Replace('\\', '/').ToLowerInvariant().Split('/') is
                    ["assets", var domain, var category and ("shaders" or "shaderincludes"), .. var rest] &&
                domain != "game" && domain != own && own.Length > 0 && rest.Length > 0 && Shader(rest[^1]))
            {
                foreign.Add(domain + ":" + category + "/" + string.Join('/', rest));
            }
        }

        return [.. foreign];
    }

    // Komet versions in the mod's files: assets/<domain>/config/komet/<shaders|shaderincludes>/<file> as domain:category/file
    internal static string[] KometVersions(IEnumerable<string> paths)
    {
        List<string> variants = [];
        foreach (var raw in paths.Bounded(MaxEntries))
        {
            if (raw.Replace('\\', '/').ToLowerInvariant().Split('/') is
                [
                    "assets", var domain, "config", "komet", var category and ("shaders" or "shaderincludes"),
                    .. var rest
                ] &&
                rest.Length > 0 && Shader(rest[^1]))
            {
                variants.Add(domain + ":" + category + "/" + string.Join('/', rest));
            }
        }

        return [.. variants];
    }

    // A config/komet.json in any of the mod's domains
    internal static bool Declares(IEnumerable<string> paths) =>
        NotNull<object>(paths) && paths.Take(MaxEntries).Any(raw =>
            raw.Replace('\\', '/').ToLowerInvariant().Split('/') is ["assets", _, "config", "komet.json"]);

    // Replaced: game asset paths in lower case ("shaders/final.fsh"); Own: "domain:pass" of the mod's programs
    // A mod for both sides (Rivers: world generation in code, river current in its client half): skipping its client
    // systems would leave the server's half talking to nothing
    private static bool Universal(Mod mod) => mod.Info?.Side is EnumAppSide.Universal or EnumAppSide.Server;

    // Any asset of a content category: assets/<domain>/itemtypes/..., blocktypes, entities, patches, recipes, worldgen
    internal static bool HasContent(IEnumerable<string> paths) =>
        paths.Bounded(MaxEntries).Any(raw =>
            raw.Replace('\\', '/').ToLowerInvariant().Split('/') is
            [
                "assets", _, "itemtypes" or "blocktypes" or "entities" or "patches" or "recipes" or "worldgen", _, ..
            ]);

    internal static (string[] Replaced, string[] Own) Tally(IEnumerable<string> paths, string? owner = null)
    {
        List<string> replaced = [];
        HashSet<string> own = [];
        foreach (var raw in paths.Bounded(MaxEntries))
        {
            var path = raw.Replace('\\', '/').ToLowerInvariant(); // a zip packed on Windows may say assets\game\shaders
            if (!Shader(path) || path.Split('/') is not ["assets", var domain, var category, .. var rest] ||
                rest.Length == 0)
            {
                continue;
            }

            if (domain == "game" && category is "shaders" or "shaderincludes")
            {
                replaced.Add(category + "/" + string.Join('/', rest));
            }
            else if (domain != "game" && category == "shaders" &&
                     (owner is null || domain.Equals(owner, StringComparison.OrdinalIgnoreCase)))
            {
                _ = own.Add(domain + ":" + Path.GetFileNameWithoutExtension(rest[^1]));
            }
        }

        return ([.. replaced], [.. own]);
    }

    private static bool Shader(string path) =>
        path.EndsWith(".fsh", StringComparison.Ordinal) || path.EndsWith(".vsh", StringComparison.Ordinal) ||
        path.EndsWith(".gsh", StringComparison.Ordinal) || path.EndsWith(".ash", StringComparison.Ordinal);

    // A mod's shader files: Replaced as game asset paths (shaders/final.fsh), Own as domain:pass
    internal sealed record Found(
        string Id,
        string Name,
        string Source,
        string[] Replaced,
        string[] Own,
        bool Declared = false)
    {
        // Its Komet versions: "domain:category/file" for each assets/<domain>/config/komet/<category>/<file>
        public string[] Variants { get; init; } = [];

        // Shader files it replaces in another mod's domain ("sheydermod:shaders/deferredlighting.fsh"; DRTAgX does)
        public string[] Elsewhere { get; init; } = [];

        public bool Replaces => Replaced.Length + Elsewhere.Length > 0;

        // It brings game content (items, blocks, entities, patches, recipes, world generation): its systems register the
        // classes that content names, so they always run; held back, only its replaced files give way (Primitive
        // Survival replaces 4 game shaders: skipped systems left 'inTreeHollowTransform' unregistered and the join crashed)
        public bool Content { get; init; }
    }

    // A program as the game last compiled it; Kept counts the compiles, so a port can tell it was outlived
    private sealed record Program(int Id, string Domain, string Pass, string[] Includes, bool Compiled, int Kept);

    internal sealed record Diagnostic(
        string Domain,
        string Pass,
        int Id,
        bool Compiled,
        string[] Includes,
        string[] Owners);

    // What a running mod drew and patched, taken out by Pause and put back by Resume within the same world
    private sealed record Paused(
        List<(IRenderer Renderer, EnumRenderStage Stage, string Name)> Renderers,
        List<(MethodBase Target, Patch Patch, HarmonyPatchType Kind)> Patches)
    {
        // its shader-reload listeners with their place in the engine's list
        public List<(int At, ActionBoolReturn Handler)> Reloads { get; } = [];
    }
}

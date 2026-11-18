using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Komet.Core;
using Komet.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Sdk;

// The Shader Studio's projects: folders under ModConfig/komet/shaders/<name>/ (ShaderPacks.UserFolder), each with a
// komet-shader.json (settings, passes), shaders/ (copies of game shaders: shaders/x.fsh stands in for game:shaders/x.fsh),
// includes/ (copies of game:shaderincludes/*), passes/ (the pack's pass files) and .komet/ (originals.json, the originals'
// text, the build history; never exported). Switched-on projects are listed in komet.json "activeShaderProjects" (their
// order is the winner order); without that key every project folder is on (the packs before the Studio).
//
// API (main thread unless noted):
//   All() -> ShaderProject[] (Name, Active)
//   Editing { get; set; }      the project open in the editor: it wins over the others (set by the UI)
//   SetActive(name, on)        persists the list, reloads the packs and the shaders
//   Create(name, template) -> folder or null;  CopyIn(gameAsset "shaders/x.fsh", project) -> copy path or null
//   GameFileOf(copyPath) -> "shaders/x.fsh" / "shaderincludes/x.ash" or null
//   Winner(gameFile) -> project name or null;  OverlaidBy(project, gameFile) -> project name or null
//   OriginalChanged(copyPath) -> (Old, New) text when the game's file changed since the copy;  MarkSeen(copyPath)
//   Export(project, modid, name, version) -> zip path in Mods/ or null (Error says why)
//   Order() -> active names in winner order (any thread: a copy)
internal static class ShaderProjects
{
    internal const string MetaFolder = ".komet";
    private const string ActiveKey = "activeShaderProjects";
    internal const string PassesFolder = "passes";
    private const string ShadersFolder = "shaders", IncludesFolder = "includes";
    private const string VanillaFolder = "vanilla";
    private const int MaxProjects = 64, MaxFiles = 512;

    private static string[] _order = [];
    private static bool _orderRead, _saved;

    public static string Root => ShaderPacks.UserFolder;

    // The last refusal or failure, for the Ausgabe panel
    public static string Error { get; private set; } = "";

    // The project open in the editor: it wins over every other active one
    public static string? Editing
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                ProjectAssets.Changed();
            }
        }
    }

    public static string Folder(string name) => Path.Join(Root, name);

    // Every project folder (a folder under the root, the trash and dot folders left out)
    private static string[] Names()
    {
        if (!Directory.Exists(Root)) return [];

        try
        {
            var names = Directory.GetDirectories(Root).Select(Path.GetFileName).OfType<string>()
                .Where(n => n.Length > 0 && n[0] != '.').Take(MaxProjects).ToArray();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            return [];
        }
    }

    public static ShaderProject[] All()
    {
        var active = Active();
        var names = Names();
        var result = new ShaderProject[Math.Min(names.Length, MaxProjects)];
        for (var i = 0; i < Math.Min(names.Length, MaxProjects); i++)
        {
            var name = names[i];
            result[i] = new ShaderProject(name, active.Contains(name, StringComparer.Ordinal));
        }

        return result;
    }

    // Switched-on projects, in komet.json's order (every folder when the key was never written)
    public static string[] Active()
    {
        if (!_orderRead)
        {
            _orderRead = true;
            _order = SettingsStore.Section(ActiveKey) is JArray list
                ? [.. list.Values<string>().OfType<string>().Take(MaxProjects)]
                : [.. Names()];
            _saved = SettingsStore.Section(ActiveKey) is JArray;
        }

        var names = Names();
        return _saved ? [.. _order.Where(n => names.Contains(n, StringComparer.Ordinal))] : names;
    }

    public static bool IsActive(string name) => Active().Contains(name, StringComparer.Ordinal);

    // Active projects in winner order: the one in the editor first, then komet.json's order
    public static string[] Order()
    {
        var active = Active();
        return Editing is { } editing && active.Contains(editing, StringComparer.Ordinal)
            ? [editing, .. active.Where(n => n != editing)]
            : active;
    }

    public static bool SetActive(ICoreClientAPI api, string name, bool on)
    {
        if (!Names().Contains(name, StringComparer.Ordinal)) return Failed("no project " + name);

        var list = Active().Where(n => n != name).ToList();
        if (on) list.Add(name);

        (_order, _saved) = ([.. list], true);
        var written = SettingsStore.SetSection(ActiveKey, new JArray(list.Cast<object>().ToArray()));
        Reload(api);
        return written || Failed("komet.json not written");
    }

    // The packs read again (an inactive project's settings and passes go) and the shaders reloaded
    private static void Reload(ICoreClientAPI? api)
    {
        if (api is null) return;

        ShaderPacks.Load(api);
        ProjectAssets.Changed();
        Links.ShaderUi.ReloadShaders(api);
    }

    // A new project from a template: "empty" (komet-shader.json only) or "pass" (a pass that shows the scene)
    public static string? Create(string name, string template)
    {
        var folder = Folder(name);
        if (name.Length == 0 || name[0] == '.' || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Directory.Exists(folder))
        {
            return Refuse("invalid or taken name");
        }

        var pass = template == "pass";
        var json = new JObject { ["name"] = name };
        if (pass) json["passes"] = new JArray(new JObject { ["id"] = "main", ["fragment"] = "passes/main.fsh" });

        var ok = StudioFiles.Create(folder, true) &&
                 StudioFiles.Write(Path.Join(folder, ShaderPacks.PackFile), json.ToString(Formatting.Indented)) &&
                 StudioFiles.Create(Path.Join(folder, ShadersFolder), true) &&
                 StudioFiles.Create(Path.Join(folder, IncludesFolder), true) &&
                 (!pass || StudioFiles.Write(Path.Join(folder, PassesFolder, "main.fsh"), PassTemplate));
        return ok ? folder : Refuse(StudioFiles.Error);
    }

    internal const string PassTemplate =
        "// Komet supplies scene, depth, uv, color, resolution, and time.\n" +
        "void main() {\n    color = texture(scene, uv);\n}\n";

    // A game shader copied into the project ("shaders/x.fsh" or "shaderincludes/x.ash", read from the install, never
    // from a swapped asset); the original's hash, game version and text are kept for OriginalChanged
    public static string? CopyIn(string gameAsset, string project)
    {
        var relative = gameAsset.Replace('\\', '/');
        if (relative.StartsWith("game:", StringComparison.Ordinal)) relative = relative[5..];

        if (CopyPath(project, relative) is not { } copy || Installed(relative) is not { } original)
        {
            return Refuse("not a game shader: " + gameAsset);
        }

        if (File.Exists(copy)) return copy;

        if (!StudioFiles.Write(copy, original)) return Refuse(StudioFiles.Error);

        Originals.Remember(project, relative, original);
        ShaderBuild.Snapshot(copy, original); // the first good state: the game's own
        return copy;
    }

    // The game file a copy stands in for
    public static string? GameFileOf(string copyPath)
    {
        if (ProjectOf(copyPath) is not { } project) return null;

        var relative = Path.GetRelativePath(Folder(project), StudioFiles.Full(copyPath)).Replace('\\', '/');
        if (relative.StartsWith(VanillaFolder + "/", StringComparison.Ordinal) &&
            ShaderAssets.Split(relative[(VanillaFolder.Length + 1)..], out var domain, out var asset) && domain != "game")
        {
            return ShaderAssets.Key(domain, asset);
        }

        if (relative.StartsWith(ShadersFolder + "/", StringComparison.Ordinal) &&
            ShaderAssets.IsSource(relative)) return relative;

        return relative.StartsWith(IncludesFolder + "/", StringComparison.Ordinal) && ShaderAssets.IsSource(relative)
            ? "shaderincludes/" + relative[(IncludesFolder.Length + 1)..]
            : null;
    }

    // The project a path lies in
    public static string? ProjectOf(string path)
    {
        var full = StudioFiles.Full(path);
        var root = StudioFiles.Full(Root);
        if (!StudioFiles.Under(full, root) || full.Length <= root.Length + 1) return null;

        var name = full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
        return name.Length > 0 && name[0] != '.' ? name : null;
    }

    // Where a project keeps its copy of a game file
    public static string? CopyPath(string project, string gameFile)
    {
        var relative = gameFile.Replace('\\', '/');
        if (project.Length == 0 || !ShaderAssets.Split(relative, out var domain, out relative)) return null;

        if (domain != "game") return Path.Join(Folder(project), VanillaFolder, domain, relative);
        if (relative.StartsWith("shaders/", StringComparison.Ordinal)) return Path.Join(Folder(project), relative);

        return relative.StartsWith("shaderincludes/", StringComparison.Ordinal)
            ? Path.Join(Folder(project), IncludesFolder, relative["shaderincludes/".Length..])
            : null;
    }

    // The project whose copy the game draws: the first active one in Order that has a copy
    public static string? Winner(string gameFile)
    {
        foreach (var project in Order().Bounded(MaxFiles))
        {
            if (CopyPath(project, gameFile) is { } copy && File.Exists(copy)) return project;
        }

        return null;
    }

    // Another active project whose copy beats this project's; null when this one wins or is off
    public static string? OverlaidBy(string project, string gameFile)
    {
        if (!IsActive(project)) return null;

        var winner = Winner(gameFile);
        return winner is not null && winner != project ? winner : null;
    }

    // The game's file changed since the copy was taken (a game update): its text then and now
    public static (string Old, string New)? OriginalChanged(string copyPath)
    {
        if (ProjectOf(copyPath) is not { } project || GameFileOf(copyPath) is not { } game ||
            Originals.Of(project, game) is not { } recorded || Installed(game) is not { } now)
        {
            return null;
        }

        return recorded.Sha == Originals.Hash(now) ? null : (Originals.Text(project, game) ?? "", now);
    }

    // A copy still as it was taken: the game's text it recorded, byte for byte. It changes nothing the author meant, so it
    // must not stand in for the file: another running shader mod's version (SheyderMod replaces chunkopaque, fogandlight,
    // final, standard...) would give way to the game's text under that mod's code, which then flickers and drops chunks.
    public static bool Unedited(string copyPath, string text) =>
        ProjectOf(copyPath) is { } project && GameFileOf(copyPath) is { } game &&
        Originals.Of(project, game) is { } recorded && recorded.Sha == Originals.Hash(text);

    // The player saw the change: the original's record follows the game's file now
    public static void MarkSeen(string copyPath)
    {
        if (ProjectOf(copyPath) is { } project && GameFileOf(copyPath) is { } game && Installed(game) is { } now)
        {
            Originals.Remember(project, game, now);
        }
    }

    // The game's own file as installed, by asset path ("shaders/x.fsh")
    private static string? Installed(string relative)
    {
        return ShaderAssets.InstalledPath(relative) is { } path && File.Exists(path) ? StudioFiles.Read(path) : null;
    }

    // The project's copies of game files (shaders/ and includes/)
    public static string[] Copies(string project)
    {
        List<string> copies = [];
        foreach (var sub in (ReadOnlySpan<string>)[ShadersFolder, IncludesFolder, VanillaFolder])
        {
            copies.AddRange(Files(Path.Join(Folder(project), sub)));
        }

        return [.. copies.Where(c => GameFileOf(c) is not null)];
    }

    internal static string[] Files(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            return
            [
                .. Directory.EnumerateFiles(folder, "*", options).Where(ShaderAssets.IsSource).Take(MaxFiles)
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            return [];
        }
    }

    // A zip in the player's Mods folder: modinfo.json, assets/<modid>/shaders/komet-shader.json and the pass files under
    // it, the copies under assets/game/shaders/ and assets/game/shaderincludes/ (no .komet/, no code)
    public static string? Export(string project, string modid, string name, string version)
    {
        var folder = Folder(project);
        if (!Directory.Exists(folder) || modid.Length == 0 || version.Length == 0 ||
            !modid.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) ||
            version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Refuse("modid: a-z0-9 only; version required");
        }

        var zip = Path.Join(GamePaths.DataPathMods, modid + "-" + version + ".zip");
        var temporary = zip + ".tmp";
        try
        {
            _ = Directory.CreateDirectory(GamePaths.DataPathMods);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Fill(archive, folder, modid, name, version);
            }

            File.Move(temporary, zip, true);
            return zip;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                Error = d.Message;
            }

            return Refuse(e.Message);
        }
    }

    private static void Fill(ZipArchive archive, string folder, string modid, string name, string version)
    {
        var info = new JObject
        {
            ["type"] = "content", ["modid"] = modid, ["name"] = name.Length > 0 ? name : modid,
            ["version"] = version, ["side"] = "Client", ["requiredOnClient"] = false, ["requiredOnServer"] = false
        };
        Entry(archive, "modinfo.json", info.ToString(Formatting.Indented));
        var json = Path.Join(folder, ShaderPacks.PackFile);
        if (File.Exists(json))
        {
            Entry(archive, "assets/" + modid + "/shaders/" + ShaderPacks.PackFile, File.ReadAllText(json));
        }

        foreach (var pass in Files(Path.Join(folder, PassesFolder)).Bounded(MaxFiles))
        {
            Entry(archive, "assets/" + modid + "/shaders/" + Relative(folder, pass), File.ReadAllText(pass));
        }

        foreach (var copy in Copies(Path.GetFileName(folder)).Bounded(MaxFiles))
        {
            if (GameFileOf(copy) is { } game && ShaderAssets.Split(game, out var domain, out var asset))
            {
                Entry(archive, string.Join('/', "assets", domain, asset), File.ReadAllText(copy));
            }
        }
    }

    private static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');

    private static void Entry(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    // World end: komet.json is read anew next time
    internal static void Forget()
    {
        (_order, _orderRead, _saved) = ([], false, false);
        Editing = null;
    }

    private static bool Failed(string why)
    {
        Error = why;
        return false;
    }

    private static string? Refuse(string why)
    {
        Error = why;
        return null;
    }

    // .komet/originals.json: per copied game file the original's hash and the game version; the text in .komet/originals/
    internal static class Originals
    {
        public static Record? Of(string project, string game) =>
            Read(project).GetValueOrDefault(game);

        public static string? Text(string project, string game) => StudioFiles.Read(TextPath(project, game));

        public static void Remember(string project, string game, string text)
        {
            var all = Read(project);
            all[game] = new Record(Hash(text), GameVersion.ShortGameVersion, DateTime.UtcNow);
            _ = StudioFiles.Write(TextPath(project, game), text);
            _ = StudioFiles.Write(File(project), JsonConvert.SerializeObject(all, Formatting.Indented));
        }

        public static string Hash(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static string File(string project) => Path.Join(Folder(project), MetaFolder, "originals.json");

        private static string TextPath(string project, string game) =>
            Path.Join(Folder(project), MetaFolder, "originals", game.Replace('/', Path.DirectorySeparatorChar));

        private static Dictionary<string, Record> Read(string project)
        {
            try
            {
                return StudioFiles.Read(File(project)) is { } json
                    ? JsonConvert.DeserializeObject<Dictionary<string, Record>>(json) ?? []
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        // ReSharper disable NotAccessedPositionalProperty.Global written to originals.json for the reader
        internal sealed record Record(string Sha, string Version, DateTime Taken);
        // ReSharper restore NotAccessedPositionalProperty.Global
    }
}

// One project for the tree: its name, switched on
internal sealed record ShaderProject(string Name, bool Active);

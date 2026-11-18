using Komet.Rendering;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// What the workbench keeps of the modules across restarts, in shader-settings.json beside the packs' values (ShaderState
// "studio" per project, "picks"): the programs whose override the player switched off (ProjectAssets keeps the game's file
// for them before the first load) and the order the player gave a stage. Only deviations are written; an empty project
// entry leaves the file. A saved order is put back one Move per frame once the stage's modules exist; a module that cannot
// move there (reserved, interleaved, gone) is left where it is and not tried again this world.
internal static partial class ShaderModules
{
    private const int ReplayEvery = 30;

    private static readonly HashSet<string> Replayed = new(StringComparer.Ordinal);
    private static ICoreClientAPI? _api;
    private static int _replayTick;

    private static readonly string[] StageExts = [".fsh", ".vsh", ".gsh"];
    private static bool _replayDone;

    // ProjectAssets.Apply hands the api over before the first load reads the saved deltas
    internal static void Use(ICoreClientAPI api) => _api = api;

    // The game files of a program's stages ("shaders/x.fsh")
    internal static string[] StageFiles(string program)
    {
        var domain = _api is { } api ? ShaderMods.ProgramByName(api, program)?.AssetDomain : null;
        return [.. StageExts.Select(ext => domain is null
            ? ShaderAssets.ProgramKey(program, ext)
            : ShaderAssets.Key(domain, "shaders/" + program + ext))];
    }

    // The active projects that copy one of the programs' stage files
    internal static string[] Copiers(IEnumerable<string> programs)
    {
        string[] files = [.. programs.Take(MaxModules).SelectMany(StageFiles)];
        return [.. ShaderProjects.Active().Where(p => files.Any(f => ShaderProjects.CopyPath(p, f) is { } c && File.Exists(c)))];
    }

    internal static ShaderState.StudioDelta? Delta(string project) =>
        _api is { } api && ShaderState.Of(api).Studio.TryGetValue(project, out var delta) ? delta : null;

    internal static string? Pick(string gameFile) =>
        _api is { } api && ShaderState.Of(api).Picks.TryGetValue(gameFile, out var pick) ? pick : null;

    // The project whose copy of a game file runs where several active projects copy it
    public static void SetPick(ICoreClientAPI api, string id, string project)
    {
        foreach (var file in (ProgramsOf(id) ?? []).SelectMany(StageFiles).ToArray().Bounded(MaxModules))
        {
            ShaderState.Of(api).Picks[file] = project;
        }

        ShaderState.Store(api);
        ProjectAssets.Changed();
        Links.ShaderUi.ReloadShaders(api);
    }

    // Switching an override off puts the programs on the vanilla list of the project whose copy runs them (else the
    // editing one); switching it on takes them off every project's list
    private static void SetVanilla(string[] programs, bool vanilla, string? project)
    {
        if (_api is not { } api) return;

        var studio = ShaderState.Of(api).Studio;
        foreach (var program in programs.Bounded(MaxModules))
        {
            if (vanilla)
            {
                string[] owners = Copiers([program]) is { Length: > 0 } copiers ? copiers : [.. Owner(project)];
                foreach (var owner in owners.Bounded(MaxModules))
                {
                    AddVanilla(studio, owner, program);
                }
            }
            else
            {
                foreach (var (_, delta) in studio.Bounded(MaxModules))
                {
                    _ = delta.Vanilla.Remove(program);
                }
            }
        }

        Prune(studio);
        ShaderState.Store(api);
    }

    private static IEnumerable<string> Owner(string? project) =>
        (project ?? ShaderProjects.Editing) is { } owner ? [owner] : [];

    private static void AddVanilla(Dictionary<string, ShaderState.StudioDelta> studio, string owner, string program)
    {
        if (!studio.TryGetValue(owner, out var delta)) studio[owner] = delta = new ShaderState.StudioDelta();

        if (!delta.Vanilla.Contains(program)) delta.Vanilla.Add(program);
    }

    private static void Prune(Dictionary<string, ShaderState.StudioDelta> studio)
    {
        foreach (var (name, delta) in studio.ToArray().Bounded(MaxModules))
        {
            if (delta.Empty) _ = studio.Remove(name);
        }
    }

    // Reset to defaults: the project's overrides on again, its orders forgotten (the frame keeps the order it has until
    // the world closes)
    public static void Reset(ICoreClientAPI api, string project)
    {
        var saved = ShaderState.Of(api);
        var picked = saved.Picks.Where(p => p.Value == project).Select(p => p.Key).ToArray();
        foreach (var file in picked.Bounded(MaxHandlers))
        {
            _ = saved.Picks.Remove(file);
        }

        if (saved.Studio.Remove(project) || picked.Length > 0) ShaderState.Store(api);

        ProjectAssets.Changed();
        Links.ShaderUi.ReloadShaders(api);
    }

    // A move the player made, kept as the stage's order under the editing project
    public static bool MoveKept(ICoreClientAPI api, string id, int step, string? project)
    {
        if (!Move(api, id, step)) return false;

        var stage = id.Split(':', 2)[0];
        if ((project ?? ShaderProjects.Editing) is { } owner && Stages(api).FirstOrDefault(s => s.Id == stage) is { } now)
        {
            var studio = ShaderState.Of(api).Studio;
            if (!studio.TryGetValue(owner, out var delta)) studio[owner] = delta = new ShaderState.StudioDelta();

            delta.Order[stage] = [.. now.Modules.Select(m => m.Id)];
            Replayed.Clear();
            ShaderState.Store(api);
        }

        return true;
    }

    // Every few frames one step towards a saved order: the first module out of place moves one up
    private static void Replay()
    {
        if (_replayDone || ++_replayTick < ReplayEvery || _api is not { } api) return;

        _replayTick = 0;
        foreach (var project in ShaderProjects.Active().Bounded(MaxStages))
        {
            foreach (var (stage, order) in (Delta(project)?.Order ?? []).Bounded(MaxStages))
            {
                if (Step(api, stage, order)) return;
            }
        }

        _replayDone = api.World is ClientMain; // nothing left to move once the world's renderers are there
    }

    private static bool Step(ICoreClientAPI api, string stage, List<string> order)
    {
        if (!Enum.TryParse<EnumRenderStage>(stage, out var which) || api.World is not ClientMain game ||
            game.eventManager?.renderersByStage is not { } stages || (int)which >= stages.Length)
        {
            return false;
        }

        var now = Modules(stages[(int)which], which).Select(m => m.Id).Where(order.Contains).ToArray();
        var wanted = order.Where(now.Contains).ToArray();
        for (var i = 0; i < Math.Min(now.Length, MaxModules); i++)
        {
            if (now[i] != wanted[i])
            {
                var id = wanted[i];
                var key = stage + "|" + id + "|" + Array.IndexOf(now, id);
                return Replayed.Add(key) && Move(api, id, -1);
            }
        }

        return false;
    }

    // Override switches: on takes the modules' programs off every vanilla list, off puts them on; one reload for all
    public static bool SetOverrides(ICoreClientAPI api, string[] ids, bool on)
    {
        var any = false;
        foreach (var id in ids.Bounded(MaxModules))
        {
            if (ProgramsOf(id) is { Length: > 0 } programs)
            {
                SetVanilla(programs, !on, null);
                any = true;
            }
        }

        if (!any) return Failed("no programs seen");

        ProjectAssets.Changed();
        Links.ShaderUi.ReloadShaders(api);
        return true;
    }

    private static string[]? ProgramsOf(string id)
    {
        if (id.StartsWith(PostStage + ":", StringComparison.Ordinal))
        {
            return Array.Find(PostSteps, s => PostStage + ":" + s.Id == id).Programs;
        }

        return Groups.TryGetValue(id, out var group) ? [.. group.Programs] : null;
    }
}

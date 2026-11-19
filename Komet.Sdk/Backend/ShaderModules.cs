using System.Diagnostics;
using HarmonyLib;
using Komet.Core;
using Komet.Rendering;
using Newtonsoft.Json.Linq;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// The frame as modules for the Studio's Frame section: a module is one renderer group of a render stage (the handlers
// sharing a profiling name, Id "Stage:name"), plus the post chain's steps (Id "Post:ssao", "Post:godrays", "Post:bloom",
// "Post:fxaa"), which are no renderers but game settings re-read every frame (ClientPlatformWindows).
// Programs: the shader programs the group used, seen while the Studio listens: DummyRenderer.OnRenderFrame (the engine's
// systems register actions) marks the group in progress and DrawCounter's Use count attributes to it; a group of its own
// IRenderer classes (mods, block entities) is not attributed (Why Unattributed, Programs empty). CpuMs: the group's
// main-thread time (its actions, smoothed); GpuMs stays null: no per-renderer GPU timer exists. DeltaMs: CpuMs against the
// time it had on the game's files.
// Slots: Game (no copy runs, or the override is switched off: its programs sit on the project's vanilla list in
// shader-settings.json, ShaderModules.Saved), Project (a project's copy runs), Off (its handlers unregistered like
// ShaderMods.Pause; a post step switched off in the game's settings, the player's value kept in komet.json
// "shaderStudio" and put back at world end or the next start). Move: one place up or down inside its stage, never
// across a RenderOrder range a renderer reserved.
//
// API, main thread: Stages lists the stages with their modules, SetOverrides and SetPick choose whose files a module
// runs, Move takes it one place up or down, Listening turns the attribution on, Error says why the last call was
// refused. Off ends at Clear (world end), moves last until the world closes.
internal static partial class ShaderModules
{
    public const string PostStage = "Post";
    private const int MaxStages = 64, MaxHandlers = 1 << 12, MaxModules = 512;
    private const double Smoothing = 0.2;

    private static readonly (string Id, string Setting, string[] Programs)[] PostSteps =
    [
        ("ssao", "ssaoQuality", ["ssao", "bilateralblur"]), ("godrays", "godRays", ["godrays"]),
        ("bloom", "bloom", ["findbright", "blur"]), ("fxaa", "fxaa", [])
    ];

    private static readonly Dictionary<IRenderer, Group> ByRenderer = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, Group> Groups = new(StringComparer.Ordinal);
    private static Harmony? _harmony;
    private static bool _patched;
    private static Group? _current;
    private static long _started;

    public static string Error { get; private set; } = "";

    public static bool Listening
    {
        get => DrawCounter.Listening;
        set
        {
            DrawCounter.Listening = value;
            if (value) Patch();
        }
    }

    internal static void Install(Harmony harmony, ICoreClientAPI api)
    {
        _harmony = harmony;
        _api = api;
        _patched = false;
        PutBackSettings();
        DrawCounter.Install(api);
    }

    // World end: the switched-off renderers come back, the post steps' settings too
    internal static void Clear()
    {
        PutBackSettings();
        ByRenderer.Clear();
        Groups.Clear();
        Replayed.Clear();
        _replayDone = false;
        (_current, _harmony, _patched) = (null, null, false);
        DrawCounter.Clear();
    }

    // A game file whose program a module keeps on the game's version (ProjectAssets)
    internal static bool KeepsGame(string gameFile, string project) =>
        ShaderAssets.Split(gameFile, out _, out var path) && path.StartsWith("shaders/", StringComparison.Ordinal) &&
        Delta(project) is { } delta && delta.Vanilla.Contains(Path.GetFileNameWithoutExtension(path));

    public static ShaderStage[] Stages(ICoreClientAPI api)
    {
        if (api.World is not ClientMain game || game.eventManager?.renderersByStage is not { } stages) return [];

        List<ShaderStage> result = [];
        for (var stage = 0; stage < Math.Min(stages.Length, MaxStages); stage++)
        {
            var name = ((EnumRenderStage)stage).ToString();
            result.Add(new ShaderStage(name, Modules(stages[stage], (EnumRenderStage)stage)));
            if ((EnumRenderStage)stage == EnumRenderStage.AfterOIT) result.Add(new ShaderStage(PostStage, Post()));
        }

        return [.. result];
    }

    private static ShaderModule[] Modules(List<RenderHandler?> handlers, EnumRenderStage stage)
    {
        List<ShaderModule> modules = [];
        HashSet<string> seen = [];
        var prefix = stage + "-";
        for (var i = 0; i < Math.Min(handlers.Count, MaxHandlers); i++)
        {
            var handler = handlers[i];
            if (handler?.Renderer is not { } renderer || DrawCounter.IsMarker(renderer)) continue;

            var label = handler.ProfilingName ?? "";
            label = label.StartsWith(prefix, StringComparison.Ordinal) ? label[prefix.Length..] : label;
            var id = stage + ":" + label;
            var group = Track(id, renderer);
            if (seen.Add(id) && modules.Count < MaxModules)
            {
                modules.Add(Module(group, label, stage.ToString(), modules.Count, renderer is DummyRenderer));
            }
        }

        return [.. modules];
    }

    private static Group Track(string id, IRenderer renderer)
    {
        if (!Groups.TryGetValue(id, out var group)) Groups[id] = group = new Group(id);

        ByRenderer[renderer] = group;
        return group;
    }

    private static ShaderModule Module(Group group, string label, string stage, int order, bool attributed)
    {
        var programs = group.Programs.ToArray();
        return Shaped(group, label, stage, order, programs, false, attributed);
    }

    private static ShaderModule Shaped(Group group, string label, string stage, int order, string[] programs, bool off,
        bool attributed)
    {
        var project = programs.Select(Winner).FirstOrDefault(w => w is not null);
        var slot = ModuleSlot.Game;
        if (off)
        {
            slot = ModuleSlot.Off;
        }
        else if (project is not null)
        {
            slot = ModuleSlot.Project;
        }

        var drawn = !off && (programs.Length == 0 ? group.Ran : programs.Any(DrawCounter.Drawn));
        var why = Why(off, attributed, drawn, programs);
        double? ms = group.Samples > 0 ? group.Ms : null;
        if (slot == ModuleSlot.Game && ms is { } now) group.GameMs = now;

        double? delta = slot == ModuleSlot.Project && ms is { } mine && group.GameMs is { } game ? mine - game : null;
        return new ShaderModule(group.Id, label, stage, programs, slot, project, order, drawn, ms, delta, why);
    }

    private static ModuleWhy Why(bool off, bool attributed, bool drawn, string[] programs)
    {
        if (off) return ModuleWhy.Off;

        if (!attributed && programs.Length == 0) return ModuleWhy.Unattributed;

        if (drawn) return ModuleWhy.None;

        return programs.Any(p => p.StartsWith("cloud", StringComparison.Ordinal))
            ? ModuleWhy.CloudMode
            : ModuleWhy.NotDrawn;
    }

    private static string? Winner(string program)
    {
        if (ProjectAssets.Winners.Count == 0) return null;

        foreach (var stage in StageFiles(program).Bounded(3))
        {
            if (ProjectAssets.Winners.TryGetValue(stage, out var copy))
            {
                return ShaderProjects.ProjectOf(copy);
            }
        }

        return null;
    }

    private static ShaderModule[] Post()
    {
        var modules = new ShaderModule[PostSteps.Length];
        for (var i = 0; i < PostSteps.Length; i++)
        {
            var (id, _, programs) = PostSteps[i];
            var key = PostStage + ":" + id;
            if (!Groups.TryGetValue(key, out var group)) Groups[key] = group = new Group(key);

            var on = PostOn(id);
            var module = Shaped(group, id, PostStage, i, programs, !on, true);
            modules[i] = on || Stored().ContainsKey(id) ? module : module with { Why = ModuleWhy.PostSetting };
        }

        return modules;
    }

    // One place earlier (-1) or later (+1) inside the stage: the group's handlers trade places with the neighbour's
    private static bool Move(ICoreClientAPI api, string id, int step)
    {
        if (api.World is not ClientMain game || step is not (1 or -1) || !Groups.TryGetValue(id, out var group) ||
            id.StartsWith(PostStage + ":", StringComparison.Ordinal))
        {
            return Failed("cannot move " + id);
        }

        var stage = (int)Enum.Parse<EnumRenderStage>(id.Split(':', 2)[0]);
        var list = game.eventManager.renderersByStage[stage];
        var reserved = game.eventManager.renderOrderReservationsByStage[stage];
        var mine = Block(list, group);
        if (mine.Count == 0) return Failed("not in the frame");

        var at = step < 0 ? mine.Start - 1 : mine.Start + mine.Count;
        if (at < 0 || at >= list.Count || list[at]?.Renderer is not { } other || DrawCounter.IsMarker(other) ||
            !ByRenderer.TryGetValue(other, out var neighbour))
        {
            return Failed("at the edge of the stage");
        }

        var theirs = Block(list, neighbour);
        if (Reserved(list, mine, reserved) || Reserved(list, theirs, reserved) || Pinned(group) || Pinned(neighbour))
        {
            return Failed("a renderer reserved its place");
        }

        var first = step < 0 ? theirs : mine;
        var second = step < 0 ? mine : theirs;
        if (first.Start + first.Count != second.Start) return Failed("the groups are interleaved");
        var a = list.GetRange(first.Start, first.Count);
        var b = list.GetRange(second.Start, second.Count);
        list.RemoveRange(first.Start, first.Count + second.Count);
        list.InsertRange(first.Start, [.. b, .. a]);
        return true;
    }

    // The OIT pass's frame (its accumulation set up and resolved) and the engine's own beginnings stay where they are
    private static bool Pinned(Group group) => group.Id is "OIT:beforeoit" or "OIT:afteroit";

    // The contiguous handlers of a group (its first run)
    private static (int Start, int Count) Block(List<RenderHandler?> list, Group group)
    {
        var start = -1;
        for (var i = 0; i < Math.Min(list.Count, MaxHandlers); i++)
        {
            var owned = list[i]?.Renderer is { } r && ByRenderer.TryGetValue(r, out var g) && g == group;
            if (owned && start < 0)
            {
                start = i;
            }
            else if (!owned && start >= 0)
            {
                return (start, i - start);
            }
        }

        return start < 0 ? (0, 0) : (start, Math.Min(list.Count, MaxHandlers) - start);
    }

    private static bool Reserved(List<RenderHandler> list, (int Start, int Count) block,
        List<RenderOrderReservation> reserved)
    {
        for (var n = 0; n < Math.Min(block.Count, MaxHandlers); n++)
        {
            if (list[block.Start + n].Renderer is not { } renderer) continue;

            var order = renderer.RenderOrder;
            var type = renderer.GetType();
            if (reserved.Exists(r => (order >= r.rangeStart && order <= r.rangeEnd) || r.allowedType1 == type ||
                                     r.allowedType2 == type))
            {
                return true;
            }
        }

        return false;
    }

    // DrawCounter: a program used while a group's action runs belongs to that group
    internal static void Used(int pass)
    {
        if (_current is not { } group || group.Programs.Count >= 32) return;

        if (Program(pass) is { } name) _ = group.Programs.Add(name);
    }

    internal static void NextFrame()
    {
        Replay();
        if (!DrawCounter.Listening) return;

        foreach (var (_, group) in Groups.Bounded(MaxModules * 4))
        {
            group.Frame();
        }
    }

    private static string? Program(int pass)
    {
        var programs = ShaderMods.Registered;
        return programs is not null && (uint)pass < (uint)programs.Length ? programs[pass]?.PassName : null;
    }

    // DummyRenderer.OnRenderFrame: the group in progress and its time, only while the Studio listens
    private static void Patch()
    {
        if (_patched || _harmony is not { } harmony) return;

        _patched = true;
        var frame = AccessTools.DeclaredMethod(typeof(DummyRenderer), nameof(DummyRenderer.OnRenderFrame));
        if (NotNull(frame))
        {
            _ = harmony.Patch(frame, new HarmonyMethod(typeof(ShaderModules), nameof(Entering)),
                new HarmonyMethod(typeof(ShaderModules), nameof(Left)));
        }
    }

    private static void Entering(DummyRenderer __instance)
    {
        if (DrawCounter.Listening && ByRenderer.TryGetValue(__instance, out var group))
        {
            (_current, _started) = (group, Stopwatch.GetTimestamp());
        }
    }

    private static void Left()
    {
        if (_current is { } group)
        {
            group.Ticks += Stopwatch.GetTimestamp() - _started;
            group.Runs++;
            _current = null;
        }
    }

    // Post steps: on as the game's settings say; switched off here, the player's value is kept to put back
    private static bool PostOn(string id) =>
        id switch
        {
            "ssao" => ClientSettings.SSAOQuality > 0,
            "godrays" => ClientSettings.GodRayQuality > 0,
            "bloom" => ClientSettings.Bloom,
            _ => ClientSettings.FXAA
        };

    private static void Apply(string id, int value)
    {
        switch (id)
        {
            case "ssao": ClientSettings.SSAOQuality = value; break;
            case "godrays": ClientSettings.GodRayQuality = value; break;
            case "bloom": ClientSettings.Bloom = value != 0; break;
            default: ClientSettings.FXAA = value != 0; break;
        }
    }

    // The player's post settings a module switched off (komet.json shaderStudio.restore): back at start and world end
    private static void PutBackSettings()
    {
        var stored = Stored();
        if (stored.Count == 0) return;

        foreach (var (id, value) in stored.ToArray().Bounded(8))
        {
            Apply(id, value);
        }

        Write([]);
    }

    private static Dictionary<string, int> Stored() =>
        SettingsStore.Section("shaderStudio") is JObject studio && studio["restore"] is JObject restore
            ? restore.Properties().Take(8)
                .ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Integer ? (int)p.Value : 0)
            : [];

    private static void Write(Dictionary<string, int> stored)
    {
        var studio = SettingsStore.Section("shaderStudio") as JObject ?? [];
        studio["restore"] = JObject.FromObject(stored);
        _ = SettingsStore.SetSection("shaderStudio", studio);
    }

    private static bool Failed(string why)
    {
        Error = why;
        return false;
    }

    private sealed class Group(string id)
    {
        public string Id { get; } = id;
        public HashSet<string> Programs { get; } = new(StringComparer.Ordinal);
        public long Ticks { get; set; }
        public int Runs { get; set; }
        public double Ms { get; private set; }
        public int Samples { get; private set; }
        public double? GameMs { get; set; }
        public bool Ran { get; private set; } = true;

        public void Frame()
        {
            if (!DrawCounter.Listening) return;

            var ms = Ticks * 1000.0 / Stopwatch.Frequency;
            Ms = Samples == 0 ? ms : Ms + (ms - Ms) * Smoothing;
            Samples++;
            Ran = Runs > 0 || (Ticks == 0 && Ran);
            (Ticks, Runs) = (0, 0);
        }
    }
}

internal enum ModuleSlot
{
    Game,
    Project,
    Off
}

// Why a module draws nothing or shows no programs: None (drawn), NotDrawn (its programs were not used lately), CloudMode
// (a cloud program the game's cloud mode does not draw), Off (switched off here), PostSetting (the
// game's own settings switch the post step off), Unattributed (an IRenderer group: its programs cannot be told apart)
internal enum ModuleWhy
{
    None,
    NotDrawn,
    CloudMode,
    Off,
    PostSetting,
    Unattributed
}

// A frame stage and its modules in render order
internal sealed record ShaderStage(string Id, ShaderModule[] Modules);

// One module row: Id "Stage:name", shown name, stage id, programs seen, slot, the project whose copy wins (null: the
// game's), its order in the stage, drawn lately, main-thread ms (null until measured while listening), ms against the game's version (Project slot), why it draws nothing
internal sealed record ShaderModule(
    string Id,
    string Display,
    string Stage,
    string[] Programs,
    ModuleSlot Slot,
    string? Project,
    int Order,
    bool Drawn,
    double? CpuMs,
    double? DeltaMs,
    ModuleWhy Why);

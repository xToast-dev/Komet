using System.Collections.Concurrent;
using System.IO.Compression;
using HarmonyLib;
using Komet.Api;
using Vintagestory.Client.NoObf;
using Komet.Core;

namespace Komet.Rendering;

// Shader mods that know nothing of Komet (from the mod database: SheyderMod, a tone-mapping mod replacing final.fsh), found
// by their files: replaced game shaders under assets/game/shaders/ or shaderincludes/, own programs under
// assets/<domain>/shaders/. Each is listed under Active shaders with how its programs fare: compiled by OpenGL (every
// Compile is watched, the ones before Komet read from the registry) and translated for Vulkan (ports are started for any
// the scene has not drawn yet). Switched off, the game's own shader files stand in for the ones the mod replaces: before
// the registry reads its files, the assets the mod won get the install's original bytes. A mod that replaces game shaders
// starts held back until the player switches it on (shader-settings.json "on"): from Komet's StartPre its systems skip
// every phase (ModLoader.TryRunModPhase), and its replaced files give way, so the game draws as without it. Switched on,
// it starts at once (its systems' phases run late, in the game's order, then the shaders reload); switched off, it pauses
// at once (Pause: its renderers and patches out, the game's files back; a mod of shader files alone only swaps files). A mod built for another major version of a mod it
// depends on is never started: DRTAgX 2.1.4 beside SheyderMod 2.0.0 stops the game.
internal static class ShaderMods
{
    private const int MaxMods = 512, MaxEntries = 65536, MaxPrograms = 1024;
    private static readonly string[] Stages = [".vsh", ".fsh", ".gsh"];
    // null when an engine renames or retypes it: a throw here would fail the type, HoldBack in StartPre included
    private static readonly AccessTools.FieldRef<ShaderProgram[]?>? Registry =
        AccessTools.Field(typeof(ShaderRegistry), "shaderPrograms") is { IsStatic: true } registry && registry.FieldType == typeof(ShaderProgram[])
            ? AccessTools.StaticFieldRefAccess<ShaderProgram[]?>(registry) : null;

    // A mod's shader files: Replaced as game asset paths (shaders/final.fsh), Own as domain:pass
    internal sealed record Found(string Id, string Name, string Source, string[] Replaced, string[] Own, bool Declared = false)
    {
        // Its Komet versions: "domain:category/file" for each assets/<domain>/config/komet/<category>/<file>
        public string[] Variants { get; init; } = [];

        // Shader files it replaces in another mod's domain ("sheydermod:shaders/deferredlighting.fsh"; DRTAgX does)
        public string[] Elsewhere { get; init; } = [];

        public bool Replaces => Replaced.Length + Elsewhere.Length > 0;
    }

    // Where a mod keeps the version of a shader file meant for Komet: assets/<domain>/config/komet/shaders/<file> stands in
    // for assets/<domain>/shaders/<file> (shaderincludes alike) while Komet runs. The game never reads config/ as shaders.
    public const string VariantPath = "config/komet/";

    // The mod's declaration for Komet, read from its files before the assets load
    public const string Manifest = "config/komet.json";

    // Whether the player wants the mod to run: a declared mod runs unless switched off, an undeclared one that replaces
    // game shaders only once switched on
    public static bool Wanted(ICoreAPI api, Found mod)
    {
        var state = ShaderState.Of(api);
        return mod.Declared ? !state.Stopped.Contains(mod.Id) : !mod.Replaces || state.On.Contains(mod.Id);
    }

    public static void Want(ICoreAPI api, Found mod, bool on)
    {
        var state = ShaderState.Of(api);
        if (!NotNull(mod)) return;
        _ = state.On.Remove(mod.Id);
        _ = state.Stopped.Remove(mod.Id);
        if (mod.Declared && !on) state.Stopped.Add(mod.Id);
        if (!mod.Declared && on) state.On.Add(mod.Id);
        ShaderState.Store(api);
    }

    // A program as the game last compiled it; Kept counts the compiles, so a port can tell it was outlived
    private sealed record Program(int Id, string Domain, string Pass, string[] Includes, bool Compiled, int Kept);

    private static int _kept;

    private static readonly Dictionary<string, Found?> Seen = [];
    private static readonly Dictionary<string, Program> Programs = [];
    private static readonly HashSet<string> Porting = [];
    private static readonly HashSet<string> Told = []; // held-back mods whose restore was logged this session
    private static readonly HashSet<string> Dumped = []; // stages whose failing source was written this session
    private static readonly AccessTools.FieldRef<Vintagestory.Client.NoObf.Shader, string>? FileName =
        AccessTools.Field(typeof(Vintagestory.Client.NoObf.Shader), "Filename") is not null
            ? AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.Shader, string>("Filename") : null;
    private const string HoldId = "komet-hold";
    private static ICoreClientAPI? _api;

    // The ids held back this session: their systems do not start and their replaced files give way to the game's
    public static HashSet<string> Off { get; } = [];

    // A program translated for Vulkan or not, by pass name ("" when it was): written by the builders, read on the main thread
    public static ConcurrentDictionary<string, string> Ported { get; } = new();

    // Something new to show: a port finished
    public static event Action? Changed;

    public static void Install(Harmony harmony, ICoreClientAPI api)
    {
        _api = api;
        Programs.Clear();
        var load = AccessTools.DeclaredMethod(typeof(ShaderRegistry), "loadRegisteredShaderPrograms");
        var compile = AccessTools.DeclaredMethod(typeof(ShaderProgram), nameof(ShaderProgram.Compile));
        if (!NotNull(harmony) || !NotNull(load) || !NotNull(compile)) return;
        _ = harmony.Patch(load, prefix: new HarmonyMethod(typeof(ShaderMods), nameof(Loading)));
        _ = harmony.Patch(compile, postfix: new HarmonyMethod(typeof(ShaderMods), nameof(AfterCompile)));
        var stage = AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.CompileShader), [typeof(Vintagestory.Client.NoObf.Shader)]);
        if (NotNull(stage)) _ = harmony.Patch(stage, postfix: new HarmonyMethod(typeof(ShaderMods), nameof(AfterStage)));
        Read();
        if (Off.Count > 0) api.Event.LevelFinalize += Tell;
        // the game loaded the mods' files before Komet could step in: once more with the Komet versions and KOMET defined
        KometShaders.Reload();
        KometShaders.RendererChanged += Rendered;
    }

    // KOMET_VULKAN follows the renderer
    private static void Rendered(object? sender, EventArgs e) => KometShaders.Reload();

    public static void Clear()
    {
        KometShaders.RendererChanged -= Rendered;
        if (_api?.Event is { } events) events.LevelFinalize -= Tell;
        (_api, Changed) = (null, null);
        Programs.Clear();
        Porting.Clear();
        Told.Clear();
        Dumped.Clear();
        Ported.Clear();
    }

    // Komet's StartPre, before any other mod's Start: shader mods not switched on are held back for this session
    public static void HoldBack(ICoreAPI api, ILogger logger)
    {
        Release();
        foreach (var mod in All(api).Bounded(MaxMods))
            if ((mod.Replaces || mod.Declared) && (!Wanted(api, mod) || Mismatch(api, mod.Id) is not null))
                _ = Off.Add(mod.Id);
        if (Off.Count == 0) return;
        var run = AccessTools.DeclaredMethod(typeof(Vintagestory.Common.ModLoader), "TryRunModPhase");
        if (!NotNull(run) || new Harmony(HoldId).Patch(run, new HarmonyMethod(typeof(ShaderMods), nameof(Skip))) is null) return;
        logger?.Notification("Komet: shader mods held back until switched on under Shaders › Active shaders: {0}", string.Join(", ", Off));
    }

    // A held-back mod's system skips the phase as if it ran (no "failed to start" in the log)
    private static bool Skip(Mod mod, ModSystem system, ICoreAPI api, ref bool __result)
    {
        if (api?.Side != EnumAppSide.Client || mod?.Info?.ModID is not { } id || !Off.Contains(id)) return true;
        __result = NotNull(system);
        return false;
    }

    // A held-back mod switched on: its systems run every phase now, in the game's order, then its files and the shaders
    // load again. False when it may not start (a dependency of another major version) or one of its phases threw.
    public static bool Start(ICoreClientAPI api, Found mod)
    {
        if (!NotNull(api) || !NotNull(mod) || !Off.Contains(mod.Id) || Mismatch(api, mod.Id) is not null) return false;
        if (Pauses.Remove(mod.Id, out var paused)) return Resume(api, mod, paused); // ran before: its phases ran already
        var systems = (api.ModLoader?.Systems ?? []).Where(s => s?.Mod?.Info?.ModID == mod.Id && s.ShouldLoad(api))
            .OrderBy(s => s.ExecuteOrder()).Take(MaxMods).ToList();
        _ = Off.Remove(mod.Id); // from here its phases are its own (Skip lets them through)
        var started = true;
        foreach (var phase in Phases.Bounded(Phases.Length))
            foreach (var system in systems.Bounded(MaxMods))
                started &= Run(api, system, phase);
        Reloaded(api);
        Links.ModStarted(api, mod.Id); // Komet's holds and the mod's settings from Komet's level
        api.Logger?.Notification("Komet: shader mod {0} started late ({1} systems){2}", mod.Id, systems.Count, started ? "" : ", a phase failed");
        return started;
    }

    // What a running mod drew and patched, taken out by Pause and put back by Resume within the same world
    private sealed record Paused(List<(IRenderer Renderer, EnumRenderStage Stage, string Name)> Renderers,
        List<(System.Reflection.MethodBase Target, Patch Patch, HarmonyPatchType Kind)> Patches);

    private static readonly Dictionary<string, Paused> Pauses = [];

    // A running shader mod switched off: its renderers and Harmony patches go, the game's shader files come back and the
    // shaders load again, so the picture is the game's at once. Its systems stay alive (no second Start when switched on
    // again); the next world holds it back from the start.
    public static bool Pause(ICoreClientAPI api, Found mod)
    {
        if (!NotNull(api) || !NotNull(mod) || Off.Contains(mod.Id) || api.World is not ClientMain game) return false;
        var assemblies = (api.ModLoader?.Systems ?? []).Where(s => s?.Mod?.Info?.ModID == mod.Id).Select(s => s.GetType().Assembly)
            .Distinct().ToHashSet();
        var paused = new Paused([], []);
        var stages = game.eventManager.renderersByStage;
        for (var stage = 0; stage < Math.Min(stages.Length, 64); stage++)
            foreach (var handler in stages[stage].ToArray().Bounded(1 << 12))
                if (Owned(handler.Renderer, assemblies))
                {
                    var name = handler.ProfilingName ?? "";
                    var prefix = (EnumRenderStage)stage + "-";
                    paused.Renderers.Add((handler.Renderer, (EnumRenderStage)stage, name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name));
                    api.Event.UnregisterRenderer(handler.Renderer, (EnumRenderStage)stage);
                }

        foreach (var target in Harmony.GetAllPatchedMethods().ToArray().Bounded(1 << 14))
            if (Harmony.GetPatchInfo(target) is { } info)
                foreach (var (patches, kind) in (ReadOnlySpan<(IReadOnlyCollection<Patch>, HarmonyPatchType)>)[(info.Prefixes, HarmonyPatchType.Prefix),
                             (info.Postfixes, HarmonyPatchType.Postfix), (info.Transpilers, HarmonyPatchType.Transpiler), (info.Finalizers, HarmonyPatchType.Finalizer)])
                    foreach (var patch in patches.ToArray().Bounded(256))
                        if (assemblies.Contains(patch.PatchMethod.DeclaringType?.Assembly!))
                        {
                            paused.Patches.Add((target, patch, kind));
                            new Harmony(patch.owner).Unpatch(target, patch.PatchMethod);
                        }

        Pauses[mod.Id] = paused;
        _ = Off.Add(mod.Id); // from here the shader loads give its files way (Swap)
        Reloaded(api);
        Links.ModStopped(api, mod.Id); // Komet's holds for it end (Vulkan-only may draw again)
        api.Logger?.Notification("Komet: shader mod {0} paused ({1} renderers, {2} patches out)", mod.Id, paused.Renderers.Count, paused.Patches.Count);
        Changed?.Invoke();
        return true;
    }

    private static bool Resume(ICoreClientAPI api, Found mod, Paused paused)
    {
        _ = Off.Remove(mod.Id);
        foreach (var (target, patch, kind) in paused.Patches.Bounded(1 << 14))
        {
            var method = new HarmonyMethod(patch.PatchMethod, patch.priority, patch.before, patch.after);
            var harmony = new Harmony(patch.owner);
            _ = kind switch
            {
                HarmonyPatchType.Prefix => harmony.Patch(target, prefix: method),
                HarmonyPatchType.Postfix => harmony.Patch(target, postfix: method),
                HarmonyPatchType.Transpiler => harmony.Patch(target, transpiler: method),
                _ => harmony.Patch(target, finalizer: method)
            };
        }

        foreach (var (renderer, stage, name) in paused.Renderers.Bounded(1 << 12)) api.Event.RegisterRenderer(renderer, stage, name);
        Reloaded(api);
        Links.ModStarted(api, mod.Id);
        api.Logger?.Notification("Komet: shader mod {0} resumed", mod.Id);
        Changed?.Invoke();
        return true;
    }

    private static bool Owned(IRenderer? renderer, HashSet<System.Reflection.Assembly> assemblies) => renderer switch
    {
        null => false,
        DummyRenderer dummy => dummy.action?.Method.DeclaringType?.Assembly is { } own && assemblies.Contains(own),
        _ => assemblies.Contains(renderer.GetType().Assembly)
    };

    // The mods' files and the shaders read again: Swap (on the load) decides which mod's file each shader gets
    private static void Reloaded(ICoreClientAPI api)
    {
        _ = api.Assets?.Reload(AssetCategory.shaders);
        _ = api.Assets?.Reload(AssetCategory.shaderincludes);
        KometShaders.Reload();
    }

    private static readonly Action<ModSystem, ICoreClientAPI>[] Phases =
    [
        (s, a) => s.StartPre(a), (s, a) => s.Start(a), (s, a) => s.AssetsLoaded(a), (s, a) => s.AssetsFinalize(a),
        (s, a) => s.StartClientSide(a)
    ];

    private static bool Run(ICoreClientAPI api, ModSystem system, Action<ModSystem, ICoreClientAPI> phase)
    {
        try
        {
            phase(system, api);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            api.Logger?.Error("Komet: {0} failed to start late: {1}", system, e);
            return false;
        }
    }

    // A dependency the mod was built for that is missing (Missing) or loaded in another major version
    public static (string Name, string Wanted, string Loaded)? Mismatch(ICoreAPI api, string id)
    {
        if (api?.ModLoader is not { } loader || loader.GetMod(id)?.Info?.Dependencies is not { } dependencies) return null;
        foreach (var dependency in dependencies.Bounded(32))
        {
            if (dependency.ModID is "game" or "survival" or "creative" || dependency.Version is not { Length: > 0 } wanted || wanted == "*")
                continue;
            var loaded = loader.GetMod(dependency.ModID)?.Info;
            if (loaded is null) return (dependency.ModID, wanted, "");
            if (Major(wanted) != Major(loaded.Version)) return (loaded.Name, wanted, loaded.Version);
        }

        return null;
    }

    private static string Major(string? version) => version?.Split('.', 2)[0].Trim() ?? "";

    // The session ends (Komet's Dispose): the next one decides anew
    public static void Release()
    {
        Pauses.Clear(); // the world goes: a paused mod is held back from the start of the next
        new Harmony(HoldId).UnpatchAll(HoldId);
        Off.Clear();
    }

    private static void Tell()
    {
        if (_api is not { } api || Off.Count == 0) return;
        var names = All(api).Where(m => Off.Contains(m.Id)).Select(m => m.Name);
        api.ShowChatMessage(Lang.Get("komet:hud-shaders-held", string.Join(", ", names)));
    }

    // Mods with shader files, in load order
    public static List<Found> All(ICoreAPI api)
    {
        List<Found> found = [];
        if (api?.ModLoader?.Mods is not { } mods) return found;
        foreach (var mod in mods.Bounded(MaxMods))
        {
            if (mod?.Info is not { } info || info.ModID == KometMod.Id || mod.SourcePath is not { Length: > 0 } source) continue;
            if (mod.SourceType is not (EnumModSourceType.ZIP or EnumModSourceType.Folder)) continue;
            if (!Seen.TryGetValue(source, out var known)) Seen[source] = known = Scan(api, mod);
            if (known is not null) found.Add(known);
        }

        return found;
    }

    // How its programs fare: (programs, failed in OpenGL, failed for Vulkan with the first reason, still translating)
    public static (int Programs, string[] Gl, (string Pass, string Why)[] Vulkan, int Pending) State(Found mod)
    {
        if (!NotNull(mod)) return (0, [], [], 0);
        var mine = Programs.Values.Where(p => Belongs(mod, p)).Take(MaxPrograms).ToList();
        var vulkan = KometShaders.Renderer == KometRenderer.Vulkan;
        List<(string, string)> failed = [];
        var pending = 0;
        foreach (var program in mine.Bounded(MaxPrograms))
        {
            if (!vulkan || !program.Compiled) continue;
            if (!Ported.TryGetValue(program.Pass, out var why))
            {
                pending++;
                Port(program);
            }
            else if (why.Length > 0) failed.Add((program.Pass, why));
        }

        return (mine.Count, [.. mine.Where(p => !p.Compiled).Select(p => p.Pass)], [.. failed], pending);
    }

    // Its file is the mod's: a replaced game file, one of the mod's own programs, or a replaced include it reads
    private static bool Belongs(Found mod, Program program)
    {
        var (domain, pass) = (program.Domain.ToLowerInvariant(), program.Pass.ToLowerInvariant());
        var file = domain == "game" && Stages.Any(stage => mod.Replaced.Contains("shaders/" + pass + stage));
        return file || mod.Own.Contains(domain + ":" + pass) || Stages.Any(stage => mod.Elsewhere.Contains(domain + ":shaders/" + pass + stage)) ||
               program.Includes.Any(i => mod.Replaced.Contains("shaderincludes/" + i) || mod.Replaced.Contains("shaders/" + i));
    }

    // A program the scene has not translated yet: its sources read here (GL), the port on a worker
    private static void Port(Program program)
    {
        if (program.Id <= 0 || !Porting.Add(program.Pass)) return;
        var (port, error) = Links.ReadPort(program.Id, program.Pass);
        if (port is null)
        {
            Ported[program.Pass] = error.Length > 0 ? error : "its sources could not be read";
            return;
        }

        var (pass, key, kept) = (program.Pass, program.Domain + ":" + program.Pass, program.Kept);
        _ = Task.Run(() =>
        {
            try
            {
                port(); // records its outcome in Ported
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a translator that throws fails this port: else the faulted task goes unseen and the page says
                // "translating" for good
                Ported[pass] = "the port threw: " + e.Message;
            }

            var api = _api;
            api?.Event?.EnqueueMainThreadTask(() => Finished(key, pass, kept), "komet-shader-mods");
        });
    }

    // A port outlived by a recompile (Keep) wrote the old program's outcome: dropped, so State ports the new one
    private static void Finished(string key, string pass, int kept)
    {
        if (!Programs.TryGetValue(key, out var now) || now.Kept != kept)
        {
            _ = Ported.TryRemove(pass, out _);
            _ = Porting.Remove(pass);
        }

        Changed?.Invoke();
    }

    // The registry's programs as they stand (those compiled before Komet's patch)
    private static void Read()
    {
        var programs = Registry?.Invoke() ?? [];
        foreach (var program in programs.Bounded(MaxPrograms))
            if (program is { PassName: { Length: > 0 } })
                Keep(program, program.ProgramId > 0 && !program.LoadError);
    }

    private static void AfterCompile(ShaderProgram __instance, bool __result)
    {
        if (__instance is { PassName: { Length: > 0 } }) Keep(__instance, __result);
    }

    // A shader stage that does not compile: its source as the driver got it (the engine's prefix after #version) goes to
    // Logs/komet-shader-<file>.txt once a session, so the error's line number points into it
    private static void AfterStage(Vintagestory.Client.NoObf.Shader shader, bool __result)
    {
        if (__result || shader?.Code is not { } code || FileName?.Invoke(shader) is not { Length: > 0 } name || !Dumped.Add(name)) return;
        var at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
        var text = code.Insert(at, shader.PrefixCode ?? "");
        try
        {
            var file = Path.Join(GamePaths.Logs, "komet-shader-" + string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".txt");
            File.WriteAllText(file, text);
            _api?.Logger?.Notification("Komet shaders: the source of {0} that did not compile is in {1}", name, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _api?.Logger?.Warning("Komet shaders: the source of {0} could not be written: {1}", name, e.Message);
        }
    }

    private static void Keep(ShaderProgram program, bool compiled)
    {
        var domain = program.AssetDomain ?? "game";
        _kept++;
        Programs[domain + ":" + program.PassName] =
            new Program(program.ProgramId, domain, program.PassName, [.. program.includes.Take(64)], compiled, _kept);
        _ = Ported.TryRemove(program.PassName, out _); // compiled anew: its port is due again
        _ = Porting.Remove(program.PassName);
    }

    // Before the registry reads its files: a switched-off mod's replacements give way to the install's originals
    // Running mods' Komet versions take their files' place; held-back mods' replaced files give way to the game's
    private static void Loading()
    {
        if (_api is { } api) Swap(api);
    }

    // Also from Komet's AssetsLoaded, before any other mod starts: a mod that reads the game's shader files itself at its
    // start (SheyderMod reads its includes) must already find what the registry will
    public static void Swap(ICoreClientAPI api)
    {
        if (!NotNull(api)) return;
        var mods = All(api);
        foreach (var mod in mods.Bounded(MaxMods))
        {
            if (Off.Contains(mod.Id))
            {
                var restored = 0;
                foreach (var file in mod.Replaced.Bounded(MaxEntries)) restored += Restore(api, mod, file, Running(mods, file)) ? 1 : 0;
                foreach (var entry in mod.Elsewhere.Bounded(MaxEntries)) restored += Give(api, mods, mod, entry) ? 1 : 0;
                if (Told.Add(mod.Id))
                    api.Logger?.Notification("Komet shaders: {0} is held back, {1} of its {2} replaced files give way", mod.Id, restored, mod.Replaced.Length + mod.Elsewhere.Length);
            }
            else
                foreach (var variant in mod.Variants.Bounded(MaxEntries)) Stand(api, mod, variant);
        }
    }

    // domain:category/file gets the bytes of domain:config/komet/category/file
    private static void Stand(ICoreClientAPI api, Found mod, string variant)
    {
        if (variant.Split(':', 2) is not [var domain, var file] || api.Assets is not { } assets) return;
        if (assets.TryGet(new AssetLocation(domain, VariantPath + file)) is not { Data: { } data })
            return;
        if (assets.TryGet(new AssetLocation(domain, file)) is { } target) target.Data = data;
        else api.Logger?.Warning("Komet: {0}'s Komet version {1} has no {2} to stand in for", mod.Id, VariantPath + file, domain + ":" + file);
    }

    // A file a held mod replaced in another mod's domain: a running mod's replacement of it, else the owner's own file
    private static bool Give(ICoreClientAPI api, List<Found> mods, Found mod, string entry)
    {
        if (entry.Split(':', 2) is not [var domain, var file] || api.Assets?.TryGet(new AssetLocation(domain, file)) is not { Data: { } data } asset)
            return false;
        try
        {
            if (Bytes(mod, domain, file) is not { } mine || !data.AsSpan().SequenceEqual(mine)) return false;
            Found? instead = null;
            foreach (var other in mods.Bounded(MaxMods))
                if (!Off.Contains(other.Id) && (other.Elsewhere.Contains(entry) || other.Id == domain)) instead = other;
            if (instead is null || Bytes(instead, domain, file) is not { } theirs) return false;
            asset.Data = theirs;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: {0}'s own {1} could not be read back: {2}", domain, file, e.Message);
            return false;
        }
    }

    // The last running mod (in load order) that replaces the file too: its version stands in, not the game's
    private static Found? Running(List<Found> mods, string file)
    {
        Found? last = null;
        foreach (var other in mods.Bounded(MaxMods))
            if (!Off.Contains(other.Id) && other.Replaced.Contains(file)) last = other;
        return last;
    }

    private static bool Restore(ICoreClientAPI api, Found mod, string file, Found? instead)
    {
        // The registry reads shaders/ and shaderincludes/ into one include table by file name: an include a mod put under
        // shaders/ (DRTAgX's fogandlight.fsh) has its original under shaderincludes/
        var original = Path.Join(GamePaths.AssetsPath, "game", file);
        if (!File.Exists(original)) original = Path.Join(GamePaths.AssetsPath, "game", "shaderincludes", Path.GetFileName(file));
        if (api.Assets?.TryGet(new AssetLocation("game", file)) is not { Data: { } data } asset) return false;
        try
        {
            if (Bytes(mod, "game", file) is not { } mine || !data.AsSpan().SequenceEqual(mine)) return false; // not the held mod's
            // a running mod that replaces the file too (SheyderMod beside a held DRTAgX) keeps its version
            if (instead is not null && Bytes(instead, "game", file) is { } theirs) asset.Data = theirs;
            else if (File.Exists(original)) asset.Data = File.ReadAllBytes(original);
            else return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: the game's {0} could not be read, {1}'s stays: {2}", file, mod.Id, e.Message);
            return false;
        }
    }

    // The mod's own copy of a game file. The loaded asset is the mod's when the bytes match: the game reads zip mods from
    // an unpacked copy in its cache, so the asset's origin does not name the zip.
    private static byte[]? Bytes(Found mod, string domain, string file)
    {
        var inner = "assets/" + domain + "/" + file;
        if (Directory.Exists(mod.Source))
        {
            var path = Path.Join(mod.Source, inner);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        using var zip = ZipFile.OpenRead(mod.Source);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(inner, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !Assert(entry.Length < 1 << 24)) return null;
        using var stream = entry.Open();
        using var copy = new MemoryStream((int)entry.Length);
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Found? Scan(ICoreAPI api, Mod mod)
    {
        if (!NotNull(mod.SourcePath)) return null;
        try
        {
            if (mod.SourceType == EnumModSourceType.Folder)
            {
                var root = Path.Join(mod.SourcePath, "assets");
                if (!Directory.Exists(root)) return null;
                var list = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => "assets/" + Path.GetRelativePath(root, f)).ToList();
                var (replaced, own) = Tally(list, mod.Info?.ModID);
                return Made(mod, replaced, own, Declares(list), KometVersions(list), Foreign(list, mod.Info?.ModID));
            }

            using var zip = ZipFile.OpenRead(mod.SourcePath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            var (zipped, owned) = Tally(entries, mod.Info?.ModID);
            return Made(mod, zipped, owned, Declares(entries), KometVersions(entries), Foreign(entries, mod.Info?.ModID));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: the files of {0} could not be read: {1}", mod.Info?.ModID, e.Message);
            return null;
        }
    }

    private static Found? Made(Mod mod, string[] replaced, string[] own, bool declared, string[] variants, string[] foreign)
    {
        if (replaced.Length + own.Length + variants.Length + foreign.Length == 0 && !declared || !NotNull(mod.Info)) return null;
        var name = mod.Info.Name is { Length: > 0 } named ? named : mod.Info.ModID;
        return new Found(mod.Info.ModID, name, mod.SourcePath, replaced, own, declared) { Variants = variants, Elsewhere = foreign };
    }

    // Shader files under another mod's domain (not game, not the mod's own) as domain:category/file
    internal static string[] Foreign(IEnumerable<string> paths, string? owner)
    {
        List<string> foreign = [];
        var own = owner?.ToLowerInvariant() ?? "";
        foreach (var raw in paths.Bounded(MaxEntries))
            if (raw.Replace('\\', '/').ToLowerInvariant().Split('/') is ["assets", var domain, var category and ("shaders" or "shaderincludes"), .. var rest] &&
                domain != "game" && domain != own && own.Length > 0 && rest.Length > 0 && Shader(rest[^1]))
                foreign.Add(domain + ":" + category + "/" + string.Join('/', rest));
        return [.. foreign];
    }

    // Komet versions in the mod's files: assets/<domain>/config/komet/<shaders|shaderincludes>/<file> as domain:category/file
    internal static string[] KometVersions(IEnumerable<string> paths)
    {
        List<string> variants = [];
        foreach (var raw in paths.Bounded(MaxEntries))
            if (raw.Replace('\\', '/').ToLowerInvariant().Split('/') is ["assets", var domain, "config", "komet", var category and ("shaders" or "shaderincludes"), .. var rest] &&
                rest.Length > 0 && Shader(rest[^1]))
                variants.Add(domain + ":" + category + "/" + string.Join('/', rest));
        return [.. variants];
    }

    // A config/komet.json in any of the mod's domains
    internal static bool Declares(IEnumerable<string> paths) =>
        NotNull(paths) && paths.Take(MaxEntries).Any(raw => raw.Replace('\\', '/').ToLowerInvariant().Split('/') is ["assets", _, "config", "komet.json"]);

    // Replaced: game asset paths in lower case ("shaders/final.fsh"); Own: "domain:pass" of the mod's programs
    internal static (string[] Replaced, string[] Own) Tally(IEnumerable<string> paths, string? owner = null)
    {
        List<string> replaced = [];
        HashSet<string> own = [];
        foreach (var raw in paths.Bounded(MaxEntries))
        {
            var path = raw.Replace('\\', '/').ToLowerInvariant(); // a zip packed on Windows may say assets\game\shaders
            if (!Shader(path) || path.Split('/') is not ["assets", var domain, var category, .. var rest] || rest.Length == 0) continue;
            if (domain == "game" && category is "shaders" or "shaderincludes") replaced.Add(category + "/" + string.Join('/', rest));
            else if (domain != "game" && category == "shaders" && (owner is null || domain.Equals(owner, StringComparison.OrdinalIgnoreCase)))
                _ = own.Add(domain + ":" + Path.GetFileNameWithoutExtension(rest[^1]));
        }

        return ([.. replaced], [.. own]);
    }

    private static bool Shader(string path) =>
        path.EndsWith(".fsh", StringComparison.Ordinal) || path.EndsWith(".vsh", StringComparison.Ordinal) ||
        path.EndsWith(".gsh", StringComparison.Ordinal) || path.EndsWith(".ash", StringComparison.Ordinal);
}

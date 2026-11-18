using System.Collections.Concurrent;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Rendering;

// Shader mods that know nothing of Komet (from the mod database: SheyderMod, a tone-mapping mod replacing final.fsh), found
// by their files: replaced game shaders under assets/game/shaders/ or shaderincludes/, own programs under
// assets/<domain>/shaders/. Each is listed under Active shaders with how its programs fare: compiled by OpenGL (every
// Compile is watched, the ones before Komet read from the registry) and translated for Vulkan (ports are started for any
// the scene has not drawn yet). Switched off, the game's own shader files stand in for the ones the mod replaces: before
// the registry reads its files, the assets the mod won get the install's original bytes. Every mod runs until the player
// switches it off (shader-settings.json "stopped"); a held-back mod's systems skip every phase from Komet's StartPre
// (ModLoader.TryRunModPhase), and its replaced files give way, so the game draws as without it. Switched on again,
// it starts at once (its systems' phases run late, in the game's order, then the shaders reload); switched off, it pauses
// at once (Pause: its renderers and patches out, the game's files back; a mod of shader files alone only swaps files). A mod built for another major version of a mod it
// depends on is never started: DRTAgX 2.1.4 beside SheyderMod 2.0.0 stops the game. A mod other mods depend on is never
// held back (Dependents): its switch is locked on.
internal static partial class ShaderMods
{
    private const int
        MaxMods = 512, MaxEntries = 65536, MaxPrograms = 1024, MaxChain = 8; // MaxChain: dependents of dependents

    // Where a mod keeps the version of a shader file meant for Komet: assets/<domain>/config/komet/shaders/<file> stands in
    // for assets/<domain>/shaders/<file> (shaderincludes alike) while Komet runs. The game never reads config/ as shaders.
    public const string VariantPath = "config/komet/";

    // The mod's declaration for Komet, read from its files before the assets load
    public const string Manifest = "config/komet.json";
    private const string HoldId = "komet-hold";

    private static readonly string[] Stages = [".vsh", ".fsh", ".gsh"];

    // null when an engine renames or retypes it: a throw here would fail the type, HoldBack in StartPre included
    private static readonly AccessTools.FieldRef<ShaderProgram?[]?>? Registry =
        AccessTools.Field(typeof(ShaderRegistry), "shaderPrograms") is { IsStatic: true } registry &&
        registry.FieldType == typeof(ShaderProgram[])
            ? AccessTools.StaticFieldRefAccess<ShaderProgram?[]?>(registry)
            : null;

    private static int _kept;

    // The registry's programs by PassId (the Shader Studio's draw count names them)
    internal static ShaderProgram?[]? Registered => Registry?.Invoke();

    // A program by name, the engine's built-in ones too: those (sky, chunkopaque …, registered by EnumShaderProgram)
    // are missing from the engine's by-name table, so GetProgramByName alone returns null for them
    internal static ShaderProgram? ProgramByName(ICoreClientAPI api, string name)
    {
        if (api.Shader.GetProgramByName(name) is ShaderProgram named) return named;

        var all = Registered;
        return all is null ? null : Array.Find(all, p => p?.PassName == name);
    }

    private static readonly Dictionary<string, Found?> Seen = [];
    private static readonly Dictionary<string, Program> Programs = [];
    private static readonly HashSet<string> Porting = [];
    private static readonly HashSet<string> Told = []; // held-back mods whose restore was logged this session
    private static readonly HashSet<string> Dumped = []; // stages whose failing source was written this session
    private static readonly Dictionary<string, string> StageErrors = new(StringComparer.OrdinalIgnoreCase);

    private static readonly AccessTools.FieldRef<Shader, string>? FileName =
        AccessTools.Field(typeof(Shader), "Filename") is not null
            ? AccessTools.FieldRefAccess<Shader, string>("Filename")
            : null;

    private static ICoreClientAPI? _api;
    private static SheyderJit? _sheyderJit;

    // Held-back mods with content: their systems run, only their files give way
    private static readonly HashSet<string> Serving = [];

    // Systems whose start was skipped skip their Dispose too, also after Release: Komet goes first at the close, and
    // Rivers' Dispose read the api its skipped StartPre never set
    private static readonly HashSet<ModSystem> Skipped = [];

    private static readonly Dictionary<string, Paused> Pauses = [];

    private static readonly Action<ModSystem, ICoreClientAPI>[] Phases =
    [
        (s, a) => s.StartPre(a), (s, a) => s.Start(a), (s, a) => s.AssetsLoaded(a), (s, a) => s.AssetsFinalize(a),
        (s, a) => s.StartClientSide(a)
    ];

    // The ids held back this session: their systems do not start and their replaced files give way to the game's
    public static HashSet<string> Off { get; } = [];

    // A program translated for Vulkan or not, by pass name ("" when it was): written by the builders, read on the main thread
    public static ConcurrentDictionary<string, string> Ported { get; } = new();

    // Whether the mod runs: every mod runs until the player switches it off. Players asked that no mod need an update or a
    // declaration to work beside Komet (Immersive Foliage, unmaintained mods); the switch is theirs, unless other mods
    // depend on the mod: then it runs either way (its switch is locked on).
    public static bool Wanted(ICoreAPI api, Found mod) =>
        !ShaderState.Of(api).Stopped.Contains(mod.Id) || Dependents(api, mod.Id).Length > 0;

    // Refused off for a mod others depend on
    public static void Want(ICoreAPI api, Found mod, bool on)
    {
        var state = ShaderState.Of(api);
        if (!NotNull(mod) || (!on && Dependents(api, mod.Id).Length > 0)) return;

        _ = state.Stopped.Remove(mod.Id);
        if (!on) state.Stopped.Add(mod.Id);

        ShaderState.Store(api);
    }

    internal static Diagnostic[] Diagnostics(ICoreAPI api)
    {
        var mods = All(api);
        return
        [
            .. Programs.Values.Take(4096).OrderBy(p => p.Domain, StringComparer.Ordinal)
                .ThenBy(p => p.Pass, StringComparer.Ordinal).Select(p =>
                    new Diagnostic(p.Domain, p.Pass, p.Id, p.Compiled, [.. p.Includes],
                        [.. mods.Where(m => Belongs(m, p)).Select(m => m.Id)]))
        ];
    }

    // Something new to show: a port finished
    public static event Action? Changed;

    public static void Install(Harmony harmony, ICoreClientAPI api)
    {
        _api = api;
        Programs.Clear();
        var load = AccessTools.DeclaredMethod(typeof(ShaderRegistry), "loadRegisteredShaderPrograms");
        var compile = AccessTools.DeclaredMethod(typeof(ShaderProgram), nameof(ShaderProgram.Compile));
        if (!NotNull(load) || !NotNull(compile)) return;

        _ = harmony.Patch(load, new HarmonyMethod(typeof(ShaderMods), nameof(Loading)),
            postfix: new HarmonyMethod(typeof(ShaderMods), nameof(RegistryLoaded)));
        _ = harmony.Patch(compile, postfix: new HarmonyMethod(typeof(ShaderMods), nameof(AfterCompile)));
        var stage = AccessTools.DeclaredMethod(typeof(ClientPlatformWindows),
            nameof(ClientPlatformWindows.CompileShader), [typeof(Shader)]);
        if (NotNull(stage))
        {
            _ = harmony.Patch(stage, postfix: new HarmonyMethod(typeof(ShaderMods), nameof(AfterStage)));
        }

        Read();
        if (Off.Count > 0) api.Event.LevelFinalize += Tell;

        _sheyderJit = new SheyderJit(api);
        api.Event.LevelFinalize += PrepareSheyder;
        // the game loaded the mods' files before Komet could step in: once more with the Komet versions and KOMET defined
        ShaderHub.Reload();
        ShaderHub.RendererChanged += Rendered;
    }

    private static void PrepareSheyder()
    {
        if (!Off.Contains("sheydermod")) _sheyderJit?.Prepare();
    }

    // KOMET_VULKAN follows the renderer
    private static void Rendered(object? sender, EventArgs e) => ShaderHub.Reload();

    public static void Clear()
    {
        _validating = false;
        ShaderHub.RendererChanged -= Rendered;
        if (_api?.Event is { } events)
        {
            events.LevelFinalize -= Tell;
            events.LevelFinalize -= PrepareSheyder;
        }

        _sheyderJit = null;
        (_api, Changed) = (null, null);
        Programs.Clear();
        Porting.Clear();
        Told.Clear();
        Dumped.Clear();
        StageErrors.Clear();
        Ported.Clear();
    }

    // Komet's StartPre, before any other mod's Start: shader mods switched off, or missing a mod they need, are held back
    // for this session
    public static void HoldBack(ICoreAPI api, ILogger? logger)
    {
        Release();
        new Harmony(HoldId).UnpatchAll(HoldId);
        Skipped.Clear();
        foreach (var mod in All(api).Bounded(MaxMods))
        {
            if ((mod.Replaces || mod.Declared) && Held(api, mod, logger))
            {
                _ = Off.Add(mod.Id);
                if (mod.Content) _ = Serving.Add(mod.Id);
            }
        }

        if (Off.Count == 0) return;

        var run = AccessTools.DeclaredMethod(typeof(ModLoader), "TryRunModPhase");
        if (!NotNull(run) ||
            new Harmony(HoldId).Patch(run, new HarmonyMethod(typeof(ShaderMods), nameof(Skip))) is null)
        {
            return;
        }

        logger?.Notification(
            "Komet: shader mods held back (switched off under Shaders › Active shaders, or a mod they need is missing): {0}",
            string.Join(", ", Off));
    }

    // A held-back mod's system skips the phase as if it ran (no "failed to start" in the log)
    private static bool Skip(Mod? mod, ModSystem? system, ICoreAPI? api, ModRunPhase phase, ref bool __result)
    {
        if (api?.Side != EnumAppSide.Client || system is null) return true;

        if (phase == ModRunPhase.Dispose && Skipped.Remove(system)) return __result = true;

        if (mod?.Info?.ModID is not { } id || !Off.Contains(id) || Serving.Contains(id)) return true;

        if (phase != ModRunPhase.Dispose) _ = Skipped.Add(system);

        __result = true;
        return false;
    }
}

using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Client;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// Vulkan draws every terrain pass (liquid depth, shadow maps, opaque, transparent, water plants) and, via
// the scene, the rest of the frame; OpenGL rests. Programs are read back from the driver as compiled, keeping engine
// settings and mod changes. The phase is decided only in Before, so a frame is wholly Vulkan's or GL's; switched off,
// Vulkan lets go of the renderer and its memory, and OpenGL draws from what it holds.
internal static partial class VulkanRenderer
{
    public enum Phase
    {
        Off,
        Waiting,
        Running,
        Failed
    }

    public enum Pass
    {
        None = -1,
        LiquidDepth,
        Shadows,
        Opaque,
        Transparent,
        WaterPlants
    }

    // Also: the pass the program draws after the transparent terrain too (chunkopaque the water plants)
    private static readonly (string Name, Pass Pass, Pass Also)[] Programs =
    [
        ("chunkliquiddepth", Pass.LiquidDepth, Pass.None), ("chunkshadowmap", Pass.Shadows, Pass.None),
        ("chunkopaque", Pass.Opaque, Pass.WaterPlants), ("chunktopsoil", Pass.Opaque, Pass.None),
        ("chunkliquid", Pass.Transparent, Pass.None), ("chunktransparent", Pass.Transparent, Pass.None)
    ];

    // Uniforms that change with every pool: push constants
    private static readonly HashSet<string> Hot = new(["origin"], StringComparer.Ordinal);
    private static readonly GlslPort.Ported?[] Ports = new GlslPort.Ported?[Programs.Length];
    private static readonly int[] Built = new int[Programs.Length]; // the GL program each port was made from
    private static readonly Build<GlslPort.Ported>?[] Porting = new Build<GlslPort.Ported>?[Programs.Length];
    private static readonly ShaderProgramBase?[] EngineNow = new ShaderProgramBase?[Programs.Length];

    private static ILogger? _logger;
    private static ICoreClientAPI? _api;
    private static TerrainRenderer? _renderer;
    private static string _failure = "";
    private static bool _failed;
    private static bool _taking; // Vulkan draws every terrain pass this frame

    // The wait for the GPU at the frame's start instead of in its middle (VulkanFrame.Pace)
    public static bool Pacing { get; set; } = true;

    // Refreshes the engine misses are shown turned as the camera goes (FrameBridge)
    public static bool Bridging { get; set; }

    public static Phase State { get; private set; }
    public static string Status { get; private set; } = "";

    public static bool Unavailable => VulkanCore.Failed;

    // FramePacer's, at window_RenderFrame's start
    public static void Paced()
    {
        if (!Pacing || State != Phase.Running || _renderer is not { } renderer) return;
        renderer.Frame.Pace();
    }

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        (_logger, _api) = (logger, api);
        ShadercFetch.Joined(logger);
        Register(api.Event);
        if (GlTap.Tapped) GlTap.Linking = EarlyLinked; // ports begin as the world loads, before the first frame
        if (Only && GlTap.Tapped) // and those the game linked before Komet installed
            foreach (var (id, _) in ProgramPorts.Linked().Bounded(1 << 10)) EarlyLinked((uint)id);
        Patch(harmony);
        VulkanWatch.Watching = () => State == Phase.Running;
        VulkanWatch.Start();
        Recorded();
    }

    public static void Stop()
    {
        Unhook();
        Unpushed();
        VulkanWatch.Stop();
        Unleaned();
        Release();
        GlTap.Untap();
        (_failed, _failure, State, Status) = (false, "", Phase.Off, "");
        if (_held) (_only, _held) = (false, false); // switched off while lean could not give back: off with the world
        GlTap.GuiStage = false;
        foreach (var porting in Porting.Bounded(Programs.Length)) porting?.Drop();
        Array.Clear(Porting);
        Array.Clear(Ports);
        Array.Clear(Built);
        Array.Clear(EngineNow); // the world's programs go with it
        (_taking, _api) = (false, null);
    }

    // Lean's stand-ins would outlive the world: the engine makes its framebuffer textures once per game run, so the next
    // world found them 1x1 (the scene drawn into one pixel: a one-coloured screen, flickering on a server join). OpenGL gets
    // every lean texture and buffer back whole as the world ends, as on a switch-off.
    private static void Unleaned()
    {
        if (GlTap.Leaned == 0 || !GlTap.Tapped || GlTap.Stranded) return;
        GlTap.Lean = false;
        if (Restored("ended with the world")) return;
        _logger?.Error("Komet: Vulkan-only ended with the world, but OpenGL could not be given back the {0} large textures and " +
                       "buffers lean kept from it: restart the game before joining another world", GlTap.Leaned);
    }

    // Every GL name stays the engine's; nothing static keeps the renderer, its scene or their frame reachable, even when
    // letting go throws partway (Forgotten)
    private static void Release()
    {
        if (_renderer is not { } renderer) return;
        try
        {
            // OpenGL hears nothing more of the scene or the renderer
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Querying, GlTap.Answering) = (null, null, null, null, null);
            (GlTap.Blitting, GlTap.Reading, GlTap.TextureClearing, GlTap.Touching) = (null, null, null, null);
            Rendering.FacePacking.Through = null;
            (Rendering.FacePacking.Reserve, Rendering.FacePacking.Commit) = (null, null);
            Culling(renderer, false); // before the backend the passes use goes with the renderer
            SceneGone(); // before the renderer whose frame and programs it uses
            renderer.Dispose();
        }
        finally
        {
            Forgotten();
        }
    }

    // A disposed scene is never wired again (Hooked): the next renderer makes its own and reports from zero
    private static void Forgotten()
    {
        (_renderer, _scene, _parting) = (null, null, false);
        PartOf.Clear();
        Array.Clear(Built); // a new renderer ports again
        (_wired, _slot, _windowShared, _reported, _window) = (default, default, (null, [-1]), default, default);
        Found.Clear();
        WindowFramebuffers.Clear();
        (_logged, _guarded, _waits) = (0, 0, ""); // the next renderer's report counts from its own start
    }

    // The engine's terrain programs in Programs' order, refilled every frame (Begin): one array, not one a frame
    private static ShaderProgramBase?[] Engine()
    {
        (EngineNow[0], EngineNow[1], EngineNow[2]) =
            (ShaderPrograms.Chunkliquiddepth, ShaderPrograms.Chunkshadowmap, ShaderPrograms.Chunkopaque);
        (EngineNow[3], EngineNow[4], EngineNow[5]) =
            (ShaderPrograms.Chunktopsoil, ShaderPrograms.Chunkliquid, ShaderPrograms.Chunktransparent);
        return EngineNow;
    }

    // Ported on a builder and awaited: a pass without its port would be missing from the picture
    internal static void Update(ShaderProgramBase?[] programs, TerrainRenderer? renderer)
    {
        if (!NotNull(programs) || !Assert(programs.Length == Programs.Length)) return;
        for (var i = 0; i < Math.Min(programs.Length, Programs.Length); i++)
        {
            if (programs[i] is { ProgramId: > 0 } program && program.ProgramId != Built[i])
                Port(i, program.ProgramId, renderer);
            if (Porting[i] is { Done: false } waited) _ = waited.Awaited(Hitches.Kind.Port); // else missing
            if (Porting[i] is { Done: true } done) Ported(i, done, renderer);
        }
    }

    private static void Port(int at, int program, TerrainRenderer? renderer)
    {
        Unport(at, renderer); // of the program the engine rebuilt
        Built[at] = program;
        var start = Hitches.Now;
        Porting[at] = ProgramPorts.Started(program, Programs[at].Name, out var error, Hot);
        Hitches.Since(Hitches.Kind.Port, start);
        if (Porting[at] is null) _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name, error);
    }

    private static void Ported(int at, Build<GlslPort.Ported> done, TerrainRenderer? renderer)
    {
        Porting[at] = null;
        var ported = Ports[at] = done.Take(out var error);
        if (ported is not null && renderer?.Watch(Programs[at].Name, Built[at], ported) is { } watched)
        {
            watched.Sources = PipelineRecipes.SourcesOf(Built[at]); // the pipelines it drew with at earlier starts begin now
            renderer.Warm(watched);
        }
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name,
            ported is null ? error : $"{FrameClock.ToMs(done.Ticks):0} ms {done.Where}");
    }

    // The program at goes with its port and any port being made: Update ports it anew
    private static void Unport(int at, TerrainRenderer? renderer)
    {
        if (Built[at] > 0)
        {
            _scene?.Forget(Built[at]);
            renderer?.Forget(Built[at]);
        }

        Porting[at]?.Drop();
        (Built[at], Ports[at], Porting[at]) = (0, null, null);
    }

    // TerrainRebuild's Vulkan side before pools go: frame closed, GPU idle, pending pool writes dropped (pools go with
    // their VAO, Disposing)
    internal static void Rebuilding()
    {
        if (_renderer is not { } renderer) return;
        if (renderer.Frame.Open) renderer.Frame.Close("the terrain rebuilt");
        renderer.PoolWritesDropped();
        _ = renderer.Device.WaitIdle();
    }

    // None for what is no terrain pass's (Komet's distant shadow cascade after the far map: the scene's)
    internal static Pass PassOf(int program, EnumRenderStage stage)
    {
        if (program <= 0) return Pass.None;
        var at = Array.IndexOf(Built, program);
        if (at < 0 || Ports[at] is null) return Pass.None;
        var pass = Programs[at].Pass;
        return (pass, stage) switch
        {
            (Pass.LiquidDepth, EnumRenderStage.Before) => pass,
            (Pass.Shadows, EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear) => pass,
            (Pass.Opaque, EnumRenderStage.Opaque) => pass,
            (Pass.Opaque, EnumRenderStage.AfterOIT) => Programs[at].Also,
            (Pass.Transparent, EnumRenderStage.OIT) => pass,
            _ => Pass.None
        };
    }
}

// MeshDataPoolManager.AddModel makes a chunk pool when none has room (AllocateNewPool) and writes into it at once.
// Adopting it later (Preparing) means copying the written data and waiting for the GPU (a frame in flight), a
// Before-stage stall per new pool while chunks stream. Adopted in AllocateNewPool's postfix the pool is still empty: no
// copy, no wait (TerrainPools.Adopt fresh). Pools refused still go to Preparing. Always on: an exact saving.
// Pool-making bodies are pinned (EngineShape): a write into the new pool there would be lost to fresh adoption.
internal static partial class VulkanRenderer
{
    // EngineShape of BirthSeams() in Vintage Story 1.22.7
    internal const ulong BirthFingerprint = 0x545C1A54CE3C990BUL;

    private const int ChunkPasses = 9;

    private static MeshDataPoolManager? _adding; // the manager whose AddModel runs, main thread
    private static bool _births;

    public static long Born { get; private set; } // pools adopted as they were made, and the time it took
    public static long BirthTicks { get; private set; }

    internal static MethodBase?[] BirthSeams()
    {
        var platform = typeof(ClientPlatformWindows);
        return
        [
            AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.AllocateNewPool)),
            AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.AllocateEmptyMesh)),
            AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.AllocateEmptySSBOMesh))
        ];
    }

    private static void PatchBirths(Harmony harmony)
    {
        (_births, _adding, Born, BirthTicks) = (false, null, 0, 0);
        var seams = BirthSeams();
        var add = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
            [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)]);
        if (!NotNull(add) || !Assert(!Array.Exists(seams, s => s is null)) ||
            !EngineShape.Matches(seams, BirthFingerprint, "Vulkan pool births", _logger)) return;
        _ = harmony.Patch(add, Of(nameof(Adding)), finalizer: Of(nameof(Added)));
        _ = harmony.Patch(seams[0], Of(nameof(Birthing)), Of(nameof(Birth)), finalizer: Of(nameof(Birthed)));
        _births = true;
    }

    // Harmony injects __instance and __result by name; the finalizer clears the manager whatever AddModel did
    private static void Adding(MeshDataPoolManager __instance) => _adding = __instance;

    private static void Added() => _adding = null;

    // A chunk pass's pool is being made: its buffers lean from a smaller size (GlTap.PoolBirth)
    private static void Birthing() => GlTap.PoolBirth = _births && _adding is not null;

    private static void Birthed() => GlTap.PoolBirth = false;

    private static void Birth(MeshDataPool __result)
    {
        GlTap.PoolBirth = false;
        if (!_births || _adding is not { } manager || (State != Phase.Running && !(Only && GlTap.Tapped)) ||
            _renderer is not { } renderer ||
            __result is null || ModelRef(__result) is not VAO vao || _api?.World is not ClientMain game ||
            TerrainOf(game) is not { } chunks) return;
        var pass = PassOf(PassPools(chunks), manager);
        if (pass < 0 || !_taking || !Assert(vao.VaoId > 0)) return;
        var (classic, start) = (Classic(pass), Stopwatch.GetTimestamp());
        Guarded(() =>
        {
            if (!renderer.Born(vao, classic)) return;
            (Born, BirthTicks) = (Born + 1, BirthTicks + Stopwatch.GetTimestamp() - start);
        });
    }

    // The chunk renderer's pass the manager draws, -1 for a manager of someone else's
    internal static int PassOf(MeshDataPoolManager[][]? passes, MeshDataPoolManager manager)
    {
        if (passes is null) return -1;
        for (var pass = 0; pass < Math.Min(passes.Length, ChunkPasses); pass++)
        {
            var managers = passes[pass];
            for (var m = 0; m < Math.Min(managers?.Length ?? 0, MaxManagers); m++)
                if (ReferenceEquals(managers![m], manager))
                    return pass;
        }

        return -1;
    }

    // A pass's pools drawn classic, with their own indices: the liquids always, the others without SSBOs
    private static bool Classic(int pass) =>
        pass == (int)EnumChunkRenderPass.Liquid || !(ScreenManager.Platform?.UseSSBOs ?? true);
}

// Draws/dispatches name their section first; other work (clears, copies, barriers) counts to the one before.
internal static partial class VulkanRenderer
{
    public enum Drawn
    {
        Terrain,
        Sky,
        Entities,
        Particles,
        Post,
        Other,
        Compute
    }

    private const int Whats = 7, MaxStages = 32;
    private static readonly int[] SectionIds = new int[MaxStages * Whats]; // 0: not made yet (0 is the frame's transfers)

    // RenderCost's groups by terrain stage: shadow maps; opaque; transparent with the liquid depth pre-pass and the pass
    // after it
    private static readonly EnumRenderStage[] TerrainStages =
    [
        EnumRenderStage.ShadowFar, EnumRenderStage.ShadowNear, EnumRenderStage.Opaque, EnumRenderStage.Before,
        EnumRenderStage.OIT, EnumRenderStage.AfterOIT
    ];

    private static readonly int[] TerrainGroups = [0, 0, 1, 2, 2, 2], TerrainSections = [-1, -1, -1, -1, -1, -1];

    internal static void Mark(Drawn what)
    {
        if (_renderer is not { Frame.Timing: true } renderer || _api?.World is not ClientMain game) return;
        var stage = (int)Stage(game);
        if (!Index(stage, MaxStages)) return;
        var at = stage * Whats + (int)what;
        if (SectionIds[at] == 0) SectionIds[at] = VulkanFrame.SectionOf(Group(Stage(game)) + " " + Named(what));
        renderer.Frame.Section(SectionIds[at]);
    }

    // Vulkan drawing opaque terrain: HUD terrain GPU rows of the last timed frame, in RenderCost groups; false under GL
    public static bool TerrainGpuMs(Span<double> groups) => TimesTerrain && TerrainMs(_renderer!.Frame, groups);

    public static bool TimesTerrain => State == Phase.Running && _taking && _renderer is not null;

    // Opaque terrain pipeline statistics (Occlusion's order) and area of the last counted frame; NaN under GL
    public static double TerrainStatistic(int at) => TimesTerrain ? _renderer!.Frame.LastStatistic(at) : double.NaN;

    public static double TerrainPixels => TimesTerrain ? _renderer!.Frame.CountedPixels : double.NaN;

    // Whole-frame GPU time (Vulkan busy, last timed frame) while Vulkan draws all incl. the window; else NaN
    public static double FrameGpuMs => State == Phase.Running && _windowing && _renderer is { } renderer
        ? renderer.Frame.LastBusyMs
        : double.NaN;

    internal static bool TerrainMs(VulkanFrame frame, Span<double> groups)
    {
        if (!NotNull(frame) || !Assert(groups.Length >= 3)) return false;
        groups.Clear();
        for (var i = 0; i < TerrainStages.Length; i++)
        {
            if (TerrainSections[i] < 0)
                TerrainSections[i] = VulkanFrame.SectionOf(Group(TerrainStages[i]) + " " + Named(Drawn.Terrain));
            groups[TerrainGroups[i]] += frame.LastMs(TerrainSections[i]);
        }

        return true; // NaN before the first timed frame, as OpenGL's rows
    }

    private static string Group(EnumRenderStage stage) => stage switch
    {
        EnumRenderStage.Before => "before",
        EnumRenderStage.ShadowFar => "shadow far",
        EnumRenderStage.ShadowNear => "shadow near",
        EnumRenderStage.Opaque => "opaque",
        EnumRenderStage.OIT => "transparent",
        EnumRenderStage.AfterOIT => "after transparent",
        EnumRenderStage.AfterPostProcessing or EnumRenderStage.AfterBlit => "post",
        EnumRenderStage.AfterFinalComposition or EnumRenderStage.Ortho or EnumRenderStage.Done => "window",
        _ => stage.ToString().ToLowerInvariant()
    };

    private static string Named(Drawn what) => what switch
    {
        Drawn.Terrain => "terrain",
        Drawn.Sky => "sky",
        Drawn.Entities => "entities",
        Drawn.Particles => "particles",
        Drawn.Post => "post-processing",
        Drawn.Compute => "compute",
        _ => "other"
    };
}

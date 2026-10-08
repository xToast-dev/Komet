using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// LiquidDepth and Shadows are deferred (they commute with OpenGL's work); Opaque (after the sky, before the entities),
// Transparent (after the clouds) and WaterPlants (before the decals) are ordered, handed off right after
// ChunkRenderer.RenderOpaque, RenderOIT and RenderAfterOIT. The programs are read back from the driver as the engine
// compiled them, so its settings and other mods' changes carry over. The phase is decided in the Before stage only, so a
// frame is Vulkan's or OpenGL's from its start; Off keeps what Vulkan holds for switching on again.
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

    private static ILogger? _logger;
    private static ICoreClientAPI? _api;
    private static TerrainRenderer? _renderer;
    private static string _failure = "";
    private static bool _failed;

    public static bool Enabled { get; set; }
    public static bool Opaque { get; set; } = true;
    public static bool Shadows { get; set; } = true;
    public static bool LiquidDepth { get; set; } = true;
    public static bool Transparent { get; set; } = true;

    // The handoff left out where OpenGL touched nothing shared since the last one (VulkanFrame.Lazy), with the scene on
    public static bool LazyHandoff { get; set; } = true;
    public static bool WaterPlants { get; set; } = true;

    // The opaque terrain and, with the scene on, the shadow passes test and write a depth of Komet's own (TerrainRenderer.Owned):
    // what lies behind is rejected before it is shaded
    public static bool OwnDepth { get; set; } = true;

    public static Phase State { get; private set; }
    public static string Status { get; private set; } = "";

    public static bool Unavailable => VulkanCore.Failed;

    private static readonly bool[] Taking = new bool[5];

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        (_logger, _api) = (logger, api);
        Register(api.Event);
        Patch(harmony);
        VulkanWatch.Watching = () => State == Phase.Running;
        VulkanWatch.Start();
    }

    public static void Stop()
    {
        _ = Assert(Built.Length == Programs.Length);
        Unhook();
        VulkanWatch.Stop();
        Release();
        GlTap.Untap();
        (_failed, _failure, State, Status) = (false, "", Phase.Off, "");
        foreach (var porting in Porting.Bounded(Programs.Length)) porting?.Drop();
        Array.Clear(Porting);
        Array.Clear(Ports);
        Array.Clear(Built);
        Array.Clear(Taking);
    }

    // Every GL name stays the engine's
    private static void Release()
    {
        if (_renderer is not { } renderer) return;
        Culling(renderer, false); // before the backend the passes use goes with the renderer
        SceneGone(); // before the renderer whose frame and programs it uses
        renderer.Dispose();
        _renderer = null;
        Array.Clear(Built); // a new renderer ports again
        _ = Assert(Built.Length == Programs.Length);
    }

    private static ShaderProgramBase?[] Engine() =>
        Assert(Programs.Length == 6)
            ? [ShaderPrograms.Chunkliquiddepth, ShaderPrograms.Chunkshadowmap, ShaderPrograms.Chunkopaque,
                ShaderPrograms.Chunktopsoil, ShaderPrograms.Chunkliquid, ShaderPrograms.Chunktransparent]
            : [];

    // Ported on a builder: until the port is done its pass stays OpenGL's (PassOf finds no port)
    internal static void Update(ShaderProgramBase?[] programs, TerrainRenderer? renderer, ReadOnlySpan<bool> passes)
    {
        if (!NotNull(programs) || !Assert(programs.Length == Programs.Length)) return;
        for (var i = 0; i < Math.Min(programs.Length, Programs.Length); i++)
        {
            if (Needed(i, passes) && programs[i] is { ProgramId: > 0 } program && program.ProgramId != Built[i])
                Port(i, program.ProgramId, renderer);
            if (Porting[i] is { Done: true } done) Ported(i, done, renderer);
        }
    }

    private static void Port(int at, int program, TerrainRenderer? renderer)
    {
        if (!Index(at, Programs.Length) || !Assert(program > 0)) return;
        renderer?.Forget(Built[at]);
        Porting[at]?.Drop(); // of the program the engine rebuilt
        (Built[at], Ports[at]) = (program, null);
        var start = Hitches.Now;
        Porting[at] = ProgramPorts.Started(program, Programs[at].Name, out var error, Hot);
        Hitches.Since(Hitches.Kind.Port, start);
        if (Porting[at] is null) _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name, error);
    }

    private static void Ported(int at, Build<GlslPort.Ported> done, TerrainRenderer? renderer)
    {
        if (!Index(at, Programs.Length) || !NotNull(done)) return;
        Porting[at] = null;
        var ported = Ports[at] = done.Take(out var error);
        if (ported is not null) _ = renderer?.Watch(Programs[at].Name, Built[at], ported);
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name,
            ported is null ? error : $"{FrameClock.ToMs(done.Ticks):0} ms off the engine's thread");
    }

    private static bool Needed(int at, ReadOnlySpan<bool> passes) =>
        Index(at, Programs.Length) && Assert(passes.Length == Taking.Length) &&
        (passes[(int)Programs[at].Pass] || (Programs[at].Also != Pass.None && passes[(int)Programs[at].Also]));

    // None for what stays OpenGL's (Komet's distant shadow cascade after the far map)
    internal static Pass PassOf(int program, EnumRenderStage stage)
    {
        if (!Assert(program >= 0) || program == 0) return Pass.None;
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

    internal static SegmentSync SyncOf(Pass pass) =>
        Assert(pass != Pass.None) && pass is Pass.Opaque or Pass.Transparent or Pass.WaterPlants
            ? SegmentSync.Ordered
            : SegmentSync.Deferred;

    // The opaque pass samples the liquid depth and the shadow maps, so all four are shared whichever pass is on
    private static int[] Shared() => (Assert(Taking.Length == 5), SceneOn, Taking[(int)Pass.Transparent]) switch
    {
        (false, _, _) => [],
        (_, true, _) => EveryFramebuffer,
        (_, _, true) => TerrainAndTransparent,
        _ => TerrainFramebuffers
    };

    private const int EngineFramebuffers = 18; // EnumFrameBuffer: Primary 0 to SSAOBlurHorizontalHalfRes 17

    private static readonly int[] EveryFramebuffer = [.. Enumerable.Range(0, EngineFramebuffers)],
        TerrainFramebuffers =
            [TerrainTargets.Primary, TerrainTargets.LiquidDepth, TerrainTargets.ShadowFar, TerrainTargets.ShadowNear],
        TerrainAndTransparent = [.. TerrainFramebuffers, TerrainTargets.Transparent];
}

using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// The scene: every GL draw, clear and blit outside the terrain passes, judged as GL is called (GlTap's Touch group). A
// framebuffer on Vulkan's images stands in for GL's window, so the final blit, AfterBlit, GUI and Done are the scene's
// and the frame ends at the swap, shown by Vulkan's swapchain.
internal static partial class VulkanRenderer
{
    private static readonly Dictionary<string, Drawn> Parts = new(StringComparer.Ordinal)
    {
        ["sky"] = Drawn.Sky, ["nightsky"] = Drawn.Sky, ["celestialobject"] = Drawn.Sky, ["aurora"] = Drawn.Sky,
        ["cloudvolumetric"] = Drawn.Sky, ["cloudmap"] = Drawn.Sky, ["entityanimated"] = Drawn.Entities,
        ["shadowmapentityanimated"] = Drawn.Entities, ["helditem"] = Drawn.Entities, ["instanced"] = Drawn.Entities,
        ["particlescube"] = Drawn.Particles, ["particlesquad"] = Drawn.Particles, ["particlesquad2d"] = Drawn.Particles,
        ["findbright"] = Drawn.Post, ["blur"] = Drawn.Post, ["godrays"] = Drawn.Post, ["ssao"] = Drawn.Post,
        ["bilateralblur"] = Drawn.Post, ["luma"] = Drawn.Post, ["blit"] = Drawn.Post, ["final"] = Drawn.Post,
        ["transparentcompose"] = Drawn.Post, ["colorgrade"] = Drawn.Post
    };

    private static SceneRenderer? _scene;
    private static bool _parting; // the scene takes every part this frame
    private static readonly Dictionary<int, Drawn> PartOf = []; // by GL program, worked out once
    private static bool _windowing; // the window's stand-in is in place this frame

    // Every frame the same list and array while nothing changes: Start takes them for the frame only
    private static readonly List<FrameBufferRef> WindowFramebuffers = [];
    private static (int[]? Of, int[] Made) _windowShared = (null, [-1]);

    // What Hooked wired last: unchanged, it runs every frame without remaking the scene methods' dozen delegates
    private static (SceneRenderer? Scene, TerrainRenderer? Renderer) _wired;
    private static TerrainRenderer.PoolSlot _slot; // the open reservation of ReservedFaces

    private const int EngineFramebuffers = 18; // EnumFrameBuffer: Primary 0 to SSAOBlurHorizontalHalfRes 17

    private static readonly int[] EveryFramebuffer = [.. Enumerable.Range(0, EngineFramebuffers)],
        TerrainAndTransparent =
        [
            TerrainTargets.Primary, TerrainTargets.LiquidDepth, TerrainTargets.ShadowFar, TerrainTargets.ShadowNear,
            TerrainTargets.Transparent
        ];

    private static bool SceneOn => _scene is not null && _parting;

    internal static Drawn PartNamed(string name) =>
        NotNull(name) && Parts.TryGetValue(name, out var part) ? part : Drawn.Other;

    private static bool Takes(int program, string name)
    {
        if (!Assert(program > 0) || _api?.World is not ClientMain) return false;
        if (!PartOf.TryGetValue(program, out var part))
        {
            if (PartOf.Count >= 1024) PartOf.Clear();
            PartOf[program] = part = PartNamed(name);
        }

        var takes = _parting;
        if (takes) Mark(part); // its draw is recorded next
        return takes;
    }

    // Every stage's work and what comes between frames: OpenGL's picture is not shown
    private static bool ClearsTaken() => _api?.World is ClientMain && _parting;

    // Every part: one left to OpenGL would be missing from the picture
    private static void DecideScene()
    {
        _parting = Only;
    }

    // A tap with a gap rebuilds all mirrors (writes went unheard); the record is always tapped
    private static bool Tapped()
    {
        if (GlTap.Tapped) return Assert(!GlTap.Stranded); // Starting leaves a stranded GL alone
        _watched = false;
        _scene?.Mirrors.Forget();
        if (GlTap.Tap()) return Assert(GlTap.Tapped);
        _logger?.Warning("Komet: OpenTK's GL table lacks {0}", GlTap.Missing);
        return false;
    }

    private static void Hooked(TerrainRenderer renderer)
    {
        _scene ??= SceneRenderer.Create(renderer, _logger);
        if (_scene is { } wired && _wired == (wired, renderer) && ReferenceEquals(GlTap.Writes, wired)) return;
        if (_scene is { } scene)
        {
            _wired = (scene, renderer);
            (scene.Takes, scene.Clearing, scene.SamplerObject) = (Takes, ClearsTaken, Sampler);
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Blitting) = (scene.Draw, scene.Clear, scene, scene.Blit);
            (GlTap.Querying, GlTap.Answering, GlTap.Touching) = (scene.Querying, scene.Answer, Touched);
            GlTap.Reading = renderer.Answer; // OpenGL's reads answered from Vulkan's images
            GlTap.TextureClearing = renderer.TextureCleared; // and its images cleared by Vulkan
            (renderer.Frame.Opened, renderer.Frame.Ending) = (scene.Opened, FrameEnding);
            // the engine's own pool uploads, which OpenGL makes too, are copied by Vulkan (PoolWritten)
            Rendering.FacePacking.Through = OnlyFaces;
            (Rendering.FacePacking.Reserve, Rendering.FacePacking.Commit) = (ReservedFaces, CommittedFaces);
        }

        (GlTap.ProgramGone, GlTap.Linking) = (Deleted, Linked);
    }

    // The engine's framebuffers with the window's stand-in added, and the ones Vulkan's images stand for; without the
    // stand-in the engine's own
    private static (IReadOnlyList<FrameBufferRef> Framebuffers, int[] Shared) Windowing(TerrainRenderer renderer,
        IRenderAPI render)
    {
        var shared = Shared();
        _windowing = SceneOn; // only Vulkan's swapchain shows its frames
        if (!_windowing || renderer.Window(render.FrameWidth, render.FrameHeight) is not { } window)
        {
            renderer.Unwindowed();
            _windowing = false;
            return (render.FrameBuffers, shared);
        }

        var engine = render.FrameBuffers;
        WindowFramebuffers.Clear();
        for (var i = 0; i < Math.Min(engine.Count, 63); i++) WindowFramebuffers.Add(engine[i]);
        WindowFramebuffers.Add(window);
        var at = WindowFramebuffers.Count - 1;
        if (!ReferenceEquals(_windowShared.Of, shared) || _windowShared.Made[^1] != at)
            _windowShared = (shared, [.. shared, at]);
        return (WindowFramebuffers, _windowShared.Made);
    }

    // The engine framebuffers Vulkan's images stand for: every one with the scene, else the terrain passes'
    private static int[] Shared() => SceneOn ? EveryFramebuffer : TerrainAndTransparent;

    // Runs with no segment open
    private static void FrameEnding()
    {
        if (_scene is not { } scene || !Assert(_renderer is not null)) return;
        scene.Mirrors.Ending();
        scene.Resolve();
        _renderer!.Ending();
    }

    // A lean pool buffer (OpenGL holds a stand-in and drops the write): the faces go to Vulkan straight, without the two
    // glBindBuffer calls and the tap the GL upload would take (3 % of the main thread at a join)
    private static unsafe bool OnlyFaces(VAO vao, int buffer, long offset, ReadOnlySpan<byte> faces)
    {
        if (_renderer is not { } renderer || buffer <= 0 || !GlTap.LeanBuffer((uint)buffer) || !NotNull(vao)) return false;
        fixed (byte* data = faces) return Assert(offset >= 0) && renderer.PoolWritten((uint)buffer, offset, faces.Length, (IntPtr)data);
    }

    // Faces packed straight into Vulkan's staging (one reservation open at a time, main thread): as OnlyFaces, without
    // the packed copy in between
    private static bool ReservedFaces(VAO vao, int buffer, long offset, int bytes, out IntPtr into)
    {
        (into, _slot) = (IntPtr.Zero, default);
        if (_renderer is not { } renderer || buffer <= 0 || !GlTap.LeanBuffer((uint)buffer) || !NotNull(vao) ||
            !renderer.PoolReserve((uint)buffer, offset, bytes, out var slot)) return false;
        (_slot, into) = (slot, slot.Into);
        return true;
    }

    private static bool CommittedFaces()
    {
        var slot = _slot;
        _slot = default;
        return _renderer is { } renderer && Assert(slot.Size > 0) && renderer.PoolCommitted(slot);
    }

    // GPU work no scene judge saw is missing from the picture (SceneRenderer.Touching)
    private static void Touched(GlTap.Touch kind, uint id)
    {
        if (_renderer is null) return;
        _scene?.Touching(kind, id);
    }

    // GL program names are reused: the port and mirror go with the program
    private static void Deleted(uint program)
    {
        if (!Assert(program > 0) || !Assert(program < int.MaxValue)) return;
        _ = PartOf.Remove((int)program);
        ProgramPorts.Forget((int)program);
        Guarded(static p =>
        {
            var at = Array.IndexOf(Built, p);
            if (at >= 0) Unport(at, _renderer); // a rebuilt terrain program with the same name is ported anew
            _scene?.Forget(p);
            _renderer?.Forget(p);
        }, (int)program);
    }

    private static void SceneGone()
    {
        _scene?.Dispose();
        _scene = null;
        PartOf.Clear();
        _parting = false;
    }
}

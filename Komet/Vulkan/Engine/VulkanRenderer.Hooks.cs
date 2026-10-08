using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;
using Komet.Gpu;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// Before 0.01 decides the frame; Preparing (0.993) runs after the tesselator uploaded and ahead of the liquid depth pass
// (0.995). Every hook catches what Vulkan throws and fails the renderer instead of the game.
internal static partial class VulkanRenderer
{
    private const int MaxGlSamplers = 16;
    private const double PrepareOrder = 0.993, EarliestOrder = -1e9, LastOrder = 1e6, OitOrder = 0.0001;
    private const string StartingMark = "komet-vulkan-pre", StartedMark = "komet-vulkan-start";

    private static readonly Dictionary<int, Samplers.State> GlSamplers = [];
    private static readonly List<IRenderer> Renderers = [];
    private static int _distant = -1;
    private static bool _watched = true;

    private static HarmonyMethod Of(string name)
    {
        var method = AccessTools.Method(typeof(VulkanRenderer), name);
        _ = NotNull(method) && Assert(method.IsStatic);
        return new HarmonyMethod(method);
    }

    private static void Patch(Harmony harmony)
    {
        if (!NotNull(harmony) || !Assert(Programs.Length == Built.Length)) return;
        var platform = typeof(ClientPlatformWindows);
        _ = harmony.Patch(AccessTools.DeclaredMethod(typeof(VAO), nameof(VAO.Dispose)), Of(nameof(Disposing)));
        _ = harmony.Patch(AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.DisposeFrameBuffer)),
            Of(nameof(Unframing)));
        _ = harmony.Patch(AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.BuildMipMaps)),
            postfix: Of(nameof(Mipmapped)));
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)]);
        _ = harmony.Patch(opaque, Of(nameof(OpaqueDrawing)));
        // after OcclusionCulling's postfix (installed before, the same priority): its last pass records into the segment
        foreach (var name in (ReadOnlySpan<string>)[nameof(ChunkRenderer.RenderOpaque), "RenderOIT", "RenderAfterOIT"])
        {
            var pass = AccessTools.DeclaredMethod(typeof(ChunkRenderer), name, [typeof(float)]);
            if (name == nameof(ChunkRenderer.RenderOpaque) || NotNull(pass)) // the opaque pass is required: none fails the install
                _ = harmony.Patch(pass, postfix: new HarmonyMethod(AccessTools.Method(typeof(VulkanRenderer),
                    nameof(PassDrawn))) { priority = Priority.Last });
        }

        IndirectDraw.Takeover = Takeover;
        PoolUpdates.Install(harmony, _logger);
        PatchBirths(harmony);
        var swap = AccessTools.Method(typeof(OpenTK.Windowing.Desktop.GameWindow), "SwapBuffers");
        if (NotNull(swap)) _ = harmony.Patch(swap, Of(nameof(Swapping)));
    }

    private static void Register(IClientEventAPI events)
    {
        if (!NotNull(events) || !Assert(Renderers.Count == 0)) return;
        Renderers.AddRange([
            new Hook(EnumRenderStage.Before, 0.01, Starting), new Hook(EnumRenderStage.Before, PrepareOrder, Preparing),
            new Hook(EnumRenderStage.Opaque, EarliestOrder, OpaqueBegins), new Hook(EnumRenderStage.Done, 0.99, Ended),
            new Hook(EnumRenderStage.OIT, OitOrder, OitBegins), new Hook(EnumRenderStage.ShadowFar, EarliestOrder, Shadowing),
            new Hook(EnumRenderStage.ShadowFarDone, EarliestOrder, Shadowed),
            new Hook(EnumRenderStage.ShadowNear, EarliestOrder, Shadowing),
            new Hook(EnumRenderStage.ShadowNearDone, EarliestOrder, Shadowed)
        ]);
        foreach (var stage in (ReadOnlySpan<EnumRenderStage>)[EnumRenderStage.Opaque, EnumRenderStage.OIT,
                     EnumRenderStage.AfterOIT, EnumRenderStage.AfterPostProcessing,
                     EnumRenderStage.AfterFinalComposition, EnumRenderStage.AfterBlit])
            Renderers.Add(new Hook(stage, LastOrder, () => _renderer?.Frame.Flush()));
        foreach (var renderer in Renderers.Bounded(16))
            events.RegisterRenderer(renderer, ((Hook)renderer).Own, "komet-vulkan");
    }

    private static void Unhook()
    {
        if (IndirectDraw.Takeover == Takeover) IndirectDraw.Takeover = null;
        Renderers.Clear();
        GlSamplers.Clear();
        (_distant, _watched, _oit) = (-1, true, (0, 0));
        GlTap.ProgramGone = null;
        _ = Assert(Renderers.Count == 0);
    }

    private static void Guarded(Action work)
    {
        if (!NotNull(work)) return;
        try
        {
            work();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }
    }

    // For the per-frame hooks: a static lambda with its state allocates nothing, where a capturing one makes a closure each call
    private static void Guarded<T>(Action<T> work, T state)
    {
        if (!NotNull(work)) return;
        try
        {
            work(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }
    }

    private static void Failed(Exception e)
    {
        Fail($"Vulkan failed: {e.GetType().Name}: {e.Message}");
        _logger?.Error("Komet: {0}", e);
        _ = Assert(State is Phase.Failed or Phase.Off);
    }

    private static void Starting()
    {
        var device = VulkanCore.Device;
        if (!Enabled) (_failed, _failure) = (false, ""); // switched off after a failure: switching on tries anew
        if (!Enabled || device is null)
        {
            Paused(Enabled ? "waits for the Vulkan device (render settings, Vulkan)" : "");
            return;
        }

        if (_failed) return;
        VulkanWatch.Mark("Before: starting", _renderer?.Frame.Number ?? -1);
        if (_renderer?.Frame.Fault is { Length: > 0 } fault)
        {
            Fail("Vulkan stopped: " + fault);
            return;
        }

        Guarded(Begin, device);
    }

    private static void Begin(VulkanDevice device)
    {
        if (!NotNull(device) || _api?.Render is not { } render) return;
        DecideScene();
        if (!Tapped(AnyPart))
        {
            Fail("OpenTK's GL table is not where Vulkan looks for it");
            return;
        }

        _renderer ??= Made(device);
        if (_renderer is not { } renderer) return;
        Hooked(renderer, GlTap.Scene);
        if (!_watched) renderer.Rewatch();
        _watched = true;
        var waits = Decide(device);
        Update(Engine(), renderer, Taking);
        waits = Joined(waits, Unported(), SceneWaits());
        var (framebuffers, shared) = Windowing(renderer, render);
        // esr-pre held these: what ran before, then the wait for the GPU to finish the slot's frame (Frame.Next) and the start
        var profiler = Vintagestory.Client.ScreenManager.FrameProfiler;
        profiler?.Mark(StartingMark);
        var started = renderer.Start(framebuffers, shared, out var why);
        profiler?.Mark(StartedMark);
        if (!started)
        {
            Fail("Vulkan terrain: " + why);
            return;
        }

        _ = Distant(renderer);
        Culling(renderer, Taking[(int)Pass.Opaque] && OcclusionCulling.Enabled, Taking[(int)Pass.Opaque]);
        if (_scene is { } scene) (scene.Framing, scene.Between) = (SceneOn, false);
        Report(waits);
        VulkanWatch.Mark("Before: started");
    }

    // Most frames wait for nothing: then no array, iterator or join
    private static string Joined(string a, string b, string c)
    {
        if (!NotNull(a) || !NotNull(b) || !NotNull(c) || a.Length + b.Length + c.Length == 0) return "";
        return string.Join("; ", ((string[])[a, b, c]).Where(w => w.Length > 0));
    }

    private static string SceneWaits()
    {
        if (!AnyPart || !Assert(Parting.Length == 5)) return "";
        if (!GlTap.Scene) return $"the rest of the frame waits: OpenTK's GL table lacks {GlTap.Missing}";
        return _scene is null ? "the rest of the frame waits: no memory for it" : "";
    }

    private static TerrainRenderer? Made(VulkanDevice device)
    {
        var made = TerrainRenderer.Create(device, Math.Max(ClientPlatformAbstract.singleIndexBufferSize / 24, 1 << 20),
            _logger, out var why);
        if (made is null) Fail("Vulkan terrain: " + why);
        else
        {
            (made.Targets.Owned, made.Targets.Renamed) = (OitOwned, OitRenamed);
            made.Frame.Counted = VulkanFrame.SectionOf(Group(EnumRenderStage.Opaque) + " " + Named(Drawn.Terrain));
        }

        Array.Clear(Built); // its programs are ported anew
        return made;
    }

    private static string Decide(VulkanDevice device)
    {
        Taking[(int)Pass.LiquidDepth] = LiquidDepth;
        Taking[(int)Pass.Shadows] = Shadows;
        var culling = OcclusionCulling.Enabled && !device.IndirectCount;
        Taking[(int)Pass.Opaque] = Opaque && !culling;
        Taking[(int)Pass.Transparent] = Transparent;
        Taking[(int)Pass.WaterPlants] = WaterPlants;
        _ = Assert(Taking.Length == 5);
        return Opaque && culling
            ? "the opaque pass waits: OcclusionCulling is on, and the device cannot draw with a count the GPU wrote"
            : "";
    }

    private static string Unported()
    {
        List<string>? missing = null; // made only when one is missing: asked every frame
        for (var i = 0; i < Math.Min(Programs.Length, Ports.Length); i++)
            if (Needed(i, Taking) && Built[i] > 0 && Ports[i] is null && Porting[i] is null) // not still being made
                (missing ??= []).Add(Programs[i].Name);
        _ = Assert(Ports.Length == Programs.Length);
        return missing is null ? "" : $"no SPIR-V port of {string.Join(", ", missing)}, OpenGL draws those (see the log)";
    }

    private static void Preparing()
    {
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is not { } renderer ||
            _api?.World is not ClientMain game || TerrainOf(game) is not { } chunks) return;
        Guarded(static s => s.renderer.Prepare(Adoptable(s.chunks)), (renderer, chunks));
    }

    private static List<(VAO, bool)> Adoptable(ChunkRenderer chunks)
    {
        Found.Clear();
        var passes = PassPools(chunks);
        if (!NotNull(passes) || !Assert(passes.Length < 32)) return Found;
        for (var pass = 0; pass < Math.Min(passes.Length, ChunkPasses); pass++)
        {
            if (passes[pass] is not { } managers || !Takes(pass)) continue;
            for (var m = 0; m < Math.Min(managers.Length, MaxManagers); m++)
            {
                if (managers[m] is not { } manager) continue;
                var pools = Pools(manager);
                for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
                    if (ModelRef(pools[i]) is VAO vao)
                        Found.Add((vao, Classic(pass)));
            }
        }

        _ = Assert(Found.Count <= 8 * MaxManagers * MaxPools);
        return Found;
    }

    private const int MaxManagers = 64, MaxPools = 4096;
    private static readonly List<(VAO, bool)> Found = [];

    // The opaque stage's first renderer: the deferred segment's work goes to the GPU before the sky reads what it drew - unless
    // the scene is on, whose Touch group closes it exactly when OpenGL reads it (the sky is Vulkan's then). The distant shadow
    // map, redrawn in the far shadow pass just now, is copied again before the opaque terrain samples it.
    private static void OpaqueBegins() => Guarded(() =>
    {
        if (_renderer is not { } renderer) return;
        if (!SceneOn) renderer.Frame.Close("the opaque stage begins");
        if (State != Phase.Running || !Distant(renderer)) return;
        renderer.Frame.Close("the distant shadow map redrawn");
        using (GlTap.Quietly()) renderer.Copies.Refresh(renderer.Frame.Number);
    });

    // The opaque terrain's draws test and write Komet's depth (TerrainRenderer.Owned) until its pass is drawn
    private static void OpaqueDrawing()
    {
        if (_renderer is { } renderer) renderer.Owning = OwnDepth && Runs(Pass.Opaque);
        _ = Assert(Taking.Length == 5);
    }

    // The shadow passes' draws and clears test and write Komet's depth too, while the scene judges every OpenGL call beside them
    // (Frame.Beside): OpenGL's entity shadows drawn beside a signalled segment would otherwise land under Komet's copy of the map
    // and be lost as it goes back. Each map's stays Komet's until something reads it or the next map's pass takes over.
    private static void Shadowing()
    {
        if (_renderer is { } renderer)
            renderer.Owning = OwnDepth && Runs(Pass.Shadows) && SceneOn;
        _ = Assert(Taking.Length == 5);
    }

    private static void Shadowed()
    {
        if (_renderer is { } renderer) renderer.Owning = false;
        _ = Assert(_renderer is null || !_renderer.Owning);
    }

    // The ordered segment ends before the entities, the transparent entities and the decals draw over the terrain; with the
    // scene on, Touch closes it where OpenGL draws next instead. Komet's depth goes back first.
    private static void PassDrawn(MethodBase __originalMethod) => Guarded(PassClosed, __originalMethod);

    private static void PassClosed(MethodBase __originalMethod)
    {
        if (__originalMethod?.Name == nameof(ChunkRenderer.RenderOpaque)) _renderer?.Disown();
        if (!SceneOn && NotNull(__originalMethod) && Assert(State != Phase.Failed || _renderer is null))
            _renderer?.Frame.Close(__originalMethod.Name switch
            {
                nameof(ChunkRenderer.RenderOpaque) => "the opaque terrain drawn",
                "RenderOIT" => "the transparent terrain drawn",
                _ => "the water plants drawn"
            });
        _ = Assert(!SceneOn || GlTap.Scene);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "revealTextureId")]
    private static extern ref int Reveal(SystemRenderOITLayers? layers);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "accumTextureId")]
    private static extern ref int Accumulation(SystemRenderOITLayers? layers);

    private static (int Reveal, int Accumulation) _oit;

    // TerrainTargets.Owned and Renamed: the transparent pass's reveal texture and accumulation layers, which
    // SystemRenderOITLayers names in its statics, may be replaced by shared ones - the statics then name those
    private static bool OitOwned(int texture) =>
        Assert(texture > 0) && (texture == Reveal(null) || texture == Accumulation(null));

    private static void OitRenamed(int from, int to)
    {
        if (!Assert(from > 0 && to > 0)) return;
        if (Reveal(null) == from) Reveal(null) = to;
        if (Accumulation(null) == from) Accumulation(null) = to;
        _oit = (Reveal(null), Accumulation(null));
    }

    // Right after the engine's BeforeOIT (order 0), which attaches its reveal texture and accumulation layers to the
    // transparent pass's framebuffer, anew after a resize: new ones are asked for again
    private static void OitBegins()
    {
        if (!Taking[(int)Pass.Transparent] || _renderer is not { } renderer) return;
        var now = (Reveal(null), Accumulation(null));
        if (now == _oit || !Assert(now.Item1 >= 0) || !Assert(now.Item2 >= 0)) return;
        _oit = now;
        Guarded(renderer.ForgetTargets);
    }

    // With the window's stand-in the frame runs on to the buffer swap (Swapping) and ends there instead
    private static void Ended()
    {
        if (_windowing || _renderer is not { } renderer || !Assert(renderer.Frame.Number >= 0)) return;
        VulkanWatch.Mark("Done", renderer.Frame.Number);
        _ = Finished(renderer, null);
    }

    // False when Vulkan presented the frame: the engine's SwapBuffers must not
    private static bool Swapping(OpenTK.Windowing.Desktop.NativeWindow __instance)
    {
        if (!_windowing || _renderer is not { } renderer || !NotNull(__instance)) return true;
        VulkanWatch.Mark("the buffers swap", renderer.Frame.Number);
        _ = Assert(renderer.Frame.Number >= 0);
        return !Finished(renderer, __instance);
    }

    // Ends the frame: shows it in the window when there is one (true once shown), else closes its last segment.
    // Not Guarded: a closure a frame.
    private static bool Finished(TerrainRenderer renderer, OpenTK.Windowing.Desktop.NativeWindow? window)
    {
        if (!NotNull(renderer) || !Assert(window is null || _windowing)) return false;
        if (_scene is { } scene) scene.Framing = false;
        var shown = false;
        try
        {
            _scene?.FlushEarly(); // kept from the frame's start, and no segment opened since: OpenGL clears before the frame shows
            if (window is null) renderer.Frame.Close("the frame's end");
            else shown = renderer.Present(window);
            if (_scene is { } next) next.Between = SceneOn && LazyHandoff && State == Phase.Running;
            if (State is Phase.Running or Phase.Waiting)
            {
                using (GlTap.Quietly())
                    if (_api?.Render is { } render)
                        Probe(renderer, render.FrameBuffers);
                if (!MemoryGuard(renderer)) Periodically(renderer);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }

        _ = Assert(renderer.Frame.Number >= 0);
        return shown;
    }

    private static bool Takeover(MeshRef modelRef, int[] indices, int[] sizes, int count, bool useSSBOs)
    {
        if (modelRef is not VAO vao) return false;
        var pass = Taken(out var renderer);
        if (pass == Pass.None) return false;
        Mark(Drawn.Terrain);
        try // not Guarded: a closure per draw would feed the garbage collector hundreds a frame
        {
            return renderer.Take(vao, (indices, sizes, count, useSSBOs), SyncOf(pass), Sampler);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail($"Vulkan failed drawing: {e.GetType().Name}: {e.Message}");
            _logger?.Error("Komet: {0}", e);
            return false;
        }
    }

    // The culling buffers are made anew with the backend they run on at the next frame
    private static void Culling(TerrainRenderer renderer, bool on, bool framing = false)
    {
        if (!NotNull(renderer)) return;
        GpuBackends.Current = on ? renderer.Compute : GlBackend.Instance;
        GpuBackends.Frame = framing ? renderer.Compute : null;
        OcclusionCulling.Drawer = on ? Counted : null;
        OcclusionCulling.Takes = on ? static () => Taken(out _) != Pass.None : null;
        _ = Assert(on == (OcclusionCulling.Drawer is not null));
    }

    // The pass Vulkan draws the bound program's draw of in now, Pass.None for OpenGL
    private static Pass Taken(out TerrainRenderer renderer)
    {
        renderer = _renderer!;
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is null || _api?.World is not ClientMain game)
            return Pass.None;
        var pass = PassOf(GlTap.Program, Stage(game));
        return pass != Pass.None && Assert(pass >= 0) && Index((int)pass, Taking.Length) && Taking[(int)pass] &&
               renderer.Takes(GlTap.DrawFramebuffer) ? pass : Pass.None;
    }

    private static bool Runs(Pass pass) =>
        State == Phase.Running && Assert(pass != Pass.None) && Index((int)pass, Taking.Length) && Taking[(int)pass];

    private static bool Counted(VAO vao, bool ssbo, (int Buffer, int Offset) commands, (int Buffer, int Offset) count,
        int max)
    {
        VulkanWatch.Mark("culling: a counted draw");
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is not { } renderer || !NotNull(vao) ||
            !Assert(commands.Offset >= 0 && count.Offset >= 0)) return false;
        Mark(Drawn.Terrain);
        try // every pool's draw: no closure for Guarded
        {
            var backend = renderer.Compute;
            var (listed, listedAt) = backend.Native(commands.Buffer);
            var (counted, countedAt) = backend.Native(count.Buffer);
            return renderer.TakeCounted(vao, ssbo,
                ((listed, listedAt + (ulong)commands.Offset), (counted, countedAt + (ulong)count.Offset), max), Sampler);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
            return false;
        }
    }

    // What Vulkan holds stays, so switching on again needs no pool adopted anew
    private static void Paused(string why)
    {
        if (_renderer is { } renderer)
        {
            renderer.Frame.Close("Vulkan switched off");
            renderer.Unwindowed(); // the window is OpenGL's again before GlTap lets go
            Culling(renderer, false);
        }

        _windowing = false;

        Array.Clear(Taking);
        (State, Status) = (_failed ? Phase.Failed : Phase.Off, _failed ? _failure : why);
        if (!GlTap.Tapped) return;
        GlTap.Untap();
        _watched = false;
        _ = Assert(!GlTap.Tapped);
    }

    private static void Fail(string why)
    {
        (_failed, _failure) = (true, NotNull(why) ? why : "?");
        _windowing = false;
        (State, Status) = (Phase.Failed, _failure);
        Array.Clear(Taking);
        try
        {
            Release();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.Error("Komet: Vulkan could not let go cleanly: {0}", e);
        }
        finally
        {
            _renderer = null; // abandoned: OpenGL keeps every name it had
        }

        GlTap.Untap();
        _watched = false;
        _logger?.Warning("Komet: {0}", _failure);
        _ = Assert(_failed);
    }

    // Komet's distant shadow cascade redraws its map with OpenGL, nothing the engine reports: a new map makes the copy stale.
    // The map flips in the far shadow pass, after Before's refresh: true when the copy is to be refreshed again before the
    // opaque terrain samples it
    private static bool Distant(TerrainRenderer renderer)
    {
        if (!NotNull(renderer) ||
            !DistantShadows.Published(out var texture, out var version, out _, out var onTexel)) return false;
        if (version == _distant || !Assert(onTexel.Length == 3)) return false;
        _distant = version;
        if (renderer.Copies.Written(texture)) return false; // Vulkan drew the map into its copy: the copy is ahead
        renderer.Copies.Changed(texture);
        return true;
    }

    private static void Disposing(VAO __instance)
    {
        if (_renderer is { } renderer && NotNull(__instance) && Assert(__instance.VaoId >= 0))
            Guarded(() => renderer.Release(__instance));
    }

    private static void Unframing(FrameBufferRef frameBuffer)
    {
        if (_renderer is { } renderer && NotNull(frameBuffer) && Assert(frameBuffer.FboId >= 0))
            Guarded(() => renderer.Release(frameBuffer));
    }

    // With the scene on, GlTap hears glGenerateMipmap itself
    private static void Mipmapped(int textureId)
    {
        if (_renderer is { } renderer && Assert(textureId >= 0) && !renderer.Copies.Trusting)
            renderer.Copies.Changed(textureId);
    }

    private static readonly System.Func<int, Samplers.State> Sampler = SamplerObject;

    private static Samplers.State SamplerObject(int id)
    {
        if (!Assert(id > 0) || !Assert(GlSamplers.Count <= MaxGlSamplers)) return default;
        if (GlSamplers.TryGetValue(id, out var known)) return known;
        GL.GetSamplerParameter(id, SamplerParameterName.TextureMinFilter, out int min);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureMagFilter, out int mag);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureWrapS, out int wrap);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureCompareMode, out int compare);
        var state = Samplers.FromGl(min, mag, wrap, compare != 0);
        if (GlSamplers.Count < MaxGlSamplers) GlSamplers[id] = state;
        return state;
    }

    private sealed class Hook(EnumRenderStage stage, double order, Action work) : IRenderer
    {
        public EnumRenderStage Own { get; } = stage;
        public double RenderOrder => order;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Assert(stage == Own) && Assert(deltaTime >= 0)) work();
        }

        public void Dispose() => _ = Assert(Renderers.Count <= 16) && Assert(RenderRange == 0);
    }
}

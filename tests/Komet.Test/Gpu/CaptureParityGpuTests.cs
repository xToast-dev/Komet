using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The capture draws the frame twice, as the game does every frame: the second is compared.
[NonParallelizable]
public sealed class CaptureParityGpuTests
{
    private const int Primary = 0, Shadow = 1, Liquid = 2, Side = TerrainScene.Size;
    private static readonly float[] Black = [0, 0, 0, 1]; // as the frame starts

    // Own: drawn on Komet's depth (TerrainRenderer.Owning, as the shadow stages set it)
    private sealed record Step(string Program, int Target, Action State, Dictionary<string, int> Reads,
        bool Gl = false, bool Counted = false, bool Own = false);

    private static readonly Action Opaque = () => Engine(true, true), NoCull = () => Engine(false, false),
        Blended = () => Engine(false, true), DepthOnly = () => Depth(false), LiquidDepth = () => Depth(true);

    private static readonly Dictionary<string, (bool High, Step[] Steps, int Segments)> Cases = new(StringComparer.Ordinal)
    {
        ["opaque passes"] = (false, [
            new("chunkopaque", Primary, Opaque, []), new("chunktopsoil", Primary, Opaque, []),
            new("chunkopaque", Primary, NoCull, []), new("chunkopaque", Primary, Blended, [])
        ], 1),
        // OcclusionCulling's draws: commands and a count the GPU reads, which leaves out the quad partly behind
        ["a draw with a count the GPU wrote"] = (false, [
            new("chunkopaque", Primary, Opaque, [], Counted: true), new("chunktopsoil", Primary, Opaque, [])
        ], 1),
        ["opaque passes with SSAO"] = (true, [
            new("chunkopaque", Primary, Opaque, []), new("chunktopsoil", Primary, Opaque, []),
            new("chunkopaque", Primary, NoCull, [])
        ], 1),
        // the deferred segment holding the shadow map is handed over as the map is bound to be read (in the game the opaque stage
        // closes it before that): two segments
        ["a shadow map drawn, then sampled"] = (true, [
            new("chunkshadowmap", Shadow, DepthOnly, []),
            new("chunkopaque", Primary, Opaque, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow })
        ], 2),
        ["the liquid depth, then the opaque pass"] = (true, [
            new("chunkliquiddepth", Liquid, LiquidDepth, []),
            new("chunkopaque", Primary, Opaque, new() { ["liquidDepth"] = Liquid })
        ], 2),
        ["OpenGL between"] = (false, [
            new("chunkopaque", Primary, Opaque, []), new("chunktopsoil", Primary, Blended, [], Gl: true),
            new("chunkopaque", Primary, NoCull, [])
        ], 2),
        // An OpenGL draw that keeps the GPU busy for a while, then Vulkan's draw over it: Vulkan must wait for it on the GPU
        ["a slow OpenGL draw, then Vulkan's"] = (false, [
            new("slow", Primary, Blended, [], Gl: true), new("chunkopaque", Primary, Opaque, [])
        ], 1),
        // OpenGL reads the shadow map the deferred segment drew: binding it hands the segment over first, the opaque pass opens
        // another
        ["OpenGL reads a shadow map Vulkan drew"] = (true, [
            new("chunkshadowmap", Shadow, DepthOnly, []),
            new("chunkopaque", Primary, Opaque, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow }, Gl: true),
            new("chunkopaque", Primary, NoCull, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow })
        ], 2),
        // OpenGL's depth-only draws into the shadow map and its draw into the primary pass beside the pending segment stay beside
        // it; the opaque pass binding the map hands it over
        ["OpenGL beside a shadow map"] = (true, [
            new("chunkshadowmap", Shadow, DepthOnly, []), new("chunkshadowmap", Shadow, DepthOnly, [], Gl: true),
            new("chunktopsoil", Primary, Opaque, [], Gl: true),
            new("chunkopaque", Primary, Opaque, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow })
        ], 2),
        // the shadow pass on Komet's depth: copied in as the deferred segment's first work, back as the opaque pass binds the map
        ["a shadow map drawn on Komet's depth, then sampled"] = (true, [
            new("chunkshadowmap", Shadow, DepthOnly, [], Own: true),
            new("chunkopaque", Primary, Opaque, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow })
        ], 2),
        // OpenGL's depth-only draw beside the pending segment runs before it, so before the copy in: the map holds both. A
        // counted draw signals the segment, the opaque pass samples the map in it: written back right before
        ["OpenGL beside a shadow map on Komet's depth"] = (true, [
            new("chunkshadowmap", Shadow, DepthOnly, [], Own: true), new("chunkshadowmap", Shadow, DepthOnly, [], Gl: true),
            new("chunkshadowmap", Shadow, DepthOnly, [], Counted: true, Own: true),
            new("chunkopaque", Primary, Opaque, new() { ["shadowMapFar"] = Shadow, ["shadowMapNear"] = Shadow })
        ], 1)
    };

    public static IEnumerable<string> Frames() => Cases.Keys;

    [TestCaseSource(nameof(Frames))]
    [Category("Slow")]
    public void VulkanDrawsTheFrameAsOpenGlDoes(string frame)
    {
        GameInstall.RequireAssets();
        var (high, steps, segments) = Cases[frame];
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        rig.Backend!.Flush();
        var scenes = steps.Select(s => s.Program).Where(p => p != "slow").Distinct()
            .ToDictionary(p => p, p => new TerrainScene(device, high, p) { Counts = rig.Backend });
        using var slow = new SlowGreen();
        try
        {
            var expected = Run(scenes, steps, high, null, slow);
            Assert.That(Visible(expected[Primary][0]), Is.GreaterThan(200), "OpenGL drew the quads");
            Assert.That(GlTap.Tap(), Is.True);
            using var capture = TerrainRenderer.Create(device, 64, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            foreach (var scene in scenes.Values) _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            _ = Run(scenes, steps, high, capture, slow); // the first frame adopts the pools and makes the texture copies
            var before = capture!.Frame.Segments;
            var drawn = Run(scenes, steps, high, capture, slow);
            Assert.That(capture.Left, Is.Zero, string.Join("; ", capture.Reasons));
            Assert.That(capture.Draws, Is.EqualTo(2 * steps.Count(s => !s.Gl)));
            Assert.That(capture.Frame.Segments - before, Is.EqualTo(segments), "segments of a frame");
            for (var target = 0; target < expected.Length; target++)
                for (var i = 0; i < expected[target].Length; i++)
                {
                    var (got, want) = (drawn[target][i], expected[target][i]);
                    Assert.That(Parity.Worst(got, want), Is.LessThanOrEqualTo(Tolerance(target, i)),
                        $"framebuffer {target}, attachment {i}: {Parity.Differing(got, want)}");
                }
        }
        finally
        {
            GlTap.Untap();
            foreach (var scene in scenes.Values) scene.Dispose();
        }
    }

    // Made on builders, as in the game: until a pool's pipeline is done OpenGL draws the pool, and every frame is OpenGL's to the
    // pixel; a draw with a count the GPU wrote waits for its pipeline instead, as OpenGL would not draw it
    [TestCaseSource(nameof(Frames))]
    public void WhileItsPipelinesAreMadeEveryFrameIsOpenGls(string frame)
    {
        GameInstall.RequireAssets();
        var (high, steps, _) = Cases[frame];
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        var device = rig.Device!;
        rig.Backend!.Flush();
        var scenes = steps.Select(s => s.Program).Where(p => p != "slow").Distinct()
            .ToDictionary(p => p, p => new TerrainScene(device, high, p) { Counts = rig.Backend });
        using var slow = new SlowGreen();
        try
        {
            var expected = Run(scenes, steps, high, null, slow);
            Assert.That(GlTap.Tap(), Is.True);
            using var capture = TerrainRenderer.Create(device, 64, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            foreach (var scene in scenes.Values) _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            var (vulkan, frames, all) = (steps.Count(s => !s.Gl), 0, false);
            for (; frames < 1000 && !all; frames++)
            {
                var draws = capture!.Draws;
                var drawn = Run(scenes, steps, high, capture, slow, every: false);
                for (var target = 0; target < expected.Length; target++)
                    for (var i = 0; i < expected[target].Length; i++)
                        Assert.That(Parity.Worst(drawn[target][i], expected[target][i]), Is.LessThanOrEqualTo(Tolerance(target, i)),
                            $"frame {frames}, framebuffer {target}, attachment {i}: {Parity.Differing(drawn[target][i], expected[target][i])}");
                all = capture.Draws - draws == vulkan && Builds.Pending == 0;
                if (frames == 0 && steps.Any(s => s.Counted))
                    Assert.That(capture.Draws - draws, Is.Positive, "the counted draws waited for their pipelines");
            }

            Assert.That(all, Is.True, "every pool drawn by Vulkan once its pipelines are made");
            Assert.That(capture!.Left, Is.Zero, string.Join("; ", capture.Reasons));
            Assert.That(capture.PipelineWaits, steps.All(s => s.Counted || s.Gl) ? Is.Zero : Is.Positive);
            TestContext.Out.WriteLine($"{frames} frames, {capture.PipelineWaits} pool draws waited for their pipelines");
        }
        finally
        {
            GlTap.Untap();
            foreach (var scene in scenes.Values) scene.Dispose();
        }
    }

    private static float Tolerance(int target, int attachment) =>
        (target, attachment) switch
        {
            (Primary, < 2) => 1.5f / 255,
            (Primary, 2 or 3) => 1e-3f,
            _ => 1e-6f
        };

    // every: each pool Vulkan's (not while its pipelines are being made)
    private static float[][][] Run(Dictionary<string, TerrainScene> scenes, Step[] steps, bool high,
        TerrainRenderer? capture, SlowGreen slow, bool every = true)
    {
        var framebuffers = Framebuffers(high);
        try
        {
            if (capture is not null)
                Assert.That(capture.Start(framebuffers, [Primary, Shadow, Liquid], out var why), Is.True, why);
            // a swap keeps the textures' names, not their texels
            foreach (var framebuffer in framebuffers) Parity.Clear(framebuffer, Black);
            foreach (var step in steps)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffers[step.Target].FboId);
                GL.Viewport(0, 0, Side, Side);
                step.State();
                var reads = step.Reads.ToDictionary(r => r.Key, r => framebuffers[r.Value].DepthTextureId);
                if (step.Gl || step.Program == "slow") capture?.Yield("OpenGL draws"); // as a pool draw Vulkan leaves
                if (step.Program == "slow")
                {
                    slow.Draw();
                    continue;
                }

                scenes[step.Program].Counted = step.Counted;
                if (capture is not null) capture.Owning = step.Own;
                var taken = scenes[step.Program].Draw(step.Gl ? null : capture, reads);
                if (capture is not null && !step.Gl && every)
                    Assert.That(taken, Is.True, string.Join("; ", capture.Reasons));
            }

            capture?.Frame.Close("the frame's end");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return [.. framebuffers.Select(f => Parity.Read(f, Side))];
        }
        finally
        {
            foreach (var framebuffer in framebuffers)
            {
                capture?.Release(framebuffer); // as the engine's DisposeFrameBuffer lets the capture know
                Parity.Delete(framebuffer);
            }
        }
    }

    private static List<FrameBufferRef> Framebuffers(bool high)
    {
        var primary = Made([.. Enumerable.Range(0, high ? 4 : 2).Select(i => i < 2 ? (SizedInternalFormat)0x8058 :
            (SizedInternalFormat)0x881A)], false);
        return [primary, Made([], true), Made([], false)];
    }

    private static FrameBufferRef Made(SizedInternalFormat[] colors, bool shadow)
    {
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        framebuffer.ColorTextureIds =
            [.. colors.Select((format, i) => Texture(format, FramebufferAttachment.ColorAttachment0 + i))];
        framebuffer.DepthTextureId = Texture((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment);
        if (shadow)
        {
            GL.TextureParameter(framebuffer.DepthTextureId, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.CompareRefToTexture);
            GL.TextureParameter(framebuffer.DepthTextureId, TextureParameterName.TextureCompareFunc,
                (int)All.Lequal);
        }

        if (colors.Length == 0) GL.DrawBuffer(DrawBufferMode.None);
        else GL.DrawBuffers(colors.Length, [.. colors.Select((_, i) => DrawBuffersEnum.ColorAttachment0 + i)]);
        return framebuffer;
    }

    private static int Texture(SizedInternalFormat format, FramebufferAttachment attachment)
    {
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int texture);
        GL.TextureStorage2D(texture, 1, format, Side, Side);
        GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    // RenderOpaque's state for a pass: depth test and write, blending on the first two attachments, SSAO's two plain
    private static void Engine(bool cull, bool blend)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        if (cull) GL.Enable(EnableCap.CullFace);
        else GL.Disable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);
        if (blend) GL.Enable(EnableCap.Blend);
        else GL.Disable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BlendFunc(2, BlendingFactorSrc.One, BlendingFactorDest.Zero);
        GL.BlendFunc(3, BlendingFactorSrc.One, BlendingFactorDest.Zero);
    }

    // The shadow passes (no culling, no blending) and the liquid depth pre-pass (culling, blending on, no colors to blend)
    private static void Depth(bool liquid)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        if (liquid) GL.Enable(EnableCap.CullFace);
        else GL.Disable(EnableCap.CullFace);
        if (liquid) GL.Enable(EnableCap.Blend);
        else GL.Disable(EnableCap.Blend);
    }

    private static int Visible(float[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4).Count(i => rgba[4 * i] + rgba[4 * i + 1] + rgba[4 * i + 2] > 0);

    // A fullscreen triangle whose every pixel loops long before it writes green: the GPU is still at it when the CPU has moved on
    internal sealed class SlowGreen : IDisposable
    {
        private readonly int _program, _vao;

        public SlowGreen()
        {
            _program = Parity.Linked(
                "#version 330\nvoid main() { gl_Position = vec4(vec2(gl_VertexID & 1, gl_VertexID >> 1) * 4.0 - 1.0, 0.5, 1.0); }",
                "#version 330\nuniform int rounds; out vec4 color; void main() { float x = gl_FragCoord.x; " +
                "for (int i = 0; i < rounds; i++) x = fract(x * 1.0001 + 0.37); color = vec4(0, 0.5 + 0.0001 * x, 0, 0.6); }");
            _vao = GL.GenVertexArray();
        }

        public void Draw()
        {
            GL.UseProgram(_program);
            GL.Uniform1(GL.GetUniformLocation(_program, "rounds"), 400_000);
            GL.Disable(EnableCap.DepthTest);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        public void Dispose()
        {
            GL.DeleteProgram(_program);
            GL.DeleteVertexArray(_vao);
        }
    }
}

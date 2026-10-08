using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The opaque terrain on Komet's own depth (TerrainRenderer.Owned), on leaves as the game draws them: the engine's chunkopaque
// (everything on, so the leaves wave and SSAO's two outputs are written), eight layers of quads in random order in three pool
// draws of one call, an atlas with clear, partly clear and solid texels, one layer twice at the same depth with other texels, the
// wind's counters moving from frame to frame. Each RenderOpaque state that blends (culled, not culled) or not, and LEQUAL: Vulkan
// on its own depth draws every value of every attachment and the depth exactly as Vulkan on the engine's; also when a draw of the
// call goes on the engine's depth in between (the depth written back, then copied in again) and when the segment closes
// mid-call. And what OpenGL draws, as close as CaptureParityGpuTests asks, but for the few values where OpenGL's and Vulkan's
// compiles of the engine's shader land on another texel, the same on the engine's depth.
[NonParallelizable]
public sealed class OwnDepthParityGpuTests
{
    private const int Side = TerrainScene.Size, Frames = 4;
    private static readonly float[] Black = [0, 0, 0, 1];
    private static readonly (int First, int Count)[] Draws = [(0, 40), (40, 70), (110, 34)];

    public enum Between
    {
        Nothing,
        EngineDepth, // the second draw on the engine's depth
        Closed // the segment closed after the first draw (the depth written back as it closes)
    }

    [TestCase(true, true, 0.001f, true, Between.Nothing, TestName = "the opaque pass (culled, blended)")]
    [TestCase(false, true, 0.25f, true, Between.Nothing, TestName = "the opaque pass not culled (blended)")]
    [TestCase(false, false, 0.42f, true, Between.Nothing, TestName = "the blended pass not culled (not blended)")]
    [TestCase(false, true, 0.25f, false, Between.Nothing, TestName = "the opaque pass not culled, tested LEQUAL")]
    [TestCase(false, true, 0.25f, true, Between.EngineDepth, TestName = "a draw on the engine's depth between")]
    [TestCase(false, true, 0.25f, true, Between.Closed, TestName = "the segment closed between")]
    public void LeavesOnKometsDepthAreTheEnginesPixels(bool cull, bool blend, float alphaTest, bool less, Between between)
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var foliage = Foliage.Layers(8, 4, 7, true);
        Assert.That(foliage.Faces, Has.Length.EqualTo(Draws[^1].First + Draws[^1].Count));
        using var scene = new TerrainScene(rig.Device!, true, "chunkopaque", foliage) { Counts = rig.Backend };
        scene.Set("alphaTest", [alphaTest]);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            using var capture = TerrainRenderer.Create(rig.Device!, 256, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            var (unlike, apart, copies) = (0, 0, 0L);
            for (var frame = 0; frame < Frames; frame++)
            {
                Wind(scene, frame);
                var state = (cull, blend, less, between);
                var expected = Run(scene, null, state, false);
                _ = Run(scene, capture, state, true); // the pool adopted, the textures copied, the pipelines made
                var plain = Run(scene, capture, state, false);
                (copies, var draws) = (capture.OwnCopies, capture.OwnDraws);
                var owned = Run(scene, capture, state, true);
                Assert.That(capture.OwnDraws - draws, Is.EqualTo(between == Between.EngineDepth ? 2 : 3),
                    "draws on Komet's depth");
                copies = capture.OwnCopies - copies;
                for (var i = 0; i < expected.Length; i++)
                {
                    var tolerance = i < 2 ? 1.5f / 255 : 1e-3f;
                    var (beyond, before) = (Beyond(owned[i], expected[i], tolerance), Beyond(plain[i], expected[i], tolerance));
                    Assert.That(beyond, Is.SubsetOf(before),
                        $"frame {frame}, attachment {i}: {Parity.Differing(owned[i], expected[i])}");
                    (apart, unlike) = (apart + before.Count, unlike + Unlike(owned[i], plain[i]));
                }

                Assert.That(Visible(owned[0]), Is.GreaterThan(Side * Side / 2), "the leaves cover the view");
            }

            Assert.That(capture.Left, Is.Zero, string.Join("; ", capture.Reasons));
            TestContext.Out.WriteLine($"{unlike} values not the same bits as on the engine's depth; {apart} values beyond the " +
                                      $"tolerance from OpenGL's, the same on the engine's depth; {copies} depth copies a frame");
            Assert.That(unlike, Is.Zero, "Komet's depth changed what Vulkan draws");
            Assert.That(copies, Is.EqualTo(between == Between.Nothing ? 2 : 4),
                "copied in and back, twice with something between");
            Assert.That(apart, Is.LessThan(Frames * Side * Side / 100), "Vulkan draws what OpenGL draws");
        }
        finally
        {
            GlTap.Untap();
        }
    }

    // OcclusionCulling's pyramid, built between the surfaces' draws, reads the engine's depth through the backend: Komet's image
    // while it holds the newer depth, the engine's again once written back
    [Test]
    public void ComputeReadsKometsDepthWhileItIsNewer()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var foliage = Foliage.Layers(2, 2, 3, false);
        using var scene = new TerrainScene(rig.Device!, true, "chunkopaque", foliage) { Counts = rig.Backend };
        var framebuffer = Framebuffer();
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            using var capture = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            Assert.That(capture.Start([framebuffer], [0], out why), Is.True, why);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
            GL.Viewport(0, 0, Side, Side);
            Engine(true, true);
            var engine = capture.OwnFind(framebuffer.DepthTextureId);
            var during = new List<SharedImage?>();
            var depth = framebuffer.DepthTextureId;
            Assert.That(scene.DrawCall(capture, [(0, 4), (4, 4)], true, drawn: c => during.Add(c.OwnFind(depth))), Is.True,
                string.Join("; ", capture.Reasons));
            var after = capture.OwnFind(depth);
            Assert.Multiple(() =>
            {
                Assert.That(engine, Is.Not.Null.And.Property(nameof(SharedImage.Exported)).True, "the engine's depth, shared");
                Assert.That(during, Has.Count.EqualTo(2).And.All.Not.SameAs(engine), "Komet's while it is newer");
                Assert.That(during[0], Is.Not.Null.And.Property(nameof(SharedImage.Exported)).False, "Komet's own image");
                Assert.That(after, Is.SameAs(engine), "written back: the engine's again");
                Assert.That(capture.OwnCopies, Is.EqualTo(2), "copied in and back");
            });
            capture.Frame.Close("the test's end");
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GlTap.Untap();
            Parity.Delete(framebuffer);
        }
    }

    private static int Unlike(float[] a, float[] b) =>
        a.Zip(b).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));

    // The values farther from OpenGL's than the tolerance
    private static HashSet<int> Beyond(float[] drawn, float[] expected, float tolerance) =>
        [.. Enumerable.Range(0, drawn.Length).Where(i => Math.Abs(drawn[i] - expected[i]) > tolerance)];

    // The engine's counters for the leaves' waving, as they move with time: set as the engine sets them, through glUniform while
    // the program is in use, which the capture's mirror hears. The player near the origin: far from it OpenGL's and Vulkan's
    // compiles of the waving already differ by more than the tolerance.
    internal static void Wind(TerrainScene scene, int frame)
    {
        GL.UseProgram(scene.Program);
        foreach (var (name, value) in (ReadOnlySpan<(string, float)>)[("windWaveCounter", 3.7f + 1.3f * frame),
                     ("windWaveCounterHighFreq", 11.1f + 4.9f * frame), ("windSpeed", 0.6f + 0.1f * frame),
                     ("windWaveIntensity", 1f), ("timeCounter", 20f + frame)])
            GL.Uniform1(GL.GetUniformLocation(scene.Program, name), value);
        GL.Uniform3(GL.GetUniformLocation(scene.Program, "playerpos"), 0.5f, 0.2f, 0.3f);
        GL.UseProgram(0);
    }

    private static float[][] Run(TerrainScene scene, TerrainRenderer? capture,
        (bool Cull, bool Blend, bool Less, Between Between) state, bool own)
    {
        var framebuffer = Framebuffer();
        try
        {
            if (capture is not null) Assert.That(capture.Start([framebuffer], [0], out var why), Is.True, why);
            Parity.Clear(framebuffer, Black);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
            GL.Viewport(0, 0, Side, Side);
            Engine(state.Cull, state.Blend);
            GL.DepthFunc(state.Less ? DepthFunction.Less : DepthFunction.Lequal);
            var taken = scene.DrawCall(capture, Draws, own, state.Between == Between.EngineDepth ? 1 : -1,
                state.Between == Between.Closed ? 1 : -1);
            if (capture is not null) Assert.That(taken, Is.True, string.Join("; ", capture.Reasons));
            capture?.Frame.Close("the frame's end");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return Parity.Read(framebuffer, Side);
        }
        finally
        {
            capture?.Release(framebuffer);
            Parity.Delete(framebuffer);
        }
    }

    // The primary framebuffer with SSAO's outputs, as CaptureParityGpuTests makes it
    internal static FrameBufferRef Framebuffer(int width = Side, int height = Side)
    {
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = width, Height = height };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        SizedInternalFormat[] colors = [(SizedInternalFormat)0x8058, (SizedInternalFormat)0x8058, (SizedInternalFormat)0x881A,
            (SizedInternalFormat)0x881A];
        framebuffer.ColorTextureIds = [.. colors.Select((format, i) =>
            Texture(format, FramebufferAttachment.ColorAttachment0 + i, width, height))];
        framebuffer.DepthTextureId = Texture((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment, width, height);
        GL.DrawBuffers(colors.Length, [.. colors.Select((_, i) => DrawBuffersEnum.ColorAttachment0 + i)]);
        return framebuffer;
    }

    private static int Texture(SizedInternalFormat format, FramebufferAttachment attachment, int width, int height)
    {
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int texture);
        GL.TextureStorage2D(texture, 1, format, width, height);
        GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    // RenderOpaque's state for a pass: depth tested LESS and written, SSAO's two outputs plain
    internal static void Engine(bool cull, bool blend)
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

    private static int Visible(float[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4).Count(i => rgba[4 * i] + rgba[4 * i + 1] + rgba[4 * i + 2] > 0);
}

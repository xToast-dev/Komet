using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The transparent pass's framebuffer as SystemRenderOITLayers leaves it: its first color replaced by an RGB8 reveal texture
// and three layers of an RGBA16F array attached as colors 3 to 5, which its FrameBufferRef does not name.
[NonParallelizable]
public sealed class TransparentParityGpuTests
{
    private const int Primary = 0, Oit = 1, Side = TerrainScene.Size, Layers = 3;
    private static readonly float[] Ones = [1, 1, 1, 1], Zeros = [0, 0, 0, 0], RevealClear = [1, 0, 0, 0];
    private static readonly string[] Programs = ["chunkopaque", "chunkliquid", "chunktransparent"];
    private static bool _openGlAfter, _rebuiltAfterStart;

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    public void VulkanDrawsTheTransparentPassesAsOpenGlDoes(bool high, bool openGlAfter, bool rebuiltAfterStart)
    {
        _openGlAfter = openGlAfter; // the transparent program drawn by OpenGL after Vulkan's liquids, as the clouds are
        _rebuiltAfterStart = rebuiltAfterStart; // the reveal and the layers attached after the frame's start, as in a world's first
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var scenes = Programs.ToDictionary(p => p, p => new TerrainScene(rig.Device!, high, p));
        try
        {
            var expected = Frames(scenes, high, null, out _);
            Assert.That(GlTap.Tap(), Is.True);
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            foreach (var scene in scenes.Values) _ = renderer!.Watch(scene.Name, scene.Program, scene.Ported);
            // the framebuffers live through the frames, as the engine's: the first shares what is attached, the later ones
            // clear and draw into what is shared by then
            var drawn = Frames(scenes, high, renderer, out var before);
            Assert.That(renderer!.Left, Is.Zero, string.Join("; ", renderer.Reasons));
            Assert.That(renderer.Draws, Is.EqualTo(3 * (Programs.Length - (_openGlAfter ? 1 : 0))));
            Assert.That(renderer.Frame.Segments - before, Is.EqualTo(2), "segments of the second frame");
            Assert.Multiple(() =>
            {
                foreach (var (name, (got, want, tolerance)) in Pairs(drawn, expected))
                    Assert.That(Parity.Worst(got, want), Is.LessThanOrEqualTo(tolerance),
                        $"{name}: {Parity.Differing(got, want)}");
            });
        }
        finally
        {
            GlTap.Untap();
            foreach (var scene in scenes.Values) scene.Dispose();
        }
    }

    private static IEnumerable<(string, (float[], float[], float))> Pairs(Dictionary<string, float[]> drawn,
        Dictionary<string, float[]> expected) =>
        expected.Select(e => (e.Key, (drawn[e.Key], e.Value, e.Key.Contains("RGBA8", StringComparison.Ordinal) ||
                                                           e.Key.Contains("RGB8", StringComparison.Ordinal)
            ? 1.5f / 255
            : 2e-3f)));

    private static Dictionary<string, float[]> Frames(Dictionary<string, TerrainScene> scenes, bool high,
        TerrainRenderer? renderer, out long before)
    {
        var set = Framebuffers(high);
        if (renderer is not null) set.Own(renderer);
        before = 0;
        try
        {
            for (var frame = 0; frame < 3; frame++)
            {
                if (frame == 2) before = renderer?.Frame.Segments ?? 0;
                Run(scenes, set, renderer, frame);
            }

            return Read(set);
        }
        finally
        {
            foreach (var framebuffer in set.List) renderer?.Release(framebuffer);
            Delete(set);
        }
    }

    private static void Run(Dictionary<string, TerrainScene> scenes, Set set, TerrainRenderer? renderer, int frame)
    {
        if (renderer is not null)
            Assert.That(renderer.Start(set.List, [Primary, Oit], out var why), Is.True, why);
        if (_rebuiltAfterStart && frame == 0) Rebuild(set);
        Parity.Clear(set.List[Primary], [0, 0, 0, 1]);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, set.List[Primary].FboId);
        GL.Viewport(0, 0, Side, Side);
        Opaque();
        Assert.That(scenes["chunkopaque"].Draw(renderer, new Dictionary<string, int>()), Is.EqualTo(renderer is not null));
        renderer?.Frame.Close("the opaque terrain drawn"); // as VulkanRenderer's postfix on RenderOpaque
        LoadTransparent(set.List[Oit]);
        var depth = new Dictionary<string, int> { ["depthTex"] = set.List[Primary].DepthTextureId };
        foreach (var name in (ReadOnlySpan<string>)["chunkliquid", "chunktransparent"])
        {
            var vulkan = name == "chunkliquid" || !_openGlAfter ? renderer : null;
            if (vulkan is null) renderer?.Yield("OpenGL draws"); // as a draw Vulkan leaves, or the clouds after
            Assert.That(scenes[name].Draw(vulkan, depth), Is.EqualTo(vulkan is not null),
                renderer is null ? name : string.Join("; ", renderer.Reasons));
        }

        renderer?.Frame.Close("the frame's end");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // The framebuffers, and the reveal texture and accumulation layers as SystemRenderOITLayers names them in its statics: the
    // renderer replaces them by shared textures and tells the new names, as VulkanRenderer tells the engine's statics
    private sealed class Set(List<FrameBufferRef> list, int reveal, int accumulation)
    {
        public List<FrameBufferRef> List { get; } = list;
        public int Reveal { get; set; } = reveal;
        public int Accumulation { get; set; } = accumulation;

        public void Own(TerrainRenderer renderer)
        {
            renderer.Targets.Owned = t => t == Reveal || t == Accumulation;
            renderer.Targets.Renamed = (from, to) =>
            {
                if (Reveal == from) Reveal = to;
                else if (Accumulation == from) Accumulation = to;
            };
        }
    }

    // As ClientPlatformWindows sets them up, then SystemRenderOITLayers' rebuild of the transparent one
    private static Set Framebuffers(bool high)
    {
        var primary = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, primary.FboId);
        primary.DepthTextureId = Mutable(PixelInternalFormat.DepthComponent32, PixelFormat.DepthComponent,
            PixelType.Float, FramebufferAttachment.DepthAttachment);
        primary.ColorTextureIds = [.. Enumerable.Range(0, high ? 4 : 2).Select(i => i < 2
            ? Mutable(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, FramebufferAttachment.ColorAttachment0 + i)
            : Mutable(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float, FramebufferAttachment.ColorAttachment0 + i))];
        GL.DrawBuffers(primary.ColorTextureIds.Length,
            [.. primary.ColorTextureIds.Select((_, i) => DrawBuffersEnum.ColorAttachment0 + i)]);
        var oit = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, oit.FboId);
        oit.ColorTextureIds =
        [
            Mutable(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.UnsignedShort, FramebufferAttachment.ColorAttachment0),
            Mutable(PixelInternalFormat.R16f, PixelFormat.Red, PixelType.UnsignedShort, FramebufferAttachment.ColorAttachment1),
            Mutable(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, FramebufferAttachment.ColorAttachment2)
        ];
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D,
            primary.DepthTextureId, 0);
        GL.DrawBuffers(3, [DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2]);
        var set = new Set([primary, oit], GL.GenTexture(), GL.GenTexture());
        if (!_rebuiltAfterStart) Rebuild(set);
        return set;
    }

    // SystemRenderOITLayers.BeforeOIT's rebuild: the reveal texture and the accumulation layers made and attached
    private static void Rebuild(Set set)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, set.List[Oit].FboId);
        GL.BindTexture(TextureTarget.Texture2D, set.Reveal);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, Side, Side, 0, PixelFormat.Rgb,
            PixelType.UnsignedByte, IntPtr.Zero);
        Nearest(TextureTarget.Texture2D);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            set.Reveal, 0);
        GL.BindTexture(TextureTarget.Texture2DArray, set.Accumulation);
        GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba16f, Side, Side, Layers, 0, PixelFormat.Rgba,
            PixelType.Float, IntPtr.Zero);
        Nearest(TextureTarget.Texture2DArray);
        for (var layer = 0; layer < Layers; layer++)
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment3 + layer,
                set.Accumulation, 0, layer);
    }

    private static int Mutable(PixelInternalFormat format, PixelFormat pixels, PixelType type, FramebufferAttachment attachment)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, Side, Side, 0, pixels, type, IntPtr.Zero);
        Nearest(TextureTarget.Texture2D);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    private static void Nearest(TextureTarget target)
    {
        GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    // RenderOpaque's state: depth test and write, culling, blending on the first two attachments, SSAO's two plain
    private static void Opaque()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BlendFunc(2, BlendingFactorSrc.One, BlendingFactorDest.Zero);
        GL.BlendFunc(3, BlendingFactorSrc.One, BlendingFactorDest.Zero);
    }

    // LoadFrameBuffer(Transparent), ClearFrameBuffer(Transparent), then SystemRenderOITLayers.BeforeOIT's draw buffers,
    // blending and clears
    private static void LoadTransparent(FrameBufferRef oit)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, oit.FboId);
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.Enable(EnableCap.DepthTest);
        GL.DrawBuffers(3, [DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2]);
        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(0, BlendEquationMode.FuncAdd);
        GL.BlendFunc(0, BlendingFactorSrc.One, BlendingFactorDest.One);
        GL.BlendEquation(1, BlendEquationMode.FuncAdd);
        GL.BlendFunc(1, BlendingFactorSrc.Zero, BlendingFactorDest.OneMinusSrcColor);
        GL.BlendEquation(2, BlendEquationMode.FuncAdd);
        GL.BlendFunc(2, BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
        GL.ClearBuffer(ClearBuffer.Color, 0, Zeros);
        GL.ClearBuffer(ClearBuffer.Color, 1, RevealClear);
        GL.ClearBuffer(ClearBuffer.Color, 2, Zeros);
        GL.DrawBuffers(6, [.. Enumerable.Range(0, 6).Select(i => DrawBuffersEnum.ColorAttachment0 + i)]);
        GL.BlendFunc(0, BlendingFactorSrc.DstColor, BlendingFactorDest.Zero);
        GL.BlendFunc(1, BlendingFactorSrc.DstColor, BlendingFactorDest.Zero);
        for (var i = 3; i < 6; i++) GL.BlendFunc(i, BlendingFactorSrc.One, BlendingFactorDest.One);
        GL.ClearBuffer(ClearBuffer.Color, 0, Ones);
        GL.ClearBuffer(ClearBuffer.Color, 1, Ones);
        for (var i = 3; i < 6; i++) GL.ClearBuffer(ClearBuffer.Color, i, Zeros);
    }

    private static Dictionary<string, float[]> Read(Set set)
    {
        var (primary, oit) = (set.List[Primary], set.List[Oit]);
        var read = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["primary color RGBA8"] = Texels(primary.ColorTextureIds[0], 1),
            ["primary glow RGBA8"] = Texels(primary.ColorTextureIds[1], 1),
            ["reveal RGB8"] = Rgb(Texels(set.Reveal, 1)), // its alpha is none in OpenGL: sampled, it reads 1 (a swizzle)
            ["revealage R16F"] = Texels(oit.ColorTextureIds[1], 1),
            ["glow RGBA8"] = Texels(oit.ColorTextureIds[2], 1),
            ["accumulation layers RGBA16F"] = Texels(set.Accumulation, Layers)
        };
        var depth = new float[Side * Side];
        GL.GetTextureImage(primary.DepthTextureId, 0, PixelFormat.DepthComponent, PixelType.Float, depth.Length * 4, depth);
        read["primary depth"] = depth;
        for (var i = 2; i < primary.ColorTextureIds.Length; i++) read[$"primary SSAO {i}"] = Texels(primary.ColorTextureIds[i], 1);
        return read;
    }

    private static float[] Rgb(float[] rgba) => [.. rgba.Where((_, i) => i % 4 != 3)];

    private static float[] Texels(int texture, int layers)
    {
        var values = new float[Side * Side * 4 * layers];
        GL.GetTextureImage(texture, 0, PixelFormat.Rgba, PixelType.Float, values.Length * 4, values);
        return values;
    }

    private static void Delete(Set set)
    {
        foreach (var framebuffer in set.List)
        {
            GL.DeleteFramebuffer(framebuffer.FboId);
            foreach (var texture in framebuffer.ColorTextureIds) GL.DeleteTexture(texture);
        }

        GL.DeleteTexture(set.List[Primary].DepthTextureId);
        GL.DeleteTexture(set.Reveal);
        GL.DeleteTexture(set.Accumulation);
    }
}

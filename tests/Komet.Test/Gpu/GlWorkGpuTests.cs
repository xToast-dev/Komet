using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What OpenGL still did beside Vulkan's frame in the game (Vulkan "all"): GUI draws with no texture bound, the options screen's
// blurred backdrop (a blit out of the window, passes through framebuffers of its own), each followed by a handoff. Every frame
// here is compared with OpenGL's own to the pixel, and none of it may close a segment or leave work to OpenGL.
[NonParallelizable]
public sealed class GlWorkGpuTests
{
    private const int Side = OcclusionGpuTests.Size, Small = Side / 4;

    private const string Vertex = """
        #version 330 core
        uniform vec4 rect;
        out vec2 uv;
        void main() {
            uv = vec2(gl_VertexID & 1, gl_VertexID >> 1);
            gl_Position = vec4(rect.xy + uv * rect.zw, 0.5, 1.0);
        }
        """;

    // As the engine's gui shader: the texture times the color, or the color alone
    private const string GuiShader = """
        #version 330 core
        uniform sampler2D tex2d;
        uniform float noTexture;
        uniform vec4 rgbaIn;
        in vec2 uv;
        out vec4 color;
        void main() { color = noTexture > 0.5 ? rgbaIn : texture(tex2d, uv) * rgbaIn; }
        """;

    // As the backdrop's passes: four bilinear fetches, flipped on the last pass
    private const string PassShader = """
        #version 330 core
        uniform sampler2D source;
        uniform vec2 step;
        uniform int flip;
        in vec2 uv;
        out vec4 color;
        void main() {
            vec2 at = flip == 1 ? vec2(uv.x, 1.0 - uv.y) : uv;
            color = vec4((texture(source, at + step * vec2(-1.0, -1.0)).rgb + texture(source, at + step * vec2(1.0, -1.0)).rgb
                + texture(source, at + step * vec2(-1.0, 1.0)).rgb + texture(source, at + step).rgb) * 0.25, 1.0);
        }
        """;

    private static readonly float[] Background = [0.1f, 0.2f, 0.3f, 1f];

    private sealed record Programs(int Gui, int Pass, int Empty, int Texture, int Sampler) : IDisposable
    {
        public void Dispose()
        {
            GL.DeleteProgram(Gui);
            GL.DeleteProgram(Pass);
            GL.DeleteVertexArray(Empty);
            GL.DeleteTexture(Texture);
            GL.DeleteSampler(Sampler);
        }
    }

    // The gui program draws a textured quad, a quad of its color alone with no texture bound, and one that samples the unit
    // holding no texture, linearly, near the texel's edge
    [Test]
    public void AGuiDrawWithNoTextureBoundIsVulkansAndReadsWhatOpenGlReads()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var programs = Made();
        var expected = Window(null, () => Untextured(programs, false));
        var (r, g, b, a) = Pixel(expected, Side * 3 / 4, Side / 4);
        Assert.That((r, g, b, Math.Round(a, 2)), Is.EqualTo((0f, 0f, 0f, 0.8)),
            "OpenGL reads (0, 0, 0, 1) from no texture, times the color (0.5, 0.75, 1, 0.8)");
        var run = Vulkan(rig, () => Untextured(programs, false), 3);
        Assert.Multiple(() =>
        {
            Assert.That(run.Reasons, Is.Empty, "nothing left to OpenGL");
            Assert.That(run.Draws, Is.EqualTo(3), "the three draws of the last frame Vulkan's");
            Assert.That(run.Closers.Where(c => c.StartsWith("OpenGL", StringComparison.Ordinal)), Is.Empty,
                "no segment closed by OpenGL's work");
            Assert.That(Parity.Worst(run.Pixels, expected), Is.Zero, Parity.Differing(run.Pixels, expected));
        });
    }

    // Through a sampler object that clamps to a white border, Mesa mixes the border into texture 0's (0, 0, 0, 1): that draw
    // stays OpenGL's, and the frame is OpenGL's still
    [Test]
    public void ANoTextureDrawThroughASamplerObjectIsOpenGls()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var programs = Made();
        var expected = Window(null, () => Untextured(programs, true));
        var (r, g, b, _) = Pixel(expected, Side * 3 / 4, Side / 4);
        Assert.That((r, g, b), Is.Not.EqualTo((0f, 0f, 0f)), "OpenGL mixes the white border in");
        var run = Vulkan(rig, () => Untextured(programs, true), 3);
        Assert.Multiple(() =>
        {
            Assert.That(run.Reasons.Single(), Does.EndWith("read through a sampler object"));
            Assert.That(run.Draws, Is.EqualTo(2), "the other draws Vulkan's");
            Assert.That(Parity.Worst(run.Pixels, expected), Is.Zero, Parity.Differing(run.Pixels, expected));
        });
    }

    // The backdrop of Komet's options screen: the window blitted into a texture of its own, shrunk and flipped through two more,
    // the result drawn over the window; then blits OpenGL makes as other code may - scaled up nearest, mirrored, out of the
    // depth - each Vulkan's
    [Test]
    public void TheBackdropsBlitsAndPassesAreVulkans()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var programs = Made();
        using var backdrop = new Backdrop();
        var expected = Window(null, () => backdrop.Draw(programs));
        var run = Vulkan(rig, () => backdrop.Draw(programs), 3);
        TestContext.Out.WriteLine($"closers: {string.Join(", ", run.Closers)}; packs {run.Packs}; blits {run.Blits}");
        Assert.Multiple(() =>
        {
            Assert.That(run.Reasons, Is.Empty, "nothing left to OpenGL");
            Assert.That(run.Blits, Is.EqualTo(Backdrop.Blits), "every blit Vulkan's");
            Assert.That(run.Closers.Where(c => c.StartsWith("OpenGL", StringComparison.Ordinal)), Is.Empty,
                "no segment closed by OpenGL's work");
            Assert.That(run.Packs, Is.Zero, "nothing packed by OpenGL in the last frame");
            Assert.That(Parity.Worst(run.Pixels, expected), Is.Zero, Parity.Differing(run.Pixels, expected));
        });
    }

    // Komet's distant shadow map: a framebuffer object the engine does not list, cleared and drawn tile by tile (scissored) with
    // the terrain's shadow program, sampled by the opaque terrain. Vulkan draws the tiles into the copy of its texture and samples
    // that copy: no clear or draw of OpenGL's, nothing packed for the copy
    [Test]
    public void TheDistantShadowMapsTilesAreVulkansAndNeedNoCopy()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var distant = new Distant(rig.Device!);
        var expected = distant.Frame(null);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        try
        {
            distant.Watch(renderer!);
            var (drawn, packs, segments, clears) = (expected, 0L, 0L, 0L);
            Dictionary<string, long> closers = [];
            for (var frame = 0; frame < 3; frame++)
            {
                (packs, segments, clears) = (renderer.Copies.Packs, renderer.Frame.Segments, scene.Clears);
                closers = new Dictionary<string, long>(renderer.Frame.Closers);
                drawn = distant.Frame(renderer);
            }

            var closed = renderer.Frame.Closers.Where(c => c.Value > closers.GetValueOrDefault(c.Key)).Select(c => c.Key).ToList();
            TestContext.Out.WriteLine($"closers: {string.Join(", ", closed)}; {renderer.Frame.Segments - segments} segments");
            Assert.Multiple(() =>
            {
                Assert.That(renderer.Left, Is.Zero, string.Join("; ", renderer.Reasons));
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(scene.Clears - clears, Is.EqualTo(Distant.Tiles + 2), "every tile's clear Vulkan's, and the primary's two");
                Assert.That(closed.Where(c => c.StartsWith("OpenGL", StringComparison.Ordinal)), Is.Empty,
                    "no segment closed by OpenGL's work");
                Assert.That(renderer.Copies.Packs - packs, Is.Zero, "nothing packed by OpenGL for the map's copy");
                Assert.That(Parity.Worst(drawn[0], expected[0]), Is.LessThanOrEqualTo(1.5f / 255),
                    "the opaque terrain: " + Parity.Differing(drawn[0], expected[0]));
                Assert.That(Parity.Worst(drawn[1], expected[1]), Is.LessThanOrEqualTo(1e-6f),
                    "the map, written back: " + Parity.Differing(drawn[1], expected[1]));
            });
        }
        finally
        {
            GlTap.Untap();
        }
    }

    // A blit Vulkan leaves to OpenGL (the scissor test on) out of a framebuffer whose texture Vulkan drew into through its copy:
    // the copy goes back first, and OpenGL blits what Vulkan drew
    [Test]
    public void AScissoredBlitIsOpenGlsAndReadsWhatVulkanDrew()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var programs = Made();
        using var backdrop = new Backdrop();
        var expected = Window(null, () => backdrop.Scissored(programs));
        var run = Vulkan(rig, () => backdrop.Scissored(programs), 3);
        Assert.Multiple(() =>
        {
            Assert.That(run.Reasons.Single(), Does.EndWith("with the scissor test on"));
            Assert.That(run.Closers, Does.Contain("OpenGL reads what Vulkan drew for it"));
            Assert.That(Parity.Worst(run.Pixels, expected), Is.Zero, Parity.Differing(run.Pixels, expected));
        });
    }

    internal sealed record Run(float[] Pixels, IReadOnlyCollection<string> Reasons, long Draws, long Blits,
        IReadOnlyCollection<string> Closers, long Packs);

    // frames: the last one is the one compared and counted
    private static Run Vulkan(GpuRig rig, Action draw, int frames)
    {
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        try
        {
            var stand = renderer!.Window(Side, Side);
            Assert.That(stand, Is.Not.Null, "the stand-in made");
            float[] pixels = [];
            var (draws, blits, packs) = (0L, 0L, 0L);
            Dictionary<string, long> closers = [];
            for (var frame = 0; frame < frames; frame++)
            {
                (draws, blits, packs) = (scene.Draws, scene.Blits, renderer.Copies.Packs);
                closers = new Dictionary<string, long>(renderer.Frame.Closers);
                Assert.That(renderer.Start([stand!], [0], out why), Is.True, why);
                pixels = Window(renderer, draw);
            }

            var closed = renderer.Frame.Closers.Where(c => c.Value > closers.GetValueOrDefault(c.Key)).Select(c => c.Key);
            return new Run(pixels, [.. scene.Reasons], scene.Draws - draws, scene.Blits - blits, [.. closed],
                renderer.Copies.Packs - packs);
        }
        finally
        {
            renderer?.Unwindowed();
            GlTap.Untap();
        }
    }

    // The frame drawn into the window (the stand-in when Vulkan runs, shown by OpenGL), read back from the real window
    private static float[] Window(TerrainRenderer? renderer, Action draw)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DrawBuffer(DrawBufferMode.Back);
        GL.Viewport(0, 0, Side, Side);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.ClearColor(Background[0], Background[1], Background[2], Background[3]);
        GL.ClearDepth(1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        draw();
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.DepthTest);
        if (renderer is not null) Assert.That(renderer.Present(null), Is.False, "OpenGL shows the frame");
        var pixels = new float[Side * Side * 4];
        var stand = GlTap.Window;
        GlTap.Window = 0; // the real window
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, Side, Side, PixelFormat.Rgba, PixelType.Float, pixels);
        GlTap.Window = stand;
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        return pixels;
    }

    private static Programs Made()
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var pixels = Enumerable.Range(0, 64).Select(i => (byte)(i % 4 == 3 ? 255 : 40 + i * 3)).ToArray();
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        var sampler = GL.GenSampler();
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureBorderColor, [1f, 1f, 1f, 1f]);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return new Programs(Parity.Linked(Vertex, GuiShader), Parity.Linked(Vertex, PassShader), GL.GenVertexArray(), texture, sampler);
    }

    private static void Untextured(Programs programs, bool sampler)
    {
        var gui = programs.Gui;
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(gui);
        GL.BindVertexArray(programs.Empty);
        GL.Uniform1(GL.GetUniformLocation(gui, "tex2d"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, programs.Texture);
        Quad(gui, (-1, -1, 1, 1), 0, (1, 1, 1, 1));
        GL.BindTexture(TextureTarget.Texture2D, 0);
        Quad(gui, (-1, 0, 1, 1), 1, (0.9f, 0.4f, 0.2f, 0.6f));
        GL.Disable(EnableCap.Blend);
        if (sampler) GL.BindSampler(0, programs.Sampler);
        Quad(gui, (0, -1, 1, 1), 0, (0.5f, 0.75f, 1f, 0.8f));
        GL.BindSampler(0, 0);
    }

    private static void Quad(int program, (float X, float Y, float Width, float Height) rect, float noTexture,
        (float R, float G, float B, float A) color)
    {
        GL.Uniform4(GL.GetUniformLocation(program, "rect"), rect.X, rect.Y, rect.Width, rect.Height);
        GL.Uniform1(GL.GetUniformLocation(program, "noTexture"), noTexture);
        GL.Uniform4(GL.GetUniformLocation(program, "rgbaIn"), color.R, color.G, color.B, color.A);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    private static (float, float, float, float) Pixel(float[] rgba, int x, int y)
    {
        var at = 4 * (y * Side + x);
        return (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]);
    }

    // Made before tapping, as the options screen makes its targets once: the full picture, two quarter ones, and a depth
    private sealed class Backdrop : IDisposable
    {
        public const int Blits = 6; // and one OpenGL would write nothing with

        private readonly (int Texture, int Framebuffer) _full = Target(Side, false), _a = Target(Small, false),
            _b = Target(Small, false), _depth = Target(Side, true), _depths = Target(Side, true);

        public void Draw(Programs programs)
        {
            Scene(programs);
            GL.GetInteger(GetPName.DrawFramebufferBinding, out int window);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, window);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _full.Framebuffer);
            GL.BlitFramebuffer(0, 0, Side, Side, 0, 0, Side, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            Depths();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, window);
            GL.UseProgram(programs.Pass);
            GL.BindVertexArray(programs.Empty);
            GL.Viewport(0, 0, Small, Small);
            Through(programs.Pass, _full.Texture, _a.Framebuffer, 1f / Side, false);
            Through(programs.Pass, _a.Texture, _b.Framebuffer, 1f / Small, true);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, window);
            GL.Viewport(0, 0, Side, Side);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(programs.Gui);
            GL.BindTexture(TextureTarget.Texture2D, _b.Texture);
            Quad(programs.Gui, (-1, -1, 2, 2), 0, (1, 1, 1, 0.7f));
            GL.Disable(EnableCap.Blend);
            Blitted(window);
            Depth(programs);
        }

        // The first pass (Vulkan's, into the quarter picture), then a scissored blit of it into the window (OpenGL's)
        public void Scissored(Programs programs)
        {
            Scene(programs);
            GL.UseProgram(programs.Pass);
            GL.BindVertexArray(programs.Empty);
            GL.Viewport(0, 0, Small, Small);
            Through(programs.Pass, programs.Texture, _a.Framebuffer, 1f / Small, false);
            GL.Viewport(0, 0, Side, Side);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _a.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(4, 4, Side - 8, Side - 8);
            GL.BlitFramebuffer(0, 0, Small, Small, 0, 0, Side, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        // A triangle with depth, as the world behind the screen
        private static void Scene(Programs programs)
        {
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.UseProgram(programs.Gui);
            GL.BindVertexArray(programs.Empty);
            GL.BindTexture(TextureTarget.Texture2D, programs.Texture);
            Quad(programs.Gui, (-0.8f, -0.6f, 1.5f, 1.2f), 0, (1, 0.8f, 0.6f, 1));
            Quad(programs.Gui, (-0.3f, -0.9f, 1.1f, 0.7f), 1, (0.2f, 0.9f, 0.4f, 1));
            GL.Disable(EnableCap.DepthTest);
        }

        // Scaled up nearest, mirrored linear, and the depth sampled back as color
        private void Blitted(int window)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _a.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, window);
            GL.BlitFramebuffer(0, 0, Small, Small, Side / 2, Side / 2, Side, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _full.Framebuffer);
            GL.BlitFramebuffer(Side / 4, 0, 0, Side / 4, 0, Side * 3 / 4, Side / 4, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);
            GL.BlitFramebuffer(0, 0, 48, 48, Side / 4, 0, Side / 4 + 32, 32, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest); // down by 3:2
            GL.BlitFramebuffer(5, 7, 50, 34, Side - 31, Side - 19, Side, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest); // down by 45:31 and 27:19
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _depth.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _full.Framebuffer);
            GL.BlitFramebuffer(0, 0, Side, Side, 0, Side, Side, 0, ClearBufferMask.DepthBufferBit,
                BlitFramebufferFilter.Nearest); // nothing: no depth attached there
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, window);
        }

        // A depth cleared in two parts, blitted upside down into another (the window's would be the real window's in OpenGL's
        // frame, whose depth format no texture shares)
        private void Depths()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _depths.Framebuffer);
            GL.DepthMask(true);
            GL.ClearDepth(0.8);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(0, 0, 40, 24);
            GL.ClearDepth(0.3);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Disable(EnableCap.ScissorTest);
            GL.ClearDepth(1);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _depths.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depth.Framebuffer);
            GL.BlitFramebuffer(0, 0, Side, Side, 0, Side, Side, 0, ClearBufferMask.DepthBufferBit,
                BlitFramebufferFilter.Nearest);
        }

        // The depth blitted, drawn as color: (depth, 0, 0, 1)
        public void Depth(Programs programs)
        {
            GL.UseProgram(programs.Gui);
            GL.BindVertexArray(programs.Empty);
            GL.BindTexture(TextureTarget.Texture2D, _depth.Texture);
            Quad(programs.Gui, (0.5f, -1f, 0.5f, 0.5f), 0, (1, 1, 1, 1));
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private static void Through(int program, int source, int into, float step, bool flip)
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, into);
            GL.BindTexture(TextureTarget.Texture2D, source);
            GL.Uniform1(GL.GetUniformLocation(program, "source"), 0);
            GL.Uniform2(GL.GetUniformLocation(program, "step"), step, step);
            GL.Uniform1(GL.GetUniformLocation(program, "flip"), flip ? 1 : 0);
            GL.Uniform4(GL.GetUniformLocation(program, "rect"), -1f, -1f, 2f, 2f);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        private static (int Texture, int Framebuffer) Target(int side, bool depth)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, depth ? (PixelInternalFormat)0x8CAC : PixelInternalFormat.Rgba8, side,
                side, 0, depth ? PixelFormat.DepthComponent : PixelFormat.Rgba, depth ? PixelType.Float : PixelType.UnsignedByte,
                IntPtr.Zero);
            var filter = depth ? (int)TextureMinFilter.Nearest : (int)TextureMinFilter.Linear; // a depth read texel for texel
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            var framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                depth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
                texture, 0);
            if (depth) GL.DrawBuffer(DrawBufferMode.None);
            if (depth) GL.ReadBuffer(ReadBufferMode.None);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return (texture, framebuffer);
        }

        public void Dispose()
        {
            foreach (var (texture, framebuffer) in (ReadOnlySpan<(int, int)>)[_full, _a, _b, _depth, _depths])
            {
                GL.DeleteFramebuffer(framebuffer);
                GL.DeleteTexture(texture);
            }
        }
    }

    // The map (made before tapping, as DistantShadows makes it), four scissored tiles a frame, and the opaque pass sampling it
    private sealed class Distant : IDisposable
    {
        public const int Tiles = 4;
        private const int Map = Side; // TerrainScene's frame size

        private readonly TerrainScene _shadow, _opaque;
        private readonly int _texture, _framebuffer;

        public Distant(VulkanDevice device)
        {
            (_shadow, _opaque) = (new TerrainScene(device, true, "chunkshadowmap"), new TerrainScene(device, true));
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, Map, Map, 0,
                PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
            _framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D,
                _texture, 0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        public void Watch(TerrainRenderer renderer)
        {
            _ = renderer.Watch(_shadow.Name, _shadow.Program, _shadow.Ported);
            _ = renderer.Watch(_opaque.Name, _opaque.Program, _opaque.Ported);
        }

        // The opaque pass's color and the map
        public float[][] Frame(TerrainRenderer? renderer)
        {
            var primary = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Map, Height = Map };
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, primary.FboId);
            primary.ColorTextureIds = [Target((SizedInternalFormat)0x8058, FramebufferAttachment.ColorAttachment0)];
            primary.DepthTextureId = Target((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment);
            try
            {
                if (renderer is not null) Assert.That(renderer.Start([primary], [0], out var why), Is.True, why);
                Parity.Clear(primary, [0, 0, 0, 1]);
                Tiled(renderer);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, primary.FboId);
                GL.Viewport(0, 0, Map, Map);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Less);
                GL.Disable(EnableCap.Blend);
                _ = _opaque.Draw(renderer, new Dictionary<string, int> { ["shadowMapFar"] = _texture, ["shadowMapNear"] = _texture });
                renderer?.Frame.Close("the frame's end");
                var map = new float[Map * Map]; // read by OpenGL: what Vulkan drew into the copy goes back first

                GL.GetTextureImage(_texture, 0, PixelFormat.DepthComponent, PixelType.Float, map.Length * 4, map);
                return [Parity.Read(primary, Map)[0], map];
            }
            finally
            {
                renderer?.Release(primary);
                Parity.Delete(primary);
            }
        }

        private void Tiled(TerrainRenderer? renderer)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            GL.Viewport(0, 0, Map, Map);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);
            for (var tile = 0; tile < Tiles; tile++)
            {
                GL.Scissor(tile % 2 * Map / 2, tile / 2 * Map / 2, Map / 2, Map / 2);
                GL.Clear(ClearBufferMask.DepthBufferBit);
                var taken = _shadow.Draw(renderer, new Dictionary<string, int>());
                if (renderer is not null) Assert.That(taken, Is.True, string.Join("; ", renderer.Reasons));
            }

            GL.Disable(EnableCap.ScissorTest);
        }

        private static int Target(SizedInternalFormat format, FramebufferAttachment attachment)
        {
            GL.CreateTextures(TextureTarget.Texture2D, 1, out int texture);
            GL.TextureStorage2D(texture, 1, format, Map, Map);
            GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
            return texture;
        }

        public void Dispose()
        {
            _shadow.Dispose();
            _opaque.Dispose();
            GL.DeleteFramebuffer(_framebuffer);
            GL.DeleteTexture(_texture);
        }
    }
}

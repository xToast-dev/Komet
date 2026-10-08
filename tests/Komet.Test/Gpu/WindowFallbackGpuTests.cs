using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// Vulkan shows the frames itself (a swapchain on the test's window). A GUI like the creative inventory's - a slot drawn without
// a texture, then an icon sampling a texture of its own, hundreds of times - samples more textures than the copies held; or
// every slot is followed by a draw only OpenGL makes (points). Either way every frame reaches the window, as OpenGL draws it.
[NonParallelizable]
public sealed class WindowFallbackGpuTests
{
    private const int Side = OcclusionGpuTests.Size, Cells = Side / 2, Icons = 600, PointSlots = 100;

    private const string Vertex = """
        #version 330 core
        uniform vec4 rect;
        out vec2 uv;
        void main() {
            uv = vec2(gl_VertexID & 1, gl_VertexID >> 1);
            gl_Position = vec4(rect.xy + uv * rect.zw, 0.0, 1.0);
        }
        """;

    private const string IconShader = """
        #version 330 core
        uniform sampler2D tex2d;
        in vec2 uv;
        out vec4 color;
        void main() { color = texture(tex2d, uv); }
        """;

    private const string SlotShader = """
        #version 330 core
        uniform vec4 tint;
        out vec4 color;
        void main() { color = tint; }
        """;

    private static readonly string[] Alternating = ["Vulkan", "OpenGL", "Vulkan", "Vulkan", "OpenGL", "Vulkan"];

    private sealed record Gui(int Icon, int Slot, int Empty, int[] Textures);

    // Two sets of icons, each frame one of them (A, A, B, B, A): more textures than the copies hold, fewer in any one frame
    [Test]
    public void EveryFrameReachesTheWindowWhileTheGuiSamplesMoreTexturesThanTheCopiesHold()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        if (!rig.Device!.Swapchains) Assert.Ignore("this Vulkan has no swapchains for windows");
        rig.Backend!.Flush();
        var gui = Made(2 * Icons);
        Assert.That(2 * Icons, Is.GreaterThan(TerrainTextures.MaxTextures), "more icons than copies");
        int[] sets = [0, 0, Icons, Icons, 0];
        var expected = new[] { 0, Icons }.ToDictionary(set => set, set => Reference(gui, set, false));
        var shown = Shown(rig, gui, sets, false, expected);
        Assert.Multiple(() =>
        {
            Assert.That(shown.Unseen, Is.Empty, "frames that reached neither Vulkan's swapchain nor OpenGL's window");
            Assert.That(shown.Reasons.Where(r => r.Contains("has no shared copy", StringComparison.Ordinal)), Is.Empty,
                "every icon sampled through a copy");
            Assert.That(shown.ByVulkan, Is.EqualTo(sets.Length), "Vulkan showed every frame");
            Assert.That(shown.Evictions, Is.Positive, "the copies sampled longest ago made room");
            Assert.That(shown.Copies, Is.LessThanOrEqualTo(TerrainTextures.MaxTextures));
        });
    }

    // Each slot followed by a draw Vulkan leaves to OpenGL: OpenGL draws on once the frame runs short of segments, so the
    // frame keeps one to show it with
    [Test]
    public void EveryFrameReachesTheWindowWhileOpenGlDrawsBetweenTheSlots()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        if (!rig.Device!.Swapchains) Assert.Ignore("this Vulkan has no swapchains for windows");
        rig.Backend!.Flush();
        var gui = Made(0);
        int[] sets = [0, 0, 0];
        var expected = new Dictionary<int, float[]> { [0] = Reference(gui, 0, true) };
        var shown = Shown(rig, gui, sets, true, expected);
        Assert.Multiple(() =>
        {
            Assert.That(shown.Unseen, Is.Empty, "frames that reached neither Vulkan's swapchain nor OpenGL's window");
            Assert.That(shown.ByVulkan, Is.EqualTo(sets.Length), "Vulkan showed every frame");
            Assert.That(shown.Segments, Is.LessThan(VulkanFrame.MaxSegments * sets.Length), "segments left in every frame");
        });
    }

    // A frame whose segments are all used before the window is shown goes through OpenGL; the next is Vulkan's again, its image
    // acquired and presented as before
    [Test]
    public void AFrameVulkanCannotShowIsOpenGlsAndTheNextIsVulkansAgain()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        if (!rig.Device!.Swapchains) Assert.Ignore("this Vulkan has no swapchains for windows");
        rig.Backend!.Flush();
        var gui = Made(0);
        var expected = Reference(gui, 0, false, 8);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        renderer!.Presenting = true;
        var ways = new List<string>();
        try
        {
            var stand = renderer.Window(Side, Side)!;
            for (var frame = 0; frame < 6; frame++)
            {
                Assert.That(renderer.Start([stand], [0], out why), Is.True, why);
                Draw(gui, 0, false, 8);
                for (var used = 0; frame % 3 == 1 && used < VulkanFrame.MaxSegments; used++)
                {
                    _ = renderer.Frame.Begin(SegmentSync.Ordered, out _, last: true);
                    renderer.Frame.Close("the test uses up the frame's segments");
                }

                ways.Add(Check(rig, renderer, expected, $"frame {frame}"));
            }
        }
        finally
        {
            renderer.Unwindowed();
            GlTap.Untap();
            Deleted(gui);
        }

        Assert.That(ways, Is.EqualTo(Alternating));
    }

    private sealed record Seen(List<string> Unseen, int ByVulkan, IReadOnlyCollection<string> Reasons, long Evictions,
        int Copies, long Segments);

    private static Seen Shown(GpuRig rig, Gui gui, int[] sets, bool points, Dictionary<int, float[]> expected)
    {
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        renderer!.Presenting = true;
        var (unseen, byVulkan, segments) = (new List<string>(), 0, renderer.Frame.Segments);
        try
        {
            var stand = renderer.Window(Side, Side);
            Assert.That(stand, Is.Not.Null, "the stand-in made");
            for (var frame = 0; frame < sets.Length; frame++)
            {
                Assert.That(renderer.Start([stand!], [0], out why), Is.True, why);
                Draw(gui, sets[frame], points);
                var way = Check(rig, renderer, expected[sets[frame]], $"frame {frame}");
                if (way == "Vulkan") byVulkan++;
                if (way == "nowhere") unseen.Add($"frame {frame}");
            }

            TestContext.Out.WriteLine($"{byVulkan} of {sets.Length} frames shown by Vulkan, {renderer.Unshown} by OpenGL; " +
                                      $"{renderer.Frame.Segments - segments} segments; {renderer.Copies.Count} copies, " +
                                      $"{renderer.Copies.Evictions} evicted; {scene.Left} draws left to OpenGL: " +
                                      string.Join("; ", scene.Reasons.Take(3)));
            return new Seen(unseen, byVulkan, [.. scene.Reasons], renderer.Copies.Evictions, renderer.Copies.Count,
                renderer.Frame.Segments - segments);
        }
        finally
        {
            renderer.Unwindowed();
            GlTap.Untap();
            Deleted(gui);
        }
    }

    // Who showed the frame: Vulkan (its stand-in is what the swapchain got), OpenGL (the stand-in copied into the real window),
    // or nowhere (dropped). Either way the stand-in is OpenGL's frame to the pixel.
    private static string Check(GpuRig rig, TerrainRenderer renderer, float[] expected, string frame)
    {
        var vulkan = renderer.Present(rig.Window);
        var way = renderer.Showing ? "Vulkan" : "nowhere";
        if (!vulkan) way = "OpenGL";
        Assert.That(Parity.Worst(Read(), expected), Is.LessThanOrEqualTo(1.5f / 255), $"{frame}: the stand-in");
        if (vulkan) return way;
        var stand = GlTap.Window;
        GlTap.Window = 0; // the real window
        try
        {
            Assert.That(Parity.Worst(Read(), expected), Is.LessThanOrEqualTo(1.5f / 255), $"{frame}: OpenGL's window");
        }
        finally
        {
            GlTap.Window = stand;
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        }

        return way;
    }

    private static float[] Read()
    {
        var pixels = new float[Side * Side * 4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, Side, Side, PixelFormat.Rgba, PixelType.Float, pixels);
        return pixels;
    }

    private static Gui Made(int icons)
    {
        var textures = new int[icons];
        for (var i = 0; i < icons; i++)
        {
            textures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            byte[] rgba = [(byte)(i * 37 % 251), (byte)(i * 91 % 241), (byte)(i * 13 % 239 + 16), 255];
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 4, 4, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, Enumerable.Range(0, 16).SelectMany(_ => rgba).ToArray()); // the unsized RGBA, as the engine
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return new Gui(Parity.Linked(Vertex, IconShader), Parity.Linked(Vertex, SlotShader), GL.GenVertexArray(), textures);
    }

    private static void Deleted(Gui gui)
    {
        if (gui.Textures.Length > 0) GL.DeleteTextures(gui.Textures.Length, gui.Textures);
        GL.DeleteVertexArray(gui.Empty);
        GL.DeleteProgram(gui.Icon);
        GL.DeleteProgram(gui.Slot);
    }

    // Drawn by OpenGL alone, before the tap
    private static float[] Reference(Gui gui, int first, bool points, int slots = 0)
    {
        Draw(gui, first, points, slots);
        return Read();
    }

    // slots: that many slots alone (no icons); else every icon of the set, or PointSlots slots each with a point after it
    private static void Draw(Gui gui, int first, bool points, int slots = 0)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DrawBuffer(DrawBufferMode.Back);
        GL.Viewport(0, 0, Side, Side);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.ClearColor(0.1f, 0.2f, 0.3f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.BindVertexArray(gui.Empty);
        var count = points ? PointSlots : Icons;
        if (slots > 0) count = slots;
        for (var i = 0; i < count; i++)
        {
            var (x, y) = (-1 + i % Cells * 4f / Side, -1 + i / Cells * 4f / Side);
            GL.UseProgram(gui.Slot);
            GL.Uniform4(GL.GetUniformLocation(gui.Slot, "rect"), x, y, 4f / Side, 4f / Side);
            GL.Uniform4(GL.GetUniformLocation(gui.Slot, "tint"), 0.3f + i % 7 * 0.1f, 0.25f, 0.5f, 1f);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            if (slots > 0) continue;
            if (points)
            {
                GL.Uniform4(GL.GetUniformLocation(gui.Slot, "rect"), x + 3f / Side, y + 3f / Side, 0f, 0f);
                GL.Uniform4(GL.GetUniformLocation(gui.Slot, "tint"), 1f, 1f, 0.2f, 1f);
                GL.DrawArrays(PrimitiveType.Points, 0, 1); // OpenGL's alone
                continue;
            }

            GL.UseProgram(gui.Icon);
            GL.Uniform4(GL.GetUniformLocation(gui.Icon, "rect"), x, y, 2f / Side, 2f / Side);
            GL.Uniform1(GL.GetUniformLocation(gui.Icon, "tex2d"), 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, gui.Textures[first + i]);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }
}

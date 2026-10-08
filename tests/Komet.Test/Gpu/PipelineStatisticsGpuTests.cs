using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What the HUD's pipeline rows show while Vulkan draws the opaque terrain: the counted section's draws in the GPU's pipeline,
// another section's left out
[NonParallelizable]
public sealed class PipelineStatisticsGpuTests
{
    private const int Side = TerrainScene.Size, Frames = 12;

    [Test]
    public void TheCountedSectionsDrawsAreCounted()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        if (!rig.Device!.PipelineStatistics) Assert.Ignore("the device counts no pipeline statistics");
        rig.Backend!.Flush();
        using var scene = new TerrainScene(rig.Device, false);
        var primary = Primary();
        Assert.That(GlTap.Tap(), Is.True);
        var hud = Komet.Core.Counting.Hud;
        Komet.Core.Counting.Hud = true; // the statistics are counted for the HUD only
        try
        {
            using var renderer = TerrainRenderer.Create(rig.Device, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            _ = renderer!.Watch(scene.Name, scene.Program, scene.Ported);
            var (counted, other) = (VulkanFrame.SectionOf("test counted terrain"), VulkanFrame.SectionOf("test other terrain"));
            renderer.Frame.Counted = counted;
            Assert.That(renderer.Frame.LastStatistic(0), Is.NaN, "nothing counted yet");
            for (var f = 0; f < Frames; f++)
            {
                Assert.That(renderer.Start([primary], [0], out why), Is.True, why);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, primary.FboId);
                GL.Viewport(0, 0, Side, Side);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
                foreach (var section in (ReadOnlySpan<int>)[counted, other])
                {
                    renderer.Frame.Section(section);
                    Assert.That(scene.Draw(renderer, new Dictionary<string, int>()), Is.True, string.Join("; ", renderer.Reasons));
                }

                renderer.Frame.Close("the test's frame");
            }

            Assert.That(rig.Device.WaitIdle(), Is.True);
            for (var f = 0; f < VulkanFrame.Slots; f++) renderer.Frame.Next();
            var (vertices, fragments, primitives, clipped) = (renderer.Frame.LastStatistic(0), renderer.Frame.LastStatistic(1),
                renderer.Frame.LastStatistic(2), renderer.Frame.LastStatistic(3));
            var line = renderer.Frame.Times();
            renderer.Release(primary);
            Assert.Multiple(() =>
            {
                Assert.That(vertices, Is.InRange(12, 18), "the three quads' vertices, once");
                Assert.That(primitives, Is.InRange(1, 6), "their six triangles at most, once");
                Assert.That(clipped, Is.InRange(1, primitives), "after clipping");
                Assert.That(fragments, Is.InRange(1, 2 * Side * Side), "the quads' pixels");
                Assert.That(renderer.Frame.CountedPixels, Is.EqualTo(Side * Side), "the rendering's area");
                Assert.That(line, Does.Contain("the opaque terrain in the pipeline"), line);
            });
        }
        finally
        {
            Komet.Core.Counting.Hud = hud;
            GlTap.Untap();
        }
    }

    private static FrameBufferRef Primary()
    {
        var primary = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, primary.FboId);
        primary.DepthTextureId = Texture(PixelInternalFormat.DepthComponent32, PixelFormat.DepthComponent, PixelType.Float,
            FramebufferAttachment.DepthAttachment);
        primary.ColorTextureIds =
        [
            Texture(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, FramebufferAttachment.ColorAttachment0),
            Texture(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, FramebufferAttachment.ColorAttachment1)
        ];
        GL.DrawBuffers(2, [DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1]);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return primary;
    }

    private static int Texture(PixelInternalFormat format, PixelFormat pixels, PixelType type, FramebufferAttachment attachment)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, Side, Side, 0, pixels, type, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }
}

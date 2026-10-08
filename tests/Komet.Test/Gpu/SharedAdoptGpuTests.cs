using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class SharedAdoptGpuTests
{
    private static readonly float[] Red = [1, 0, 0, 1];

    [Test]
    public void AMutableTextureMovesOntoSharedMemoryAndKeepsItsName()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 16, 16, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int immutable);
        GL.TextureStorage2D(immutable, 1, SizedInternalFormat.Rgba8, 16, 16);
        var framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        const uint usage = Vk.Sampled | Vk.TransferSrc | Vk.TransferDst | Vk.ColorAttachment;
        using var shared = SharedImage.Adopt(rig.Device!, texture, (16, 16, 1, 1), SharedFormat.Rgba8, usage, out var why);
        using var refused = SharedImage.Adopt(rig.Device!, immutable, (16, 16, 1, 1), SharedFormat.Rgba8, usage,
            out var refusal);
        try
        {
            Assert.That(shared, Is.Not.Null, why);
            Assert.That(shared!.Texture, Is.EqualTo(texture), "the name stays");
            Assert.That(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer),
                Is.EqualTo(FramebufferErrorCode.FramebufferComplete));
            GL.ClearBuffer(ClearBuffer.Color, 0, Red);
            var texels = new byte[16 * 16 * 4];
            GL.GetTextureImage(texture, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texels.Length, texels);
            Assert.That(texels[..4], Is.EqualTo(new byte[] { 255, 0, 0, 255 }), "drawn into through the framebuffer");
            Assert.That(refused, Is.Null, "an immutable texture is refused");
            Assert.That(refusal, Is.Not.Empty);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            shared?.Abandon();
            GL.DeleteTexture(texture);
            GL.DeleteTexture(immutable);
        }
    }

    // A block atlas as the engine makes one (unsized GL_RGBA from BGRA texels, mipmapped, the max level set) with a
    // framebuffer drawing into it, as the item icons are drawn: adopted with its levels, and a refusal is not tried again
    [Test]
    public void AnAtlasTheEngineDrawsIntoIsAdoptedOnce()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 4096, 2048, 0, PixelFormat.Bgra,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 3);
        Assert.That(GlTap.Tap(), Is.True); // the atlas is older than the tapping, as in the game
        var framebuffer = GL.GenFramebuffer();
        using var targets = new TerrainTargets(rig.Device!);
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            targets.NewFrame();
            var shared = targets.Adopt(texture, out var why);
            Assert.That(shared, Is.Not.Null, why);
            Assert.That(shared!.Levels, Is.EqualTo(4));
        }
        finally
        {
            GlTap.Untap();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
        }
    }
}

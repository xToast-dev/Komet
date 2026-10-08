using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The engine makes its block atlas with glTexImage2D and the unsized GL_RGBA, mipmaps generated, GL_TEXTURE_MAX_LEVEL
// capped afterwards.
[NonParallelizable]
public sealed class TerrainTexturesGpuTests
{
    private const int Side = 64, MaxLevel = 3;
    private static readonly bool[] Kept = [true, false, true, true];

    [Test]
    public void AnAtlasLikeTextureIsCopiedLevelByLevelUpToItsMaxLevel()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var pixels = Enumerable.Range(0, Side * Side).Select(i => (i % Side * 4) | (i / Side * 4 << 8) | (0xFF << 24))
            .ToArray();
        var atlas = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, atlas);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Side, Side, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, pixels);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, MaxLevel);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError), "setup");
        using var textures = new TerrainTextures(rig.Device!);

        var copy = textures.Get(atlas, out var why);
        Assert.That(copy, Is.Not.Null, why);
        Assert.That(copy!.Levels, Is.EqualTo(MaxLevel + 1), "the levels OpenGL samples");
        Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError), "the copy's making");
        textures.Refresh(0);
        Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError));
        for (var level = 0; level <= MaxLevel; level++)
        {
            var side = Side >> level;
            var (expected, copied) = (new int[side * side], new int[side * side]);
            GL.GetTextureImage(atlas, level, PixelFormat.Rgba, PixelType.UnsignedByte, expected.Length * 4, expected);
            GL.GetTextureImage(copy.Texture, level, PixelFormat.Rgba, PixelType.UnsignedByte, copied.Length * 4,
                copied);
            Assert.That(copied, Is.EqualTo(expected), $"level {level}");
        }

        GL.DeleteTexture(atlas);
    }

    // At the cap a new copy takes the place of the one sampled longest ago, never one sampled in the frame being drawn, and
    // the one that goes stays alive until the GPU is done with the frame it went in (frames in flight may still sample it)
    [Test]
    public void ACopyMakesRoomByRetiringTheOneSampledLongestAgo()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var textures = Enumerable.Range(0, 5).Select(_ => Small()).ToArray();
        try
        {
            using var copies = new TerrainTextures(rig.Device!, cap: 3);
            var images = textures.Take(3).Select((texture, frame) => copies.Get(texture, out _, frame: frame)!).ToArray();
            images[0].Used = 5; // drawn with again in frame 5 (TerrainRenderer.Segment marks it)
            Assert.That(copies.Get(textures[3], out var why, frame: 6), Is.Not.Null, why);
            Assert.Multiple(() =>
            {
                Assert.That(textures.Take(4).Select(copies.Has), Is.EqualTo(Kept), "the copy of frame 1 went, the oldest");
                Assert.That(copies.Evictions, Is.EqualTo(1));
                Assert.That(images[1].Image, Is.Not.Zero, "retired, not destroyed: frame 6 is in flight");
            });
            copies.Collect(5);
            Assert.That(images[1].Image, Is.Not.Zero, "frame 6 not done yet");
            copies.Collect(6);
            Assert.That(images[1].Image, Is.Zero, "destroyed once frame 6 is done");

            Assert.That(copies.Get(textures[2], out _, frame: 7), Is.SameAs(images[2]), "asked for in frame 7: used in it");
            images[0].Used = 7;
            copies.Get(textures[3], out _, frame: 7)!.Used = 7;
            Assert.That(copies.Get(textures[4], out why, frame: 7), Is.Null, "every copy sampled in frame 7: no room");
            Assert.Multiple(() =>
            {
                Assert.That(why, Does.Contain("sampled in this frame"));
                Assert.That(copies.Count, Is.EqualTo(3));
                Assert.That(copies.Get(textures[4], out why, frame: 8), Is.Not.Null, $"frame 8: room again ({why})");
                Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError));
            });
        }
        finally
        {
            GL.DeleteTextures(textures.Length, textures);
        }
    }

    // Between frames, near the cap, copies left unsampled for a while go, so the next frame's new ones find room as it draws
    [Test]
    public void BetweenFramesCopiesLongUnsampledGoNearTheCap()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var textures = Enumerable.Range(0, 8).Select(_ => Small()).ToArray();
        try
        {
            using var copies = new TerrainTextures(rig.Device!, cap: 8);
            foreach (var texture in textures) Assert.That(copies.Get(texture, out var why, frame: 0), Is.Not.Null, why);
            _ = copies.Settle(30);
            Assert.That(copies.Count, Is.EqualTo(8), "sampled 30 frames ago: kept");
            _ = copies.Settle(100);
            Assert.That((copies.Count, copies.Evictions), Is.EqualTo((7, 1L)), "down to the cap less an eighth");
        }
        finally
        {
            GL.DeleteTextures(textures.Length, textures);
        }
    }

    private static int Small()
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            new int[16]);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }
}

// As the engine's framebuffers keep their textures when Vulkan terrain stops
[NonParallelizable]
public sealed class AbandonedImageGpuTests
{
    [Test]
    public void AnAbandonedImageStaysAnOpenGlTexture()
    {
        using var window = OcclusionGpuTests.Context(); // outlives the rig: the texture stays OpenGL's
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        using var rig = GpuRig.Start(GpuRig.Vulkan, out var why);
        if (rig is null) Assert.Ignore(why);
        var image = SharedImage.Create(rig.Device!, 8, 8, 1, SharedFormat.Rgba8, Komet.Vulkan.Vk.Sampled, out why);
        Assert.That(image, Is.Not.Null, why);
        var texture = image!.Texture;
        var pixels = Enumerable.Range(0, 64).Select(i => i * 0x01020304).ToArray();
        GL.TextureSubImage2D(texture, 0, 0, 0, 8, 8, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        image.Abandon();
        rig.Dispose();
        var read = new int[64];
        GL.GetTextureImage(texture, 0, PixelFormat.Rgba, PixelType.UnsignedByte, read.Length * 4, read);
        Assert.Multiple(() =>
        {
            Assert.That(GL.IsTexture(texture), Is.True);
            Assert.That(read, Is.EqualTo(pixels));
            Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError));
        });
        GL.DeleteTexture(texture);
    }
}

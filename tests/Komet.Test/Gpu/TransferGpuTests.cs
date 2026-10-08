using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What OpenGL hands Vulkan through GlStaging instead of exported images and blocking reads: the copies of the textures Vulkan
// samples (packed by OpenGL, copied into private images in a segment's setup) and the buffers the mirrors import.
[NonParallelizable]
public sealed unsafe class TransferGpuTests
{
    private const int Side = 16;

    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 position;
        void main() { gl_Position = vec4(position, 0.0, 1.0); }
        """;

    private const string Fragment = """
        #version 330 core
        uniform vec4 color;
        out vec4 outColor;
        void main() { outColor = color; }
        """;

    private static readonly float[] Background = [0.1f, 0.1f, 0.2f, 1];

    // An atlas as the engine makes one (unsized GL_RGBA, levels, the max level capped), an RGB texture and an R8 one with odd
    // sides (rows padded to the pack alignment), RGBA16F and a depth; packed with the engine's pack state set oddly, which
    // comes back as it was - asked of OpenGL untapped, known from the tap with the scene's taps in
    [TestCase(PixelInternalFormat.Rgba, 64, 40, 3, false)]
    [TestCase(PixelInternalFormat.Rgb, 30, 18, 2, false)]
    [TestCase(PixelInternalFormat.R8, 13, 7, 1, true)]
    [TestCase(PixelInternalFormat.Rgba16f, 20, 12, 1, false)]
    [TestCase(PixelInternalFormat.DepthComponent32f, 16, 10, 0, true)]
    public void APrivateCopyHoldsEveryLevelAsOpenGlHasIt(PixelInternalFormat format, int width, int height, int maxLevel,
        bool tapped)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush(); // the device has one command buffer, which the backend may be recording into
        var texture = Texture(format, width, height, maxLevel);
        if (tapped) Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var staging = new GlStaging(rig.Device!);
        using var textures = new TerrainTextures(rig.Device!, staging);
        var engines = GL.GenBuffer();
        try
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, engines);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 8);
            GL.PixelStore(PixelStoreParameter.PackRowLength, width + 3);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 1);
            var copy = textures.Get(texture, out var why);
            Assert.That(copy, Is.Not.Null, why);
            Assert.That(copy!.Exported, Is.False, "a copy only sampled is private");
            Assert.That(copy.Levels, Is.EqualTo(maxLevel + 1));
            textures.Refresh(0);
            Assert.Multiple(() =>
            {
                Assert.That(textures.Filling, Is.True, "packed, waiting for a setup");
                Assert.That(textures.Dirty(texture), Is.False);
                Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError), "the pack");
                Assert.That(GL.GetInteger(GetPName.PackAlignment), Is.EqualTo(8), "alignment put back");
                Assert.That(GL.GetInteger(GetPName.PackRowLength), Is.EqualTo(width + 3), "row length put back");
                Assert.That(GL.GetInteger(GetPName.PackSkipPixels), Is.EqualTo(1), "skipped pixels put back");
                Assert.That(GL.GetInteger((GetPName)0x88ED), Is.EqualTo(engines), "the engine's pack buffer bound again");
            });
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            var read = Levels(rig, (textures, staging), copy);
            Assert.That(textures.Filling, Is.False, "the fill went into the commands");
            for (var level = 0; level <= maxLevel; level++)
                Assert.That(read[level], Is.EqualTo(Gl(texture, copy, level)), $"level {level}");
        }
        finally
        {
            if (tapped) GlTap.Untap();
            GL.DeleteBuffer(engines);
            GL.DeleteTexture(texture);
        }
    }

    // Refreshed in every frame (a small copy, not trusting), a private copy is moved to an exported one, which OpenGL blits
    // into; left alone long enough, it is private again
    [Test]
    public void ACopyRefreshedInEveryFrameIsExportedAndOneLeftAloneIsPrivateAgain()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var texture = Texture(PixelInternalFormat.Rgba8, 8, 8, 0);
        using var staging = new GlStaging(rig.Device!);
        using var textures = new TerrainTextures(rig.Device!, staging);
        try
        {
            Assert.That(textures.Get(texture, out var why)?.Exported, Is.False, why);
            for (var frame = 0; frame < 8; frame++)
            {
                Assert.That(textures.Settle(frame), Is.Zero, $"frame {frame}");
                textures.Refresh(frame);
            }

            Assert.That(textures.Settle(8), Is.EqualTo(1), "hot after eight frames in a row");
            Assert.That(textures.Has(texture), Is.False, "let go, to come back exported");
            Assert.That(textures.Exports(texture, false), Is.True);
            var hot = textures.Get(texture, out why);
            Assert.That(hot?.Exported, Is.True, why);
            textures.Trusting = true; // refreshed only when changed from here on
            textures.Refresh(9);
            Assert.That(textures.Settle(500), Is.Zero, "not cold yet");
            Assert.That(textures.Settle(700), Is.EqualTo(1), "cold");
            Assert.That(textures.Get(texture, out why)?.Exported, Is.False, why);
            Assert.That(textures.Moves, Is.EqualTo(2));
            Assert.That(textures.Get(texture, out why, stand: true)?.Exported, Is.False,
                "a stand of a private copy is the caller's to move (Movable)");
            Assert.That(textures.Movable(texture), Is.True);
        }
        finally
        {
            GL.DeleteTexture(texture);
        }
    }

    // An upload staged in host memory while no segment is open waits as a fill: one at a time per copy, and only for a copy
    // whose contents are in its image already; once the frame whose memory holds it comes round, the copy goes dirty instead
    [Test]
    public void AnUploadBetweenSegmentsWaitsUntilItsMemoryIsTakenBack()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var texture = Texture(PixelInternalFormat.Rgba8, 8, 8, 0);
        using var staging = new GlStaging(rig.Device!);
        using var textures = new TerrainTextures(rig.Device!, staging) { Trusting = true };
        try
        {
            var copy = textures.Get(texture, out var why);
            Assert.That(copy, Is.Not.Null, why);
            var region = new Vk.BufferImageCopy { Aspect = Vk.AspectColor, Layers = 1, Width = 4, Height = 4, Depth = 1 };
            textures.Refresh(0);
            Assert.That(textures.Queue(texture, (1, 0, 64), region, 0), Is.False, "the copy's first fill still waits");
            _ = Levels(rig, (textures, staging), copy!); // recorded: the image holds the texture
            Assert.Multiple(() =>
            {
                Assert.That(textures.Queue(texture, (1, 256, 64), region with { Offset = 256 }, 5), Is.True);
                Assert.That(textures.Queue(texture, (1, 512, 64), region with { Offset = 512 }, 5), Is.False, "one waits already");
                textures.Expire(4);
                Assert.That(textures.Filling, Is.True, "its memory is still the frame's");
                textures.Expire(5);
                Assert.That(textures.Filling, Is.False);
                Assert.That(textures.Dirty(texture), Is.True, "OpenGL copies it instead");
            });
        }
        finally
        {
            GL.DeleteTexture(texture);
        }
    }

    // In the order taken: the ring wraps past the ranges still in flight, comes back as their frames are done, and what is too
    // large for it gets a buffer of its own (named for OpenGL's signal until it goes)
    [Test]
    public void TheStagingRingGivesBackInOrderAndWraps()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var staging = new GlStaging(rig.Device!);
        const ulong quarter = GlStaging.RingBytes / 4;
        var first = staging.Take(quarter, 1, out var why);
        var second = staging.Take(quarter, 2, out why);
        var third = staging.Take(quarter, long.MaxValue, out why); // not copied from yet
        Assert.That(third, Is.Not.Null, why);
        var full = staging.Take(quarter + 1, 3, out why);
        var large = staging.Take(GlStaging.RingBytes, 3, out why);
        Assert.Multiple(() =>
        {
            Assert.That(first!.Value.At, Is.Zero);
            Assert.That(second!.Value.At, Is.EqualTo(quarter));
            Assert.That(full!.Value.Buffer, Is.Not.EqualTo(first.Value.Buffer), "no room in the ring: a buffer of its own");
            Assert.That(large!.Value.Buffer, Is.Not.EqualTo(first.Value.Buffer));
            Assert.That(staging.Owned, Is.EqualTo(2));
            Assert.That(staging.Buffers, Has.Length.EqualTo(3), "the ring and both of their own");
        });
        staging.Collect(2); // the first two come back, the third is not used yet: everything after it waits too
        var wrapped = staging.Take(quarter + 1, 4, out why);
        Assert.That(wrapped?.At, Is.Zero, "past the end: wrapped to the front, " + why);
        staging.Used(third!.Value.Ticket, 4);
        staging.Collect(4);
        Assert.That(staging.Buffers, Has.Length.EqualTo(1), "the ring alone");
        Assert.That(staging.Take(GlStaging.RingBytes / 2, 5, out why)?.At, Is.Zero, "empty: from the front, " + why);
    }

    // A buffer made before the mirrors began, read by a draw of an ordered segment, which OpenGL signalled already: OpenGL copies
    // it on the GPU and signals the segment late, the segment's setup fills the mirror, and the draw is Vulkan's - the pixels
    // as OpenGL draws them
    [Test]
    public void ABufferMadeBeforeTheMirrorsIsImportedOnTheGpu()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var program = Parity.Linked(Vertex, Fragment);
        var (vao, vbo) = Triangle();
        var framebuffer = Made();
        try
        {
            var expected = Frame(null, framebuffer, program, vao);
            Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
            var drawn = Frame(renderer, framebuffer, program, vao);
            drawn = Frame(renderer, framebuffer, program, vao);
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty, "nothing left to OpenGL");
                Assert.That(scene.Draws, Is.EqualTo(2));
                Assert.That(scene.Mirrors.Imports, Is.EqualTo(1), "imported once, mirrored since");
                Assert.That(scene.Mirrors.ImportedBytes, Is.EqualTo(6 * 4));
                Assert.That(renderer!.Frame.LateSignals, Is.Positive, "the ordered segment was signalled late");
                Assert.That(renderer.Frame.Fault, Is.Empty);
                Assert.That(Parity.Worst(drawn[0], expected[0]), Is.LessThanOrEqualTo(1.5f / 255),
                    Parity.Differing(drawn[0], expected[0]));
            });
            renderer!.Release(framebuffer);
        }
        finally
        {
            GlTap.Untap();
            Parity.Delete(framebuffer);
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
            GL.DeleteProgram(program);
        }
    }

    private static float[][] Frame(TerrainRenderer? renderer, FrameBufferRef framebuffer, int program, int vao)
    {
        var why = "";
        Assert.That(renderer?.Start([framebuffer], [0], out why) ?? true, Is.True, why);
        Parity.Clear(framebuffer, Background);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        GL.Viewport(0, 0, Side, Side);
        GL.Disable(EnableCap.DepthTest);
        GL.UseProgram(program);
        GL.Uniform4(GL.GetUniformLocation(program, "color"), 0.9f, 0.4f, 0.2f, 1f);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        renderer?.Frame.Close("the frame's end");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return Parity.Read(framebuffer, Side);
    }

    private static (int Vao, int Vbo) Triangle()
    {
        var vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        var vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        float[] corners = [-0.8f, -0.7f, 0.75f, -0.6f, -0.1f, 0.85f];
        GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * 4, corners, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        return (vao, vbo);
    }

    private static FrameBufferRef Made()
    {
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        var (color, depth) = (GL.GenTexture(), GL.GenTexture());
        GL.BindTexture(TextureTarget.Texture2D, color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Side, Side, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            color, 0);
        GL.BindTexture(TextureTarget.Texture2D, depth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)0x81A7, Side, Side, 0, PixelFormat.DepthComponent,
            PixelType.Float, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D,
            depth, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        (framebuffer.ColorTextureIds, framebuffer.DepthTextureId) = ([color], depth);
        return framebuffer;
    }

    // Each level its own texels, specified one by one
    private static int Texture(PixelInternalFormat format, int width, int height, int maxLevel)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        var depth = format == PixelInternalFormat.DepthComponent32f;
        for (var level = 0; level <= maxLevel; level++)
        {
            var (w, h) = (Math.Max(width >> level, 1), Math.Max(height >> level, 1));
            if (depth)
                GL.TexImage2D(TextureTarget.Texture2D, level, format, w, h, 0, PixelFormat.DepthComponent, PixelType.Float,
                    Enumerable.Range(0, w * h).Select(i => (i * 37 + level) % 1000 / 1000f).ToArray());
            else
                GL.TexImage2D(TextureTarget.Texture2D, level, format, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
                    Enumerable.Range(0, w * h * 4).Select(i => (byte)(i * 7 + level * 50 + i / 4)).ToArray());
        }

        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, maxLevel);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError), "the texture");
        return texture;
    }

    private static (PixelFormat Format, PixelType Type, int Bytes) Pack(SharedFormat format) => format.Vulkan switch
    {
        Vk.FormatD32F => (PixelFormat.DepthComponent, PixelType.Float, 4),
        9 => (PixelFormat.Red, PixelType.UnsignedByte, 1),
        Vk.FormatRgba16F => (PixelFormat.Rgba, PixelType.HalfFloat, 8),
        _ => (PixelFormat.Rgba, PixelType.UnsignedByte, 4)
    };

    private static byte[] Gl(int texture, SharedImage like, int level)
    {
        var (format, type, bytes) = Pack(like.Format);
        var read = new byte[Math.Max(like.Width >> level, 1) * Math.Max(like.Height >> level, 1) * bytes];
        GL.GetTextureImage(texture, level, format, type, read.Length, read);
        return read;
    }

    // The private image's levels, blitted into an exported one OpenGL reads: OpenGL signals what it packed, Vulkan copies it in
    // (Flush) and on, OpenGL waits
    private static byte[][] Levels(GpuRig rig, (TerrainTextures Textures, GlStaging Staging) from, SharedImage copy)
    {
        var device = rig.Device!;
        using var check = SharedImage.Create(device, copy.Width, copy.Height, copy.Levels, copy.Format,
            Vk.TransferDst | Vk.Sampled, out var why);
        Assert.That(check, Is.Not.Null, why);
        using var ready = SharedSemaphore.Create(device);
        using var done = SharedSemaphore.Create(device);
        GlInterop.Signal(ready!.Gl, from.Staging.Buffers, [], []);
        GL.Flush();
        Assert.That(device.Run(c => Copied(c, from.Textures, copy, check!, device.Family), [ready.Vulkan], [done!.Vulkan]),
            Is.True);
        GlInterop.Wait(done.Gl, [], [(uint)check!.Texture], [GlInterop.LayoutShaderRead]);
        return [.. Enumerable.Range(0, copy.Levels).Select(level => Gl(check.Texture, copy, level))];
    }

    private static void Copied(IntPtr commands, TerrainTextures textures, SharedImage from, SharedImage into, uint family)
    {
        textures.Flush(commands, 0);
        Moved(commands, from, Vk.LayoutShaderRead, Vk.LayoutTransferSrc);
        Moved(commands, into, Vk.LayoutUndefined, Vk.LayoutTransferDst);
        for (var level = 0; level < from.Levels; level++)
        {
            var (w, h) = (Math.Max(from.Width >> level, 1), Math.Max(from.Height >> level, 1));
            var layer = new Vk.Layers { Aspect = from.Format.Aspect, Level = (uint)level, Count = 1 };
            var blit = new Vk.ImageBlit
            {
                Source = layer, SourceX1 = w, SourceY1 = h, SourceZ1 = 1, Target = layer, TargetX1 = w, TargetY1 = h,
                TargetZ1 = 1
            };
            VkApi.CmdBlitImage(commands, from.Image, Vk.LayoutTransferSrc, into.Image, Vk.LayoutTransferDst, 1, &blit,
                Vk.FilterNearest);
        }

        Handoff.Release(commands, family, [into.In(Vk.LayoutTransferDst) with { To = Vk.LayoutShaderRead }]);
    }

    private static void Moved(IntPtr commands, SharedImage image, int from, int to)
    {
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = from, NewLayout = to,
            SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image.Image,
            Range = new Vk.ColorRange { Aspect = image.Format.Aspect, Levels = (uint)image.Levels, Layers = 1 }
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }
}

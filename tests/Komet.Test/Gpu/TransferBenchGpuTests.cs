using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What moving data from OpenGL to Vulkan costs the engine's thread: refreshing texture copies (exported: OpenGL blits into a
// shared image; private: OpenGL blits and packs into GlStaging, Vulkan copies into its own image) and reading a buffer
// OpenGL's queue still works towards (GetNamedBufferSubData, which waits for the GPU, against a copy on the GPU). Explicit:
// mesa_glthread=true dotnet test tests/Komet.Test/Komet.Test.csproj -c Release --no-build
//     --filter "FullyQualifiedName~TransferBenchGpuTests" --logger "console;verbosity=detailed"
[NonParallelizable]
[Explicit("a benchmark")]
[Category("Bench")]
public sealed class TransferBenchGpuTests
{
    private const int Warm = 20, Frames = 200, Busy = 2048, Reads = 100;

    private const string FullVertex = """
        #version 330 core
        void main() { gl_Position = vec4(vec2(gl_VertexID & 1, gl_VertexID >> 1) * 4.0 - 1.0, 0.0, 1.0); }
        """;

    private const string HeavyFragment = """
        #version 330 core
        uniform int rounds;
        out vec4 color;
        void main() {
            vec4 v = vec4(gl_FragCoord.xy, 0.5, 1.0);
            for (int i = 0; i < rounds; i++) v = fract(v * 1.0001 + vec4(0.37, 0.11, 0.53, 0.07));
            color = v;
        }
        """;

    // count textures of side by side texels with levels, every one changed in every frame; the scene's taps in, as the game
    // runs with the scene in Vulkan (the pack state known, not asked)
    [TestCase(false, 32, 1, 64)]
    [TestCase(true, 32, 1, 64)]
    [TestCase(false, 1024, 4, 4)]
    [TestCase(true, 1024, 4, 4)]
    public void TextureCopiesRefreshed(bool exported, int side, int levels, int count)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var device = rig.Device!;
        var names = Enumerable.Range(0, count).Select(i => Texture(side, levels, i)).ToArray();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var staging = exported ? null : new GlStaging(device);
        using var textures = new TerrainTextures(device, staging) { Trusting = true };
        using var ready = SharedSemaphore.Create(device);
        var exportable = VkMemory.Exported;
        try
        {
            foreach (var name in names) Assert.That(textures.Get(name, out var why)?.Exported, Is.EqualTo(exported), why);
            exportable = VkMemory.Exported - exportable;
            var (gl, vulkan, wall) = (new long[Frames], new long[Frames], new long[Frames]);
            for (var frame = 0; frame < Warm + Frames; frame++)
            {
                foreach (var name in names) textures.Changed(name);
                var start = Stopwatch.GetTimestamp();
                textures.Refresh(frame);
                var packed = Stopwatch.GetTimestamp();
                GlInterop.Signal(ready!.Gl, staging?.Buffers ?? [], [], []);
                GL.Flush();
                var recorded = 0L;
                Assert.That(device.Run(c =>
                {
                    var at = Stopwatch.GetTimestamp();
                    textures.Flush(c, frame);
                    recorded = Stopwatch.GetTimestamp() - at;
                }, [ready.Vulkan], []), Is.True);
                GL.Finish();
                staging?.Collect(frame);
                if (frame < Warm) continue;
                (gl[frame - Warm], vulkan[frame - Warm], wall[frame - Warm]) =
                    (packed - start, recorded, Stopwatch.GetTimestamp() - start);
            }

            var us = 1e6 / Stopwatch.Frequency;
            double Median(long[] values) => values.Order().ElementAt(values.Length / 2) * us;
            var kind = exported ? "exported" : "private";
            TestContext.Out.WriteLine($"{kind}: {count} copies of {side}x{side}, {levels} levels, " +
                                      $"{exportable} exportable allocations for them; medians a frame: OpenGL's refresh " +
                                      $"{Median(gl):0.0} µs ({Median(gl) / count:0.00} a copy), Vulkan's copies recorded " +
                                      $"{Median(vulkan):0.0} µs, to done on the GPU {Median(wall):0.0} µs");
        }
        finally
        {
            GlTap.Untap();
            GL.DeleteTextures(names.Length, names);
        }
    }

    // OpenGL's queue busy for a few milliseconds of fragments, then a buffer read: back to the CPU as the mirrors did, or copied
    // on the GPU into staging Vulkan copies from, as they do now
    [TestCase(false)]
    [TestCase(true)]
    public void ABufferReadWhileOpenGlIsBusy(bool onTheGpu)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var staging = new GlStaging(rig.Device!);
        var program = Parity.Linked(FullVertex, HeavyFragment);
        var (framebuffer, target, vao) = (GL.GenFramebuffer(), GL.GenTexture(), GL.GenVertexArray());
        GL.BindTexture(TextureTarget.Texture2D, target);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, Busy, Busy, 0, PixelFormat.Rgba, PixelType.Float,
            IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            target, 0);
        var buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, 1 << 16, new byte[1 << 16], BufferUsageHint.StaticDraw);
        var (read, times) = (new byte[1 << 16], new long[Reads]);
        _ = staging.Take(1 << 16, 0, out _); // the ring made before: the first take imports it into OpenGL
        try
        {
            for (var i = 0; i < Reads; i++)
            {
                GL.Viewport(0, 0, Busy, Busy);
                GL.UseProgram(program);
                GL.Uniform1(GL.GetUniformLocation(program, "rounds"), 64);
                GL.BindVertexArray(vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                var start = Stopwatch.GetTimestamp();
                if (onTheGpu && staging.Take(1 << 16, i, out _) is { } staged)
                    GL.CopyNamedBufferSubData(buffer, staged.Gl, IntPtr.Zero, new IntPtr((long)staged.At), 1 << 16);
                else GL.GetNamedBufferSubData(buffer, IntPtr.Zero, read.Length, read);
                times[i] = Stopwatch.GetTimestamp() - start;
                GL.Finish();
                staging.Collect(i);
            }

            var us = 1e6 / Stopwatch.Frequency;
            var sorted = times.Order().ToArray();
            TestContext.Out.WriteLine($"{(onTheGpu ? "copied on the GPU" : "read back")}: 64 KB behind a busy queue, the " +
                                      $"engine's thread {sorted[Reads / 2] * us:0.0} µs median, {sorted[^1] * us:0.0} µs worst");
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(target);
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(buffer);
            GL.DeleteProgram(program);
        }
    }

    // As the engine makes its atlases: the unsized GL_RGBA, level 0 given, the rest generated
    private static int Texture(int side, int levels, int seed)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, side, side, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, Enumerable.Range(0, side * side * 4).Select(i => (byte)(i * 3 + seed)).ToArray());
        if (levels > 1) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }
}

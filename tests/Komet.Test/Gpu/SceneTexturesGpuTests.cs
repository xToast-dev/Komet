using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What the scene's draws sample as the units change between them, through the copies of textures made after tapping.
[NonParallelizable]
public sealed class SceneTexturesGpuTests
{
    private const int Side = 16, Frames = 3;

    private const string Vertex = """
        #version 330 core
        void main() {
            vec2 p = vec2(gl_VertexID & 1, gl_VertexID >> 1) * 4.0 - 1.0;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    private const string Fragment = """
        #version 330 core
        uniform sampler2D tex;
        uniform float right;
        uniform float stripe;
        out vec4 color;
        void main() {
            if (stripe >= 0.0 ? floor(gl_FragCoord.x / 4.0) != stripe : (gl_FragCoord.x >= 8.0) != (right > 0.5)) discard;
            color = texture(tex, vec2(0.5));
        }
        """;

    private static readonly byte[][] Colors = [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255]];

    private static readonly float[] Background = [0, 0, 0, 1];

    // A texture uploaded while no segment is open (a draw left to OpenGL closed it): its copy
    // (private) takes the upload from host memory in the next segment's setup, so a draw that samples it after another one
    // opened that segment needs no segment of its own, and samples the upload; OpenGL packs nothing for it
    [Test]
    public void ACopyUploadedBetweenSegmentsIsFreshWhenTheNextOpens()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        var program = Parity.Linked(Vertex, Fragment);
        var (empty, left, uploaded) = (GL.GenVertexArray(), Texture([0, 0, 255, 255]), Texture([0, 0, 0, 255]));
        var framebuffer = Made(true);
        try
        {
            var (segments, packs, uploads) = (0L, 0L, 0L);
            byte[] last = [];
            for (var frame = 0; frame < Frames; frame++)
            {
                (segments, packs, uploads) = (renderer!.Frame.Segments, renderer.Copies.Packs, renderer.Uploads);
                Assert.That(renderer.Start([framebuffer], [0], out why), Is.True, why);
                Parity.Clear(framebuffer, Background);
                Draw(program, empty, left, false);
                renderer.Frame.Close("a draw left to OpenGL");
                last = [(byte)(60 * frame + 40), 200, 30, 255];
                GL.BindTexture(TextureTarget.Texture2D, uploaded);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 4, 4, PixelFormat.Rgba, PixelType.UnsignedByte,
                    Enumerable.Range(0, 16).SelectMany(_ => last).ToArray());
                Draw(program, empty, left, false); // opens the next segment
                Draw(program, empty, uploaded, true);
                renderer.Frame.Close("the frame's end");
            }

            var color = Parity.Read(framebuffer, Side)[0];
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(renderer!.Frame.Closers.Keys.Where(k => k.StartsWith("copied texture", StringComparison.Ordinal)),
                    Is.Empty, "no segment closed for the upload");
                Assert.That(renderer.Frame.Segments - segments, Is.EqualTo(2), "segments of the last frame");
                Assert.That(renderer.Copies.Packs - packs, Is.Zero, "nothing packed by OpenGL in the last frame");
                Assert.That(renderer.Uploads - uploads, Is.EqualTo(1), "the upload copied from host memory");
                Assert.That(Pixel(color, 2), Is.EqualTo((0f, 0f, 1f)), "the left half");
                var (r, g, b) = Pixel(color, Side - 2);
                Assert.That(Math.Abs(r - last[0] / 255f) + Math.Abs(g - last[1] / 255f) + Math.Abs(b - last[2] / 255f),
                    Is.LessThan(1.5f / 255), $"the right half samples the last upload: {(r, g, b)}");
            });
        }
        finally
        {
            renderer?.Release(framebuffer);
            Parity.Delete(framebuffer);
            GL.DeleteTextures(2, [left, uploaded]);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
            GlTap.Untap();
        }
    }

    // Four stripes drawn twice a frame, each draw with another texture: bound to the unit again (the same one too), bound by
    // DSA, or the sampler uniform moved to another unit holding it. Every draw samples what its units hold then, the ones
    // bound before as the ones seen before.
    [Test]
    public void EachDrawSamplesWhatItsUnitsHold()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        var (program, empty) = (Parity.Linked(Vertex, Fragment), GL.GenVertexArray());
        var textures = Colors.Select(Texture).ToArray();
        var framebuffer = Made(true);
        try
        {
            var draws = 0L;
            for (var frame = 0; frame < Frames; frame++)
            {
                draws = scene.Draws;
                Assert.That(renderer!.Start([framebuffer], [0], out why), Is.True, why);
                Parity.Clear(framebuffer, Background);
                GL.UseProgram(program);
                GL.Uniform1(GL.GetUniformLocation(program, "right"), 0f);
                GL.BindVertexArray(empty);
                for (var draw = 0; draw < 8; draw++) Stripe(program, textures, draw, frame);
                GL.BindVertexArray(0);
                GL.UseProgram(0);
                renderer.Frame.Close("the frame's end");
            }

            Assert.That(scene.Draws - draws, Is.EqualTo(8), string.Join("; ", scene.Reasons));
            var color = Parity.Read(framebuffer, Side)[0];
            Assert.Multiple(() =>
            {
                for (var stripe = 0; stripe < 4; stripe++)
                {
                    var want = Colors[(stripe + Frames) % 4]; // the second round's
                    Assert.That(Pixel(color, 4 * stripe + 1), Is.EqualTo((want[0] / 255f, want[1] / 255f, want[2] / 255f)),
                        $"stripe {stripe}");
                }
            });
        }
        finally
        {
            renderer?.Release(framebuffer);
            Parity.Delete(framebuffer);
            GL.DeleteTextures(textures.Length, textures);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
            GlTap.Untap();
        }
    }

    private const string CubeFragment = """
        #version 330 core
        uniform samplerCube ctex;
        out vec4 color;
        const vec3 Faces[6] = vec3[6](vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1),
            vec3(0, 0, -1));
        void main() { color = texture(ctex, Faces[int(gl_FragCoord.x) * 6 / 16]); }
        """;

    // The night sky's cube map, made as the engine makes it (unsized RGBA, BGRA rows, one glTexImage2D a face) before the tap:
    // Vulkan samples each face from its own copy, which no frame after the first packs again
    [Test]
    public void ACubeMapIsSampledFaceByFace()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        byte[][] faces = [[0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [0, 255, 255, 255], [255, 0, 255, 255],
            [255, 255, 0, 255]]; // BGRA
        var cube = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, cube);
        for (var face = 0; face < faces.Length; face++)
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.Rgba, 4, 4, 0, PixelFormat.Bgra,
                PixelType.UnsignedByte, Enumerable.Range(0, 16).SelectMany(_ => faces[face]).ToArray());
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        var (program, empty) = (Parity.Linked(Vertex, CubeFragment), GL.GenVertexArray());
        var framebuffer = Made(true);
        try
        {
            var (draws, packs) = (0L, 0L);
            for (var frame = 0; frame < Frames; frame++)
            {
                (draws, packs) = (scene.Draws, renderer!.Copies.Packs);
                Assert.That(renderer.Start([framebuffer], [0], out why), Is.True, why);
                Parity.Clear(framebuffer, Background);
                GL.UseProgram(program);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.TextureCubeMap, cube);
                GL.Uniform1(GL.GetUniformLocation(program, "ctex"), 0);
                GL.BindVertexArray(empty);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                GL.BindVertexArray(0);
                GL.UseProgram(0);
                GL.BindTexture(TextureTarget.TextureCubeMap, 0);
                renderer.Frame.Close("the frame's end");
            }

            var color = Parity.Read(framebuffer, Side)[0];
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(scene.Draws - draws, Is.EqualTo(1), "the last frame's draw is Vulkan's");
                Assert.That(renderer!.Copies.Packs - packs, Is.Zero, "nothing packed again in the last frame");
                for (var face = 0; face < faces.Length; face++)
                {
                    var x = (16 * face + 15) / 6; // the first column of the face's stripe
                    var want = faces[face];
                    Assert.That(Pixel(color, x), Is.EqualTo((want[2] / 255f, want[1] / 255f, want[0] / 255f)), $"face {face}");
                }
            });
        }
        finally
        {
            renderer?.Release(framebuffer);
            Parity.Delete(framebuffer);
            GL.DeleteTexture(cube);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
            GlTap.Untap();
        }
    }

    // The first round samples the stripe's own texture, the second the one after it, by the frame
    private static void Stripe(int program, int[] textures, int draw, int frame)
    {
        var (stripe, second) = (draw % 4, draw >= 4);
        var texture = textures[second ? (stripe + frame + 1) % 4 : stripe];
        var unit = second && stripe % 2 == 1 ? 5 : 3;
        if (unit == 5 || stripe == 0) GL.BindTextureUnit(unit, texture);
        else
        {
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.BindTexture(TextureTarget.Texture2D, texture); // again: nothing changed
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        GL.Uniform1(GL.GetUniformLocation(program, "tex"), unit);
        GL.Uniform1(GL.GetUniformLocation(program, "stripe"), (float)stripe);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private static (float R, float G, float B) Pixel(float[] rgba, int x)
    {
        var at = 4 * (Side / 2 * Side + x);
        return (rgba[at], rgba[at + 1], rgba[at + 2]);
    }

    private static void Draw(int program, int vao, int texture, bool right)
    {
        GL.UseProgram(program);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(program, "tex"), 3);
        GL.Uniform1(GL.GetUniformLocation(program, "right"), right ? 1f : 0f);
        GL.Uniform1(GL.GetUniformLocation(program, "stripe"), -1f);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    // Made after tapping, 4x4 of one color, sampled nearest
    private static int Texture(byte[] rgba)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var pixels = Enumerable.Range(0, 16).SelectMany(_ => rgba).ToArray();
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        return texture;
    }

    private static FrameBufferRef Made(bool depth)
    {
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        framebuffer.ColorTextureIds = [Target(false, FramebufferAttachment.ColorAttachment0)];
        if (depth) framebuffer.DepthTextureId = Target(true, FramebufferAttachment.DepthAttachment);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return framebuffer;
    }

    private static int Target(bool depth, FramebufferAttachment attachment)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, depth ? (PixelInternalFormat)0x81A7 : PixelInternalFormat.Rgba8, Side, Side,
            0, depth ? PixelFormat.DepthComponent : PixelFormat.Rgba, depth ? PixelType.Float : PixelType.UnsignedByte,
            IntPtr.Zero);
        GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }
}

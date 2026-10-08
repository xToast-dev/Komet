using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The shadow passes on Komet's own depth (TerrainRenderer.Owned), as the game draws them with the scene on: the map cleared
// - by Vulkan, which then needs no copy in, or by OpenGL, copied in - then the engine's chunkshadowmap on eight layers of
// waving leaves whose atlas has clear texels it discards, in two pool draws (deferred as the shadow pass draws them, or
// OcclusionCulling's counted ones, ordered) with an entity caster between them (alpha-tested, depth only: the scene's draw on
// the same depth), then a draw of the scene that reads the map. Vulkan on Komet's depth writes the map and reads it exactly
// as on the engine's; what OpenGL draws, as close as on the engine's.
[NonParallelizable]
public sealed class ShadowOwnDepthGpuTests
{
    private const int Side = TerrainScene.Size, Frames = 3;
    private static readonly (int First, int Count)[] Casters = [(0, 70), (70, 74)];

    [TestCase(false, true, TestName = "the shadow pass (deferred), the map cleared by Vulkan")]
    [TestCase(true, true, TestName = "OcclusionCulling's counted draws (ordered), the map cleared by Vulkan")]
    [TestCase(false, false, TestName = "the shadow pass (deferred), the map cleared by OpenGL")]
    [TestCase(true, false, TestName = "OcclusionCulling's counted draws (ordered), the map cleared by OpenGL")]
    public void CastersOnKometsDepthAreTheEnginesMap(bool counted, bool vulkanClear)
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var foliage = Foliage.Layers(8, 4, 7, true);
        Assert.That(foliage.Faces, Has.Length.EqualTo(Casters[^1].First + Casters[^1].Count));
        using var scene = new TerrainScene(rig.Device!, true, "chunkshadowmap", foliage)
        {
            Counts = rig.Backend, Counted = counted
        };
        using var pass = new ShadowPass(scene);
        var expected = Enumerable.Range(0, Frames).Select(f => pass.Frame(null, false, f)).ToArray();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        try
        {
            using var capture = TerrainRenderer.Create(rig.Device!, 256, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            using var drawer = SceneParityGpuTests.Hooked(capture);
            (drawer.Deferring, drawer.Clearing) = (() => !counted, () => vulkanClear);
            var (unlike, apart) = (0, 0);
            for (var frame = 0; frame < Frames; frame++)
            {
                _ = pass.Frame(capture, true, frame); // the pool adopted, the textures copied, the ports and pipelines made
                var plain = pass.Frame(capture, false, frame);
                var (copies, clears, draws, scene0) = (capture.OwnCopies, capture.OwnClears, capture.OwnDraws, drawer.Draws);
                var owned = pass.Frame(capture, true, frame);
                Assert.That((capture.OwnCopies - copies, capture.OwnClears - clears),
                    Is.EqualTo(vulkanClear ? (1L, 1L) : (2L, 0L)), "taken over by the clear or copied in, back as it is read");
                Assert.That((capture.OwnDraws - draws, drawer.Draws - scene0), Is.EqualTo((2L, 2L)),
                    "both pool draws on Komet's depth, the entity and the read the scene's");
                for (var i = 0; i < expected[frame].Length; i++)
                {
                    var (beyond, before) = (Beyond(owned[i], expected[frame][i]), Beyond(plain[i], expected[frame][i]));
                    Assert.That(beyond, Is.SubsetOf(before),
                        $"frame {frame}, {i}: {Parity.Differing(owned[i], expected[frame][i])}");
                    (apart, unlike) = (apart + before.Count, unlike + Unlike(owned[i], plain[i]));
                }

                Assert.That(owned[0].Count(d => d < 1), Is.GreaterThan(Side * Side / 2), "the casters cover the map");
            }

            Assert.That(capture.Left, Is.Zero, string.Join("; ", capture.Reasons));
            Assert.That(drawer.Reasons, Is.Empty, "nothing of the scene's left to OpenGL");
            TestContext.Out.WriteLine($"{unlike} values not the same bits as on the engine's depth; {apart} values beyond " +
                                      "the tolerance from OpenGL's, the same on the engine's depth");
            Assert.That(unlike, Is.Zero, "Komet's depth changed the map or what reads it");
            Assert.That(apart, Is.LessThan(Frames * Side * Side / 100), "Vulkan draws what OpenGL draws");
        }
        finally
        {
            GlTap.Untap();
        }
    }

    private static int Unlike(float[] a, float[] b) =>
        a.Zip(b).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));

    private static HashSet<int> Beyond(float[] drawn, float[] expected) =>
        [.. Enumerable.Range(0, drawn.Length).Where(i => Math.Abs(drawn[i] - expected[i]) > 1e-5f)];

    // The map's depth state as the engine's shadow pass leaves it: tested LESS and written, no culling, no blending
    internal static void Depth()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
    }

    // A depth-only map as the engine makes its shadow maps (32-bit depth, no color), read raw: no compare mode
    internal static FrameBufferRef Map(int side)
    {
        var map = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = side, Height = side, ColorTextureIds = [] };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, map.FboId);
        map.DepthTextureId = Texture((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment, side);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        return map;
    }

    internal static int Texture(SizedInternalFormat format, FramebufferAttachment attachment, int side)
    {
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int texture);
        GL.TextureStorage2D(texture, 1, format, side, side);
        GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TextureParameter(texture, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TextureParameter(texture, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    // The entity caster (shadowmapentityanimated's fragment: its texture's clear texels discarded) and the read of the map
    private sealed class ShadowPass : IDisposable
    {
        private const string EntityVertex = """
            #version 330 core
            layout(location = 0) in vec3 position;
            layout(location = 1) in vec2 uvIn;
            uniform mat4 projection;
            out vec2 uv;
            void main() { uv = uvIn; gl_Position = projection * vec4(position, 1.0); }
            """;

        private const string EntityFragment = """
            #version 330 core
            in vec2 uv;
            uniform sampler2D entityTex;
            out vec4 outColor;
            void main() { outColor = texture(entityTex, uv); if (outColor.a < 0.01) discard; }
            """;

        private const string ReadVertex = """
            #version 330 core
            out vec2 uv;
            void main() {
                vec2 p = vec2(gl_VertexID & 1, gl_VertexID >> 1) * 4.0 - 1.0;
                uv = p * 0.5 + 0.5;
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """;

        private const string ReadFragment = """
            #version 330 core
            in vec2 uv;
            uniform sampler2D map;
            out vec4 color;
            void main() { color = vec4(texture(map, uv).r, uv, 1.0); }
            """;

        // The camera's perspective of TerrainScene (near 0.1, far 100), looking down -z
        private static readonly float[] Projection = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -100.1f / 99.9f, -1, 0, 0, -20f / 99.9f, 0];

        private readonly TerrainScene _scene;
        private readonly int _entity, _reader, _texture, _vao, _empty;
        private readonly int[] _buffers = new int[3];

        public ShadowPass(TerrainScene scene)
        {
            _scene = scene;
            (_entity, _reader) = (Parity.Linked(EntityVertex, EntityFragment), Parity.Linked(ReadVertex, ReadFragment));
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 8, 8, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, [.. Enumerable.Range(0, 256).Select(Texel)]);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _vao = GL.GenVertexArray();
            _empty = GL.GenVertexArray();
            GL.BindVertexArray(_vao);
            GL.GenBuffers(3, _buffers);
            // slanted through the leaves' layers, from 2 to 3.6 in front of the camera
            Attribute(_buffers[0], [-1.2f, -1f, -2f, 1.2f, -1f, -3.6f, 1.2f, 1f, -3.6f, -1.2f, 1f, -2f], 0, 3);
            Attribute(_buffers[1], [0, 0, 1, 0, 1, 1, 0, 1], 1, 2);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _buffers[2]);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 24, [0, 1, 2, 0, 2, 3], BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
        }

        // A third of the texels clear
        private static byte Texel(int i)
        {
            if (i % 4 != 3) return (byte)(i * 5);
            return i / 4 % 3 == 0 ? (byte)0 : (byte)255;
        }

        private static void Attribute(int buffer, float[] data, int location, int size)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * 4, data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(location);
        }

        // The map's depth and what the read wrote. own: the stage on Komet's depth (VulkanRenderer.Shadowing)
        public float[][] Frame(TerrainRenderer? capture, bool own, int frame)
        {
            var map = Map(Side);
            var read = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, read.FboId);
            read.ColorTextureIds = [Texture((SizedInternalFormat)0x8814, FramebufferAttachment.ColorAttachment0, Side)];
            try
            {
                if (capture is not null) Assert.That(capture.Start([map, read], [0, 1], out var why), Is.True, why);
                OwnDepthParityGpuTests.Wind(_scene, frame);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, read.FboId);
                GL.ClearBuffer(ClearBuffer.Color, 0, [0f, 0f, 0f, 1f]);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, map.FboId);
                GL.Viewport(0, 0, Side, Side);
                Depth();
                if (capture is not null) capture.Owning = own;
                var one = 1f;
                GL.ClearBuffer(ClearBuffer.Depth, 0, ref one); // as the engine's ClearFrameBuffer
                var drawn = 0;
                var taken = _scene.DrawCall(capture, Casters, false, drawn: _ =>
                {
                    if (drawn++ == 0) Entity(true);
                }, kept: true);
                if (capture is null) Entity(false);
                else Assert.That(taken, Is.True, string.Join("; ", capture.Reasons));
                if (capture is not null) capture.Owning = false; // the stage's end
                Read(map, read);
                capture?.Frame.Close("the frame's end");
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                var color = new float[Side * Side * 4];
                GL.GetTextureImage(read.ColorTextureIds[0], 0, PixelFormat.Rgba, PixelType.Float, color.Length * 4, color);
                return [Parity.Read(map, Side)[0], color];
            }
            finally
            {
                foreach (var framebuffer in (ReadOnlySpan<FrameBufferRef>)[map, read])
                {
                    capture?.Release(framebuffer);
                    GL.DeleteFramebuffer(framebuffer.FboId);
                    foreach (var texture in framebuffer.ColorTextureIds.Append(framebuffer.DepthTextureId))
                        if (texture > 0)
                            GL.DeleteTexture(texture);
                }
            }
        }

        // between: drawn between the pool draws, the terrain's program in use again after
        private void Entity(bool between)
        {
            GL.UseProgram(_entity);
            GL.UniformMatrix4(GL.GetUniformLocation(_entity, "projection"), 1, false, Projection);
            GL.BindTextureUnit(5, _texture);
            GL.Uniform1(GL.GetUniformLocation(_entity, "entityTex"), 5);
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(between ? _scene.Program : 0);
        }

        private void Read(FrameBufferRef map, FrameBufferRef read)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, read.FboId);
            GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(_reader);
            GL.BindTextureUnit(6, map.DepthTextureId);
            GL.Uniform1(GL.GetUniformLocation(_reader, "map"), 6);
            GL.BindVertexArray(_empty);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        public void Dispose()
        {
            GL.DeleteProgram(_entity);
            GL.DeleteProgram(_reader);
            GL.DeleteTexture(_texture);
            GL.DeleteVertexArray(_vao);
            GL.DeleteVertexArray(_empty);
            GL.DeleteBuffers(_buffers.Length, _buffers);
        }
    }
}

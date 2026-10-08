using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// Lines asked two pixels wide (as the engine outlines the selected block) are refused by this forward-compatible context.
// Meshes, the uniform block's contents and two textures are made and written anew in each of the three frames, as the
// engine streams them, and the last frame is compared.
[NonParallelizable]
public sealed class SceneParityGpuTests
{
    private const int Side = 64, Primary = 0, Post = 1;

    private const string EntityVertex = """
        #version 330 core
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec2 uv;
        layout(location = 2) in vec4 color;
        layout(location = 3) in vec2 offset;
        uniform mat4 projection;
        layout(std140) uniform Animation { vec4 tint; vec4 shift[2]; };
        out vec2 uvOut;
        out vec4 colorOut;
        void main() {
            uvOut = uv;
            colorOut = color * tint;
            gl_Position = projection * vec4(position.xy + offset + shift[1].xy, position.z, 1.0);
        }
        """;

    private const string EntityFragment = """
        #version 330 core
        in vec2 uvOut;
        in vec4 colorOut;
        uniform sampler2D tex;
        uniform sampler2D detail;
        uniform float alphaTest;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        void main() {
            vec4 c = texture(tex, uvOut) * texture(detail, uvOut * 2.0) * colorOut;
            if (c.a < alphaTest) discard;
            outColor = c;
            outGlow = vec4(c.rgb * 0.5, 1.0);
        }
        """;

    private const string PostVertex = """
        #version 330 core
        out vec2 uv;
        void main() {
            vec2 p = vec2(gl_VertexID & 1, gl_VertexID >> 1) * 4.0 - 1.0;
            uv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    private const string PostFragment = """
        #version 330 core
        in vec2 uv;
        uniform sampler2D scene;
        uniform vec3 gain;
        out vec4 color;
        void main() { color = vec4(texture(scene, uv).rgb * gain, 1.0); }
        """;

    // A map drawn into a framebuffer of its own, not the engine's (as FluffyClouds' cloud map), and read by the next pass
    private const string MapFragment = """
        #version 330 core
        in vec2 uv;
        uniform vec3 gain;
        out vec4 color;
        void main() { color = vec4(uv * gain.xy, gain.z, 1.0); }
        """;

    private const string FlatVertex = """
        #version 330 core
        layout(location = 0) in vec2 position;
        uniform vec2 scale;
        out vec2 uv;
        void main() { uv = position * 0.5 + 0.5; gl_Position = vec4(position * scale, 0.0, 1.0); }
        """;

    private const string FlatFragment = """
        #version 330 core
        in vec2 uv;
        uniform sampler2D ramp;
        uniform vec4 lineColor;
        uniform int useRamp;
        out vec4 color;
        void main() { color = useRamp == 1 ? vec4(texture(ramp, uv).rgb, 0.7) : lineColor; }
        """;

    private static readonly float[] Projection = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0.5f, 1];
    private static readonly float[] Animation = [1, 0.9f, 0.8f, 1, 0, 0, 0, 0, 0.05f, -0.1f, 0, 0];
    private static readonly byte[] Colors = [255, 128, 64, 255, 64, 255, 128, 200, 128, 64, 255, 255, 255, 255, 255, 128];
    private static readonly int[] Indices = [0, 1, 2, 0, 2, 3];
    private static readonly float[] Ramp = [1f, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 1];
    private static readonly float[] Corners = [-1, -1, 1, -1, -1, 1, 1, 1];
    private static readonly ushort[] Strip = [0, 1, 2, 3];
    private static readonly float[] Lines = [-0.9f, -0.47f, 0.9f, -0.47f, -0.28f, -0.9f, -0.28f, 0.9f]; // off the pixel edges: a line on one is a tie OpenGL and Vulkan break the other way
    private static readonly float[] Background = [0.1f, 0.1f, 0.2f, 1];
    private static readonly byte[] Detail = [.. Enumerable.Range(0, 64).Select(i => (byte)(i % 4 == 3 ? 255 : 180 + i))];
    private static readonly byte[] Patch = [10, 200, 30, 255, 40, 20, 220, 255, 250, 60, 90, 255, 5, 5, 5, 255]; // BGRA

    // Hot: the programs settle on their hot uniforms (push constants) after their first draw, so the frames compared draw
    // with them. Lazy: the handoff as the game runs it with the scene on - OpenGL signals only after it touched shared memory
    // and waits for Vulkan only before it touches it again
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void VulkanDrawsTheSceneAsOpenGlDoes(bool hot, bool lazy)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var scene = new Scene();
        var expected = scene.Frame(null);
        var (samples, map) = (scene.Samples, scene.Map);
        Assert.That(samples, Is.GreaterThan(100), "OpenGL counted the entities' samples");
        Assert.That(Visible(expected[Primary][0]), Is.GreaterThan(300), "OpenGL drew the entities");
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var drawer = Hooked(renderer!, lazy);
        var watched = SceneRenderer.Watched;
        SceneRenderer.Watched = hot ? 1 : int.MaxValue;
        try
        {
            _ = scene.Frame(renderer);
            _ = scene.Frame(renderer);
            var (draws, clears, segments) = (drawer.Draws, drawer.Clears, renderer!.Frame.Segments);
            var (late, packs) = (renderer.Frame.LateSignals, renderer.Copies.Packs);
            var drawn = scene.Frame(renderer);
            Assert.That(drawer.Reasons, Is.Empty, "nothing left to OpenGL");
            Assert.That(drawer.Draws - draws, Is.EqualTo(Scene.Draws), "every draw taken, the lines too");
            Assert.That(drawer.Clears - clears, Is.EqualTo(Scene.Clears), "every clear taken");
            // the two textures made this frame are filled from their uploads in host memory in the open segment's setup: no
            // segment closes for them, nor for the blits (Vulkan's): the frame's end closes the one
            Assert.That(renderer.Frame.Segments - segments, Is.EqualTo(1),
                string.Join(", ", renderer.Frame.Closers.Select(c => $"{c.Key} {c.Value}")));
            Assert.That((renderer.Frame.LateSignals - late, renderer.Copies.Packs - packs), Is.EqualTo((0L, 0L)),
                "nothing packed by OpenGL, nothing to signal late for");
            Assert.That(drawer.Blits, Is.EqualTo(3 * Scene.Blitted), "every blit Vulkan's");
            Assert.That(renderer.Copies.Private, Is.Positive, "copies made private");
            Assert.That(scene.Samples, Is.EqualTo(samples), "the occlusion query answered from Vulkan's");
            TestContext.Out.WriteLine($"lazy {lazy}: {renderer.Frame.CleanSignals} signals left out, " +
                                      $"{renderer.Frame.DirtySignals} given, {renderer.Frame.LazyWaits} waits, " +
                                      $"{renderer.Frame.Segments} segments");
            Assert.That(renderer.Uploads, Is.Positive, "the patch went into the old texture's copy in the segment");
            Assert.That(renderer.Programs.Count(p => p.Ported.HotUniforms.Size > 0), hot ? Is.GreaterThan(0) : Is.Zero,
                "programs drawing with push constants");
            Assert.That(Parity.Worst(scene.Map, map), Is.LessThanOrEqualTo(1e-6f),
                "OpenGL reads the map Vulkan drew through its copy: written back");
            Same(drawn, expected, "");
        }
        finally
        {
            SceneRenderer.Watched = watched;
            GlTap.Untap();
        }
    }

    private static void Same(float[][][] drawn, float[][][] expected, string at) =>
        Assert.Multiple(() =>
        {
            for (var target = 0; target < expected.Length; target++)
                for (var i = 0; i < expected[target].Length; i++)
                {
                    var (got, want) = (drawn[target][i], expected[target][i]);
                    var depth = i == expected[target].Length - 1; // interpolated a little differently: a few ulps
                    Assert.That(Parity.Worst(got, want), Is.LessThanOrEqualTo(depth ? 1e-5f : 1.5f / 255),
                        $"{at}framebuffer {target}, attachment {i}: {Parity.Differing(got, want)}");
                }
        });

    // Made on builders, as in the game: a draw whose port or pipeline is not done yet is OpenGL's, between Vulkan's, and every
    // frame - those too, and the ones after the programs switched to the ports with their hot uniforms - is OpenGL's to the
    // pixel; once all are made, every draw is Vulkan's
    [Test]
    public void WhileItsPortsAndPipelinesAreMadeEveryFrameIsOpenGls()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        rig.Backend!.Flush();
        using var scene = new Scene();
        var expected = scene.Frame(null);
        var (samples, map) = (scene.Samples, scene.Map);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var drawer = Hooked(renderer!, lazy: true);
        var watched = SceneRenderer.Watched;
        SceneRenderer.Watched = 1;
        var (frames, settled, mixed) = (0, false, 0);
        try
        {
            for (; frames < 2000 && !settled; frames++)
            {
                var (draws, started) = (drawer.Draws, Builds.Started);
                var drawn = scene.Frame(renderer);
                Same(drawn, expected, $"frame {frames}: ");
                Assert.That(Parity.Worst(scene.Map, map), Is.LessThanOrEqualTo(1e-6f), $"frame {frames}: the map");
                Assert.That(scene.Samples, Is.EqualTo(samples), $"frame {frames}: the occlusion query");
                if (frames == 0) Assert.That(drawer.PortWaits, Is.EqualTo(Scene.Draws), "the first frame waits for every port");
                var all = drawer.Draws - draws == Scene.Draws;
                if (!all && drawer.Draws > draws) mixed++;
                settled = all && Builds.Started == started && Builds.Pending == 0 && drawer.Building == 0;
            }

            TestContext.Out.WriteLine($"{frames} frames until settled, {mixed} with Vulkan's and OpenGL's draws; " +
                                      $"{drawer.PortWaits} + {drawer.PipelineWaits} draws waited");
            Assert.That(settled, Is.True, "every port and pipeline made, every draw Vulkan's: " + string.Join("; ", drawer.Reasons));
            Assert.That(drawer.Reasons, Is.Empty, "nothing left to OpenGL for any other reason");
            Assert.That(drawer.PipelineWaits, Is.Positive, "draws waited for their pipelines");
            Assert.That(renderer!.Programs.Count(p => p.Ported.HotUniforms.Size > 0), Is.Positive,
                "switched to the ports with push constants");
        }
        finally
        {
            SceneRenderer.Watched = watched;
            GlTap.Untap();
        }
    }

    // Gone while ports and pipelines are still being made for it: what its builders make after is destroyed by them, before the
    // device goes (it waits for every build)
    [Test]
    public void DisposedWhileItsPipelinesAreMadeNothingOutlivesIt()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        rig.Backend!.Flush();
        using var scene = new Scene();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        var live = TerrainPipeline.Live;
        var (frames, building) = (0, 0);
        try
        {
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var drawer = Hooked(renderer!);
            for (; frames < 2000 && drawer.PipelineWaits == 0; frames++) _ = scene.Frame(renderer);
            building = drawer.Building;
            Assert.That(drawer.PipelineWaits, Is.Positive, "a frame asked for pipelines being made");
        }
        finally
        {
            GlTap.Untap();
        }

        Assert.That(Builds.Wait(TimeSpan.FromSeconds(60)), Is.True);
        TestContext.Out.WriteLine($"disposed after {frames} frames, {building} ports and pipelines still being made");
        Assert.That(TerrainPipeline.Live, Is.EqualTo(live), "every pipeline destroyed, the ones made after too");
    }

    internal static SceneRenderer Hooked(TerrainRenderer renderer, bool lazy = false)
    {
        var scene = SceneRenderer.Create(renderer, null);
        Assert.That(scene, Is.Not.Null);
        (scene!.Takes, scene.Clearing, scene.Framing) = ((_, _) => true, () => true, true);
        (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Blitting) = (scene.Draw, scene.Clear, scene, scene.Blit);
        (GlTap.Querying, GlTap.Answering) = (scene.Querying, scene.Answer);
        GlTap.Touching = (kind, id) =>
        {
            scene.Touching(kind, id);
            renderer.Touched(kind, id); // as VulkanRenderer's
        };
        if (lazy) (GlTap.Quieting, renderer.Frame.Lazy) = (renderer.Frame.GlQuiet, true);
        renderer.Frame.Opened = scene.Opened;
        renderer.Frame.Ending = () =>
        {
            scene.Mirrors.Ending();
            scene.Resolve();
        };
        renderer.Copies.Trusting = true;
        return scene;
    }

    private sealed class Scene : IDisposable
    {
        public const int Draws = 7, Clears = 6, MapSide = 16, Blitted = 2; // 3 + 2 glClearBuffer, the scissored glClear

        private readonly int _entity, _post, _flat, _old, _empty, _query, _mapProgram, _map, _mapFramebuffer;

        public int Samples { get; private set; }
        private readonly List<int> _frame = [];

        public Scene()
        {
            (_entity, _post, _flat) = (Parity.Linked(EntityVertex, EntityFragment), Parity.Linked(PostVertex, PostFragment),
                Parity.Linked(FlatVertex, FlatFragment));
            GL.UniformBlockBinding(_entity, GL.GetUniformBlockIndex(_entity, "Animation"), 1);
            _old = GL.GenTexture(); // before tapping: read back from OpenGL
            GL.BindTexture(TextureTarget.Texture2D, _old);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, Detail);
            Filter(TextureMinFilter.Nearest);
            _empty = GL.GenVertexArray();
            GL.GenQueries(1, out _query);
            _mapProgram = Parity.Linked(PostVertex, MapFragment);
            _map = GL.GenTexture(); // before tapping, mutable, in a framebuffer object the engine does not list
            GL.BindTexture(TextureTarget.Texture2D, _map);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, MapSide, MapSide, 0, PixelFormat.Rgba,
                PixelType.Float, IntPtr.Zero);
            Filter(TextureMinFilter.Nearest);
            _mapFramebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mapFramebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _map, 0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        // The map Vulkan draws through its copy; OpenGL reads the texture itself at the frame's end (written back then)
        public float[] Map { get; private set; } = [];

        private void Mapped(FrameBufferRef into)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mapFramebuffer);
            GL.Viewport(0, 0, MapSide, MapSide);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(_mapProgram);
            GL.Uniform3(GL.GetUniformLocation(_mapProgram, "gain"), 0.8f, 0.6f, 0.3f);
            GL.BindVertexArray(_empty);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, into.FboId);
            GL.Viewport(0, 0, Side, Side);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            GL.UseProgram(_post);
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture2D, _map);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_post, "scene"), 6);
            GL.Uniform3(GL.GetUniformLocation(_post, "gain"), 0.25f, 0.25f, 0.25f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.Disable(EnableCap.Blend);
        }

        public float[][][] Frame(TerrainRenderer? renderer)
        {
            var framebuffers = Framebuffers();
            try
            {
                if (renderer is not null)
                    Assert.That(renderer.Start(framebuffers, [Primary, Post], out var why), Is.True, why);
                foreach (var f in framebuffers) Parity.Clear(f, Background);
                Entities(framebuffers[Primary]);
                Fullscreen(framebuffers[Primary], framebuffers[Post]);
                Mapped(framebuffers[Post]);
                Scissored(framebuffers[Post]);
                Flat(framebuffers[Post]);
                Blits(framebuffers[Primary], framebuffers[Post]);
                renderer?.Frame.Close("the frame's end");
                var map = new float[MapSide * MapSide * 4];
                GL.GetTextureImage(_map, 0, PixelFormat.Rgba, PixelType.Float, map.Length * 4, map);
                Map = map;
                GL.GetQueryObject(_query, GetQueryObjectParam.QueryResult, out int samples);
                Samples = samples;
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                return [.. framebuffers.Select(f => Parity.Read(f, Side))];
            }
            finally
            {
                foreach (var f in framebuffers)
                {
                    renderer?.Release(f);
                    Parity.Delete(f);
                }

                foreach (var name in _frame) GL.DeleteTexture(name);
                _frame.Clear();
            }
        }

        private static void Blits(FrameBufferRef from, FrameBufferRef into)
        {
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, from.FboId);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, into.FboId);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment1);
            GL.BlitFramebuffer(0, 0, Side / 4, Side / 4, 0, 0, Side / 2, Side / 2, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BlitFramebuffer(Side, 0, Side / 2, Side / 2, Side / 2, Side / 2, Side, Side, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void Entities(FrameBufferRef target)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.FboId);
            GL.Viewport(0, 0, Side, Side);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            var texture = GL.GenTexture(); // made after tapping: uploaded as BGRA, swizzled into the mirror
            _frame.Add(texture);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 8, 8, 0, PixelFormat.Bgra,
                PixelType.UnsignedByte, [.. Enumerable.Range(0, 256).Select(i => (byte)(i % 4 == 3 ? 200 : i * 7))]);
            Filter(TextureMinFilter.Linear);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, _old);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 4, 4, PixelFormat.Rgba, PixelType.UnsignedByte, Detail);
            GL.ActiveTexture(TextureUnit.Texture0);
            var ubo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, ubo);
            GL.BufferData(BufferTarget.UniformBuffer, 48, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BufferSubData(BufferTarget.UniformBuffer, 0, 48, Animation);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 1, ubo);
            var (vao, buffers) = Quad();
            GL.UseProgram(_entity);
            GL.UniformMatrix4(GL.GetUniformLocation(_entity, "projection"), 1, false, Projection);
            GL.Uniform1(GL.GetUniformLocation(_entity, "tex"), 1);
            GL.Uniform1(GL.GetUniformLocation(_entity, "detail"), 2);
            GL.Uniform1(GL.GetUniformLocation(_entity, "alphaTest"), 0.05f);
            GL.BindVertexArray(vao);
            GL.BeginQuery(QueryTarget.SamplesPassed, _query);
            GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, IntPtr.Zero, 2);
            GL.EndQuery(QueryTarget.SamplesPassed);
            // a patch of the old texture uploaded between two draws that sample it: the second sees it, the first not
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 1, 1, 2, 2, PixelFormat.Bgra, PixelType.UnsignedByte, Patch);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, IntPtr.Zero, 1);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffers(buffers.Length, buffers);
            GL.DeleteBuffer(ubo);
        }

        // A quad as UploadMesh makes one: positions, uvs, colors as normalized bytes, a per-instance offset, indices
        private static (int Vao, int[] Buffers) Quad()
        {
            var vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            var buffers = new int[5];
            GL.GenBuffers(5, buffers);
            Attribute(buffers[0], [-0.6f, -0.6f, 0.2f, 0.6f, -0.6f, 0.4f, 0.6f, 0.6f, 0.6f, -0.6f, 0.6f, 0.4f], 0, 3);
            Attribute(buffers[1], [0, 0, 1, 0, 1, 1, 0, 1], 1, 2);
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffers[2]);
            GL.BufferData(BufferTarget.ArrayBuffer, 16, Colors, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
            // interleaved behind other floats, as the particles' position and scale: the pointer's offset has to be read
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffers[3]);
            GL.BufferData(BufferTarget.ArrayBuffer, 32, [9f, 9f, -0.2f, 0, 9f, 9f, 0.25f, 0.1f], BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, 16, 8);
            GL.VertexAttribDivisor(3, 1);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffers[4]);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 24, Indices, BufferUsageHint.StaticDraw);
            for (var i = 0; i < 4; i++) GL.EnableVertexAttribArray(i);
            GL.BindVertexArray(0);
            return (vao, buffers);
        }

        private static void Attribute(int buffer, float[] data, int location, int size)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * 4, data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, 0, 0);
        }

        private void Fullscreen(FrameBufferRef from, FrameBufferRef into)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, into.FboId);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(_post);
            GL.ActiveTexture(TextureUnit.Texture4);
            GL.BindTexture(TextureTarget.Texture2D, from.ColorTextureIds[0]);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_post, "scene"), 4);
            GL.Uniform3(GL.GetUniformLocation(_post, "gain"), 0.9f, 1f, 0.8f);
            GL.BindVertexArray(_empty);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        private static void Scissored(FrameBufferRef target)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.FboId);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(40, 2, 20, 10);
            GL.ClearColor(0.9f, 0.3f, 0.1f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Disable(EnableCap.ScissorTest);
        }

        private void Flat(FrameBufferRef target)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.FboId);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(8, 12, 40, 30);
            var ramp = GL.GenTexture();
            _frame.Add(ramp);
            GL.ActiveTexture(TextureUnit.Texture5);
            GL.BindTexture(TextureTarget.Texture2D, ramp);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, 2, 2, 0, PixelFormat.Rgba,
                PixelType.Float, Ramp);
            Filter(TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.ActiveTexture(TextureUnit.Texture0);
            var vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            var buffers = new int[3];
            GL.GenBuffers(3, buffers);
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffers[0]);
            GL.BufferData(BufferTarget.ArrayBuffer, 32, Corners, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, 0);
            GL.EnableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffers[1]);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 8, Strip, BufferUsageHint.StaticDraw);
            GL.UseProgram(_flat);
            GL.Uniform1(GL.GetUniformLocation(_flat, "ramp"), 5);
            GL.Uniform1(GL.GetUniformLocation(_flat, "useRamp"), 1);
            GL.Uniform2(GL.GetUniformLocation(_flat, "scale"), 0.8f, 0.7f);
            GL.DrawElements(PrimitiveType.TriangleStrip, 4, DrawElementsType.UnsignedShort, IntPtr.Zero);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffers[2]);
            GL.BufferData(BufferTarget.ArrayBuffer, 32, Lines, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, 0);
            GL.Uniform1(GL.GetUniformLocation(_flat, "useRamp"), 0);
            GL.Uniform4(GL.GetUniformLocation(_flat, "lineColor"), 0.2f, 0.9f, 0.9f, 1f);
            GL.Uniform2(GL.GetUniformLocation(_flat, "scale"), 1f, 1f);
            GL.LineWidth(2); // refused by this forward-compatible context, as Vulkan follows
            GL.DrawArrays(PrimitiveType.Lines, 0, 4);
            GL.LineWidth(1);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.Disable(EnableCap.Blend);
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffers(buffers.Length, buffers);
        }

        private static void Filter(TextureMinFilter filter)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                filter == TextureMinFilter.Nearest ? (int)TextureMagFilter.Nearest : (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        }

        private static List<FrameBufferRef> Framebuffers() => [Made(2), Made(1)];

        private static FrameBufferRef Made(int colors)
        {
            var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
            framebuffer.ColorTextureIds =
                [.. Enumerable.Range(0, colors).Select(i => Target((SizedInternalFormat)0x8058, FramebufferAttachment.ColorAttachment0 + i))];
            framebuffer.DepthTextureId = Target((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment);
            GL.DrawBuffers(colors, [.. Enumerable.Range(0, colors).Select(i => DrawBuffersEnum.ColorAttachment0 + i)]);
            return framebuffer;
        }

        // Mutable, as the engine makes its framebuffer textures (glTexImage2D): a draw can adopt one Start left out
        private static int Target(SizedInternalFormat format, FramebufferAttachment attachment)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            var depth = attachment == FramebufferAttachment.DepthAttachment;
            GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)format, Side, Side, 0,
                depth ? PixelFormat.DepthComponent : PixelFormat.Rgba, depth ? PixelType.Float : PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
            return texture;
        }

        public void Dispose()
        {
            GL.DeleteProgram(_entity);
            GL.DeleteProgram(_post);
            GL.DeleteProgram(_flat);
            GL.DeleteTexture(_old);
            GL.DeleteVertexArray(_empty);
            GL.DeleteQuery(_query);
            GL.DeleteProgram(_mapProgram);
            GL.DeleteFramebuffer(_mapFramebuffer);
            GL.DeleteTexture(_map);
        }
    }

    private static int Visible(float[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4).Count(i => Math.Abs(rgba[4 * i] - 0.1f) > 0.02f);
}

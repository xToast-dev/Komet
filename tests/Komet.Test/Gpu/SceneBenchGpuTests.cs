using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The engine thread's cost of the scene path: a frame of entities (an animation block written before every fourth), block
// entities, instanced particles, post-processing passes, a blit and GUI elements (a texture each, two of them drawn anew), each
// draw with its uniforms, through GlTap into SceneRenderer; and the same frames in OpenGL alone. Options: as the game with the
// options screen open - a tenth of the GUI drawn with no texture bound, a text made anew (a new texture) each frame, and the
// blurred backdrop: the window blitted into a framebuffer of its own, two passes through two more, drawn over the window.
// Explicit: run on its own, e.g. mesa_glthread=true dotnet test tests/Komet.Test -c Release --filter
// "FullyQualifiedName~SceneBenchGpuTests" --logger "console;verbosity=detailed"
[NonParallelizable]
[Explicit("a benchmark")]
[Category("Bench")]
public sealed class SceneBenchGpuTests
{
    private const int Side = 256, Warmup = 300, Measured = 2000, Vaos = 8, Textures = 4, Elements = 35;
    private const int EntityDraws = 90, BlockDraws = 40, ParticleDraws = 20, PostDraws = 10, GuiDraws = 70, GuiTextures = 50;
    private const int Draws = EntityDraws + BlockDraws + ParticleDraws + PostDraws + GuiDraws, Clears = 3;

    private const string EntityVertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        layout(location = 2) in vec4 colorIn;
        uniform mat4 projectionMatrix;
        uniform mat4 viewMatrix;
        uniform mat4 modelMatrix;
        uniform vec4 rgbaAmbientIn;
        uniform vec4 rgbaLightIn;
        uniform vec4 rgbaFogIn;
        uniform float fogMinIn;
        uniform float fogDensityIn;
        uniform vec3 lightPosition;
        uniform int addRenderFlags;
        uniform float extraGlow;
        layout(std140) uniform Animation { mat4 elementTransforms[35]; };
        out vec2 uv;
        out vec4 color;
        out float fog;
        void main() {
            mat4 element = elementTransforms[int(colorIn.a * 34.0)];
            vec4 world = modelMatrix * element * vec4(vertexPositionIn, 1.0);
            vec4 view = viewMatrix * world;
            uv = uvIn;
            float lit = max(dot(normalize(lightPosition), vec3(0.0, 1.0, 0.0)), 0.2);
            color = colorIn * rgbaLightIn * rgbaAmbientIn * lit + vec4(extraGlow * float(addRenderFlags & 7));
            fog = clamp(fogMinIn + fogDensityIn * length(view.xyz), 0.0, 1.0);
            gl_Position = projectionMatrix * view;
        }
        """;

    private const string EntityFragment = """
        #version 330 core
        in vec2 uv;
        in vec4 color;
        in float fog;
        uniform sampler2D entityTex;
        uniform float alphaTest;
        uniform vec4 rgbaFogIn;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        void main() {
            vec4 c = texture(entityTex, uv) * color;
            if (c.a < alphaTest) discard;
            outColor = mix(c, rgbaFogIn, fog);
            outGlow = vec4(c.rgb * 0.25, 1.0);
        }
        """;

    private const string BlockVertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        layout(location = 2) in vec4 colorIn;
        uniform mat4 projectionMatrix;
        uniform mat4 viewMatrix;
        uniform mat4 modelMatrix;
        uniform vec4 rgbaTint;
        uniform vec4 rgbaAmbientIn;
        uniform float dontWarpVertices;
        out vec2 uv;
        out vec4 color;
        void main() {
            uv = uvIn;
            color = colorIn * rgbaTint * rgbaAmbientIn;
            gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vertexPositionIn * (1.0 - dontWarpVertices * 0.01), 1.0);
        }
        """;

    private const string BlockFragment = """
        #version 330 core
        in vec2 uv;
        in vec4 color;
        uniform sampler2D tex;
        uniform float alphaTest;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        void main() {
            vec4 c = texture(tex, uv) * color;
            if (c.a < alphaTest) discard;
            outColor = c;
            outGlow = vec4(0.0, 0.0, 0.0, 1.0);
        }
        """;

    private const string ParticleVertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec4 particlePosSize;
        layout(location = 2) in vec4 particleColor;
        uniform mat4 projectionMatrix;
        uniform mat4 viewMatrix;
        uniform vec3 cameraOffset;
        out vec4 color;
        void main() {
            color = particleColor;
            vec3 at = particlePosSize.xyz + cameraOffset + vertexPositionIn * particlePosSize.w;
            gl_Position = projectionMatrix * viewMatrix * vec4(at, 1.0);
        }
        """;

    private const string ParticleFragment = """
        #version 330 core
        in vec4 color;
        uniform float alphaTest;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        void main() {
            if (color.a < alphaTest) discard;
            outColor = color;
            outGlow = vec4(color.rgb * 0.5, color.a);
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
        uniform float offset;
        out vec4 color;
        void main() { color = vec4(texture(scene, uv + vec2(offset)).rgb * gain, 1.0); }
        """;

    // As the engine's gui program: a texture per element, a dozen uniforms set before each
    private const string GuiVertex = """
        #version 330 core
        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        layout(location = 2) in vec4 colorIn;
        uniform vec4 rgbaIn;
        uniform vec4 rgbaGlowIn;
        uniform int extraGlow;
        uniform mat4 projectionMatrix;
        uniform mat4 modelViewMatrix;
        uniform int applyColor;
        out vec2 uv;
        out vec4 color;
        out vec4 glow;
        void main() {
            uv = uvIn;
            color = applyColor == 1 ? rgbaIn * colorIn : rgbaIn;
            glow = rgbaGlowIn * float(extraGlow) / 255.0;
            gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);
        }
        """;

    private const string GuiFragment = """
        #version 330 core
        in vec2 uv;
        in vec4 color;
        in vec4 glow;
        uniform float noTexture;
        uniform float alphaTest;
        uniform int darkEdges;
        uniform int tempGlowMode;
        uniform sampler2D tex2d;
        uniform sampler2D tex2dOverlay;
        uniform float overlayOpacity;
        out vec4 outColor;
        void main() {
            vec4 c = mix(texture(tex2d, uv), vec4(1.0), noTexture) * color;
            c.rgb = mix(c.rgb, texture(tex2dOverlay, uv).rgb, overlayOpacity) * (darkEdges == 1 ? 0.8 : 1.0);
            if (c.a < alphaTest) discard;
            outColor = c + glow * float(tempGlowMode);
        }
        """;

    private static readonly float[] Projection = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1.002f, -1, 0, 0, -0.2f, 0];
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    private static readonly float[] Background = [0.1f, 0.1f, 0.2f, 1];
    private static readonly int[] Corners = [0, 1, 2, 0, 2, 3];

    // cold: the caches emptied before every draw (1 MB walked), as the engine's own work between its draws leaves them
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void SceneDrawsOnTheEngineThread(bool cold, bool options)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false; // made on builders, as in the game: the warm-up frames' stutters are the engine thread's own
        rig.Backend!.Flush();
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
        var (touching, work) = (GlTap.Touching!, new long[(int)GlTap.Touch.Mipmap + 1]);
        GlTap.Touching = (kind, id) => // what OpenGL still runs: counted
        {
            work[(int)kind]++;
            touching(kind, id);
        };
        var watched = SceneRenderer.Watched;
        SceneRenderer.Watched = 8; // settled long before the measured frames, as the shader cache has them in the game
        using var frame = new BenchFrame { Cold = cold, Options = options };
        var measured = cold ? Measured / 4 : Measured;
        try
        {
            var (worst, first, ready) = (0L, 0L, 0);
            for (var i = 0; i < Warmup; i++)
            {
                var (at, waits) = (Stopwatch.GetTimestamp(), scene.PortWaits + scene.PipelineWaits);
                _ = frame.Run(renderer!);
                var took = Stopwatch.GetTimestamp() - at;
                (worst, first) = (Math.Max(worst, took), i == 0 ? took : first);
                if (scene.PortWaits + scene.PipelineWaits > waits) ready = i + 1;
            }

            TestContext.Out.WriteLine($"{(cold ? "cold" : "hot")}: warm-up frames on the engine thread: first " +
                                      $"{first * 1e3 / Stopwatch.Frequency:0.0} ms, worst {worst * 1e3 / Stopwatch.Frequency:0.0} ms; " +
                                      $"draws left to OpenGL while their port or pipeline was made: {scene.PortWaits} + " +
                                      $"{scene.PipelineWaits}, none from frame {ready} on");
            var (draws, clears, ticks, left) = (scene.Draws, scene.Clears, scene.Ticks, scene.Left);
            var (writes, renames) = (scene.Mirrors.Writes, scene.Mirrors.Renames);
            var (segments, packs, late, closers) = (renderer.Frame.Segments, renderer.Copies.Packs, renderer.Frame.LateSignals,
                new Dictionary<string, long>(renderer.Frame.Closers));
            var worked = (long[])work.Clone();
            var (issued, closing, drawn, bytes) = (new long[measured], new long[measured], new long[measured], 0L);
            for (var i = 0; i < measured; i++)
            {
                var (allocated, before) = (GC.GetAllocatedBytesForCurrentThread(), scene.Ticks);
                (issued[i], closing[i]) = frame.Run(renderer);
                bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                drawn[i] = scene.Ticks - before;
            }

            var (us, f) = (1e6 / Stopwatch.Frequency, renderer.Frame);
            var gl = Enumerable.Range(0, work.Length).Where(k => work[k] > worked[k])
                .Select(k => $"{(GlTap.Touch)k} {(double)(work[k] - worked[k]) / measured:0.00}");
            var closed = f.Closers.Where(c => c.Value > closers.GetValueOrDefault(c.Key))
                .Select(c => $"{c.Key} {(double)(c.Value - closers.GetValueOrDefault(c.Key)) / measured:0.00}");
            var kind = cold ? "cold" : "hot";
            if (options) kind = "options";
            TestContext.Out.WriteLine($"{kind}, measured frames: " +
                                      $"{(double)(f.Segments - segments) / measured:0.00} segments a frame, closed by " +
                                      $"{string.Join(", ", closed)}; OpenGL ran a frame: {string.Join(", ", gl)}; " +
                                      $"{(double)(scene.Left - left) / measured:0.00} draws left to OpenGL a frame " +
                                      $"({string.Join("; ", scene.Reasons)}); {(double)(renderer.Copies.Packs - packs) / measured:0.00} " +
                                      $"levels packed, {(double)(f.LateSignals - late) / measured:0.00} late signals a frame");
            if (!options)
            {
                Assert.That(scene.Left, Is.EqualTo(left), "nothing left to OpenGL once warm: " + string.Join("; ", scene.Reasons));
                Assert.That(scene.Draws - draws, Is.EqualTo((long)measured * Draws), "every draw taken");
                Assert.That(scene.Clears - clears, Is.EqualTo((long)measured * Clears), "every clear taken");
            }

            double Median(long[] values) => values.Order().ElementAt(values.Length / 2) * us;
            TestContext.Out.WriteLine(
                $"{(cold ? "cold" : "hot")}: {Draws} draws + {Clears} clears a frame, {measured} frames, medians a frame: " +
                $"SceneRenderer.Draw {Median(drawn) / Draws:0.00} µs a draw (mean {(scene.Ticks - ticks) * us / (scene.Draws - draws):0.00}); " +
                $"the frame's GL calls (taps, uniforms, uploads, draws) {Median(issued):0.0} µs, frame start and end " +
                $"{Median(closing):0.0} µs; {bytes / measured} bytes allocated a frame; " +
                $"{(double)(scene.Mirrors.Writes - writes) / measured:0.0} mirror writes, " +
                $"{(double)(scene.Mirrors.Renames - renames) / measured:0.0} renamed a frame; " +
                $"{(double)f.Segments / f.Frames:0.0} segments a frame, closed by " +
                string.Join(", ", f.Closers.OrderByDescending(c => c.Value).Take(4).Select(c => $"{c.Key} {c.Value}")));
            TestContext.Out.WriteLine($"frame, every frame so far, µs a frame: opening {f.OpenTicks * us / f.Frames:0.0}, OpenGL's " +
                                      $"signal {f.SignalTicks * us / f.Frames:0.0}, submitting {f.SubmitTicks * us / f.Frames:0.0}" +
                                      Hitches.Report() + Builds.Report());
            var copies = renderer.Copies;
            TestContext.Out.WriteLine($"transfers: {VkMemory.Exported} exportable allocations live ({VkMemory.ExportedBytes >> 20} MB), " +
                                      $"{copies.Count} texture copies ({copies.Private} private, {copies.Packs} levels packed, " +
                                      $"{copies.Fills} fills, {copies.Moves} moved), {scene.Mirrors.Imports} buffers imported " +
                                      $"({scene.Mirrors.Asked} sizes asked), {f.LateSignals} late signals, " +
                                      $"{renderer.Staging.Owned} staging buffers of their own");
        }
        finally
        {
            SceneRenderer.Watched = watched;
            frame.Release(renderer!);
            GlTap.Untap();
        }
    }

    // The same frames drawn by OpenGL alone, nothing tapped: what the engine's thread spends on the calls without Komet
    [Test]
    public void TheSameFramesInOpenGl()
    {
        using var rig = GpuRig.Open(GpuRig.Gl);
        using var frame = new BenchFrame();
        for (var i = 0; i < Warmup; i++) _ = frame.Run(null);
        var issued = new long[Measured];
        for (var i = 0; i < Measured; i++) (issued[i], _) = frame.Run(null);
        GL.Finish();
        var median = issued.Order().ElementAt(Measured / 2) * 1e6 / Stopwatch.Frequency;
        TestContext.Out.WriteLine($"opengl: {Draws} draws + {Clears} clears a frame, {Measured} frames, median a frame: the " +
                                  $"frame's GL calls {median:0.0} µs");
        Assert.That(median, Is.Positive);
    }

    // The terrain's pool draws: as many a frame as the game makes, each MultiDrawElements over as many ranges as visible chunks
    [TestCase(200)]
    public void TerrainPoolsOnTheEngineThread(int ranges)
    {
        const int pools = 90;
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        using var scene = new TerrainScene(rig.Device!, false);
        Assert.That(GlTap.Tap(), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        _ = renderer!.Watch(scene.Name, scene.Program, scene.Ported);
        var (pointers, sizes) = (new int[2 * ranges], Enumerable.Repeat(6, ranges).ToArray());
        for (var i = 0; i < ranges; i++) pointers[2 * i] = i % 16 * 24; // a quad's six indices each
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        framebuffer.ColorTextureIds = [BenchFrame.Target(false, FramebufferAttachment.ColorAttachment0)];
        framebuffer.DepthTextureId = BenchFrame.Target(true, FramebufferAttachment.DepthAttachment);
        var ticks = new long[Measured];
        try
        {
            for (var frame = 0; frame < Warmup + Measured; frame++)
            {
                Assert.That(renderer.Start([framebuffer], [0], out why), Is.True, why);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
                GL.Viewport(0, 0, Side, Side);
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                Assert.That(scene.Draw(renderer, new Dictionary<string, int>()), Is.True, string.Join("; ", renderer.Reasons));
                GL.UseProgram(scene.Program);
                var (origin, before) = (GL.GetUniformLocation(scene.Program, "origin"), renderer.TakeTicks);
                for (var pool = 0; pool < pools; pool++)
                {
                    GL.Uniform3(origin, pool * 0.01f, 0f, 0f);
                    Assert.That(renderer.Take(scene.Vao, (pointers, sizes, ranges, true), SegmentSync.Ordered, _ => default));
                }

                if (frame >= Warmup) ticks[frame - Warmup] = renderer.TakeTicks - before;
                GL.UseProgram(0);
                renderer.Frame.Close("the frame's end");
            }

            var median = ticks.Order().ElementAt(Measured / 2) * 1e6 / Stopwatch.Frequency;
            TestContext.Out.WriteLine($"terrain: {pools} pool draws of {ranges} ranges a frame, {Measured} frames: " +
                                      $"TerrainRenderer.Take {median / pools:0.00} µs a pool draw (median)");
        }
        finally
        {
            renderer.Release(framebuffer);
            Parity.Delete(framebuffer);
            GlTap.Untap();
        }
    }

    // Everything made once, after tapping, as the engine makes its meshes while the scene runs
    private sealed class BenchFrame : IDisposable
    {
        private readonly int _entity, _block, _particle, _post, _gui, _ubo, _empty;
        private readonly int[] _gui2d = new int[GuiTextures];
        private readonly byte[] _text = new byte[32 * 16 * 4];
        private readonly int[] _vaos = new int[Vaos], _textures = new int[Textures], _particles = new int[ParticleDraws];
        private readonly int[] _instanceBuffers = new int[ParticleDraws];
        private readonly List<int> _buffers = [];
        private readonly List<FrameBufferRef> _framebuffers;
        private readonly float[] _animation = new float[Elements * 16], _model = (float[])Identity.Clone();
        private readonly float[] _instances = new float[8 * 64];
        private readonly byte[] _cache = new byte[1 << 20];
        private int _frame;

        private long _thrashed;

        public BenchFrame()
        {
            (_entity, _block) = (Parity.Linked(EntityVertex, EntityFragment), Parity.Linked(BlockVertex, BlockFragment));
            (_particle, _post) = (Parity.Linked(ParticleVertex, ParticleFragment), Parity.Linked(PostVertex, PostFragment));
            GL.UniformBlockBinding(_entity, GL.GetUniformBlockIndex(_entity, "Animation"), 1);
            for (var e = 0; e < Elements; e++) Identity.CopyTo(_animation, e * 16);
            _ubo = Buffer(BufferTarget.UniformBuffer, _animation.Length * 4, _animation);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 1, _ubo);
            for (var i = 0; i < Vaos; i++) _vaos[i] = Mesh(i);
            for (var i = 0; i < Textures; i++) _textures[i] = Texture(i, 8, 8);
            for (var i = 0; i < ParticleDraws; i++) (_particles[i], _instanceBuffers[i]) = Particles();
            _empty = GL.GenVertexArray();
            _gui = Parity.Linked(GuiVertex, GuiFragment);
            for (var i = 0; i < GuiTextures; i++) _gui2d[i] = Texture(i + Textures, 32, 16);
            _framebuffers = [Made(2), Made(1)];
        }

        public bool Cold { get; init; }
        public bool Options { get; init; }

        // Not counted as the frame's GL calls
        private void Thrash()
        {
            if (!Cold) return;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < _cache.Length; i += 64) _cache[i]++;
            _thrashed += Stopwatch.GetTimestamp() - start;
        }

        // The time the frame's GL calls took, and the time of the frame's start and end
        public (long Issued, long Closed) Run(TerrainRenderer? renderer)
        {
            var begin = Stopwatch.GetTimestamp();
            _thrashed = 0;
            var why = "";
            Assert.That(renderer?.Start(_framebuffers, [0, 1], out why) ?? true, Is.True, why);
            var started = Stopwatch.GetTimestamp();
            Parity.Clear(_framebuffers[0], Background);
            Entities();
            Blocks();
            Instanced(_frame);
            Post();
            Blit();
            Gui(_frame);
            if (Options) Backdrop();
            var issued = Stopwatch.GetTimestamp();
            renderer?.Frame.Close("the frame's end");
            _frame++;
            return (issued - started - _thrashed, started - begin + Stopwatch.GetTimestamp() - issued);
        }

        private void Blit()
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffers[0].FboId);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffers[1].FboId);
            GL.BlitFramebuffer(0, 0, Side / 4, Side / 4, 0, 0, Side / 4, Side / 4, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void Entities()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[0].FboId);
            GL.Viewport(0, 0, Side, Side);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            GL.Enable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(_entity);
            Common(_entity);
            Uniform(_entity, "rgbaFogIn", 0.5f, 0.6f, 0.7f, 1f);
            GL.Uniform1(GL.GetUniformLocation(_entity, "fogMinIn"), 0.05f);
            GL.Uniform1(GL.GetUniformLocation(_entity, "fogDensityIn"), 0.002f);
            GL.Uniform3(GL.GetUniformLocation(_entity, "lightPosition"), 0.3f, 0.8f, 0.5f);
            GL.Uniform1(GL.GetUniformLocation(_entity, "entityTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_entity, "alphaTest"), 0.05f);
            GL.ActiveTexture(TextureUnit.Texture0);
            var (model, light, flags, glow) = (GL.GetUniformLocation(_entity, "modelMatrix"),
                GL.GetUniformLocation(_entity, "rgbaLightIn"), GL.GetUniformLocation(_entity, "addRenderFlags"),
                GL.GetUniformLocation(_entity, "extraGlow"));
            for (var i = 0; i < EntityDraws; i++)
            {
                if (i % 10 == 0) GL.BindTexture(TextureTarget.Texture2D, _textures[i / 10 % Textures]);
                if (i % 4 == 0)
                {
                    _animation[16 * (i % Elements) + 12] = (_frame + i) % 7 * 0.01f;
                    GL.BindBuffer(BufferTarget.UniformBuffer, _ubo);
                    GL.BufferSubData(BufferTarget.UniformBuffer, 0, _animation.Length * 4, _animation);
                }

                Place(i);
                GL.UniformMatrix4(model, 1, false, _model);
                GL.Uniform4(light, 0.8f + i % 5 * 0.04f, 0.9f, 1f, 1f);
                GL.Uniform1(flags, i % 3);
                GL.Uniform1(glow, i % 2 * 0.1f);
                GL.BindVertexArray(_vaos[i % Vaos]);
                Thrash();
                GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedShort, IntPtr.Zero);
            }

            GL.BindVertexArray(0);
        }

        private void Blocks()
        {
            GL.UseProgram(_block);
            Common(_block);
            GL.Uniform1(GL.GetUniformLocation(_block, "tex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_block, "alphaTest"), 0.1f);
            GL.Uniform1(GL.GetUniformLocation(_block, "dontWarpVertices"), 0f);
            var (model, tint) = (GL.GetUniformLocation(_block, "modelMatrix"), GL.GetUniformLocation(_block, "rgbaTint"));
            for (var i = 0; i < BlockDraws; i++)
            {
                if (i % 15 == 0)
                {
                    if (i / 15 % 2 == 0) GL.Disable(EnableCap.CullFace);
                    else GL.Enable(EnableCap.CullFace);
                }

                if (i % 6 == 0) GL.BindTexture(TextureTarget.Texture2D, _textures[(i / 6 + 1) % Textures]);
                Place(i + 7);
                GL.UniformMatrix4(model, 1, false, _model);
                GL.Uniform4(tint, 1f, 1f - i % 3 * 0.1f, 1f, 1f);
                GL.BindVertexArray(_vaos[(i + 3) % Vaos]);
                Thrash();
                GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedShort, IntPtr.Zero);
            }

            GL.BindVertexArray(0);
            GL.Enable(EnableCap.CullFace);
        }

        private void Instanced(int frame)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.UseProgram(_particle);
            Common(_particle);
            GL.Uniform1(GL.GetUniformLocation(_particle, "alphaTest"), 0.01f);
            var offset = GL.GetUniformLocation(_particle, "cameraOffset");
            for (var i = 0; i < ParticleDraws; i++)
            {
                GL.Uniform3(offset, i * 0.01f, 0f, -frame % 5 * 0.01f);
                GL.BindVertexArray(_particles[i]);
                if (i % 4 == 0)
                {
                    _instances[0] = frame % 11 * 0.01f;
                    GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceBuffers[i]);
                    GL.BufferSubData(BufferTarget.ArrayBuffer, 0, _instances.Length * 4, _instances);
                }

                Thrash();
                GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, 64);
            }

            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Enable(EnableCap.CullFace);
        }

        private void Post()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[1].FboId);
            GL.Disable(EnableCap.DepthTest);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            GL.UseProgram(_post);
            GL.ActiveTexture(TextureUnit.Texture4);
            GL.BindTexture(TextureTarget.Texture2D, _framebuffers[0].ColorTextureIds[0]);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_post, "scene"), 4);
            var (gain, offset) = (GL.GetUniformLocation(_post, "gain"), GL.GetUniformLocation(_post, "offset"));
            GL.BindVertexArray(_empty);
            for (var i = 0; i < PostDraws; i++)
            {
                GL.Uniform3(gain, 0.1f, 0.1f + i * 0.01f, 0.1f);
                GL.Uniform1(offset, i * 0.001f);
                Thrash();
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

            GL.BindVertexArray(0);
        }

        private void Gui(int frame)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[1].FboId);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(_gui);
            GL.UniformMatrix4(GL.GetUniformLocation(_gui, "projectionMatrix"), 1, false, Identity);
            GL.Uniform1(GL.GetUniformLocation(_gui, "tex2d"), 0);
            GL.Uniform1(GL.GetUniformLocation(_gui, "tex2dOverlay"), 1);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _textures[0]);
            GL.ActiveTexture(TextureUnit.Texture0);
            int At(string name) => GL.GetUniformLocation(_gui, name);
            var (rgba, glowIn, glow, modelView, color) = (At("rgbaIn"), At("rgbaGlowIn"), At("extraGlow"), At("modelViewMatrix"),
                At("applyColor"));
            var (none, alpha, dark, temp, overlay) = (At("noTexture"), At("alphaTest"), At("darkEdges"), At("tempGlowMode"),
                At("overlayOpacity"));
            for (var t = 0; t < 2; t++) // text that changed: drawn into its texture anew
            {
                _text[0] = (byte)(frame + t);
                GL.BindTexture(TextureTarget.Texture2D, _gui2d[(frame + 7 * t) % GuiTextures]);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 32, 16, PixelFormat.Bgra, PixelType.UnsignedByte, _text);
            }

            if (Options) Remade(frame);
            GL.BindVertexArray(_vaos[0]);
            for (var i = 0; i < GuiDraws + (Options ? 1 : 0); i++)
            {
                var untextured = Options && i % 7 == 0 && i < GuiDraws;
                var texture = i == GuiDraws ? _remade : _gui2d[i % GuiTextures];
                GL.BindTexture(TextureTarget.Texture2D, untextured ? 0 : texture);
                Place(i);
                GL.UniformMatrix4(modelView, 1, false, _model);
                GL.Uniform4(rgba, 1f, 1f, 1f, 0.9f + i % 2 * 0.1f);
                GL.Uniform4(glowIn, 0f, 0f, 0f, 0f);
                GL.Uniform1(glow, 0);
                GL.Uniform1(color, i % 2);
                GL.Uniform1(none, untextured ? 1f : 0f);
                GL.Uniform1(alpha, 0.001f);
                GL.Uniform1(dark, 0);
                GL.Uniform1(temp, 0);
                GL.Uniform1(overlay, 0f);
                Thrash();
                GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.Disable(EnableCap.Blend);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private int _remade;
        private readonly (int Texture, int Framebuffer)[] _backdrop = new (int, int)[3];

        // A text made anew, as LoadOrUpdateCairoTexture makes one whose size changed: the old texture deleted
        private void Remade(int frame)
        {
            if (_remade != 0) GL.DeleteTexture(_remade);
            _remade = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _remade);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _text[1] = (byte)frame;
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 32 - frame % 2 * 4, 16, 0, PixelFormat.Bgra,
                PixelType.UnsignedByte, _text);
        }

        // As Komet's options screen draws its backdrop: the window blitted into a texture of its own, shrunk and flipped
        // through two more, the result drawn over the window
        private void Backdrop()
        {
            if (_backdrop[0].Texture == 0)
                for (var i = 0; i < 3; i++)
                    _backdrop[i] = Offscreen(i == 0 ? Side : Side / 4);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffers[1].FboId);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _backdrop[0].Framebuffer);
            GL.BlitFramebuffer(0, 0, Side, Side, 0, 0, Side, Side, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(_post);
            GL.Uniform1(GL.GetUniformLocation(_post, "scene"), 0);
            GL.Uniform3(GL.GetUniformLocation(_post, "gain"), 1f, 1f, 1f);
            GL.Uniform1(GL.GetUniformLocation(_post, "offset"), 0.001f);
            GL.BindVertexArray(_empty);
            GL.Viewport(0, 0, Side / 4, Side / 4);
            for (var pass = 1; pass < 3; pass++)
            {
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _backdrop[pass].Framebuffer);
                GL.BindTexture(TextureTarget.Texture2D, _backdrop[pass - 1].Texture);
                Thrash();
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

            GL.Viewport(0, 0, Side, Side);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[1].FboId);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.BindTexture(TextureTarget.Texture2D, _backdrop[2].Texture);
            GL.Uniform3(GL.GetUniformLocation(_post, "gain"), 0.5f, 0.5f, 0.5f);
            Thrash();
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.Disable(EnableCap.Blend);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        // A framebuffer of the options screen's own, which the engine does not list
        private static (int Texture, int Framebuffer) Offscreen(int side)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, side, side, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            var framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
                texture, 0);
            return (texture, framebuffer);
        }

        private static void Common(int program)
        {
            GL.UniformMatrix4(GL.GetUniformLocation(program, "projectionMatrix"), 1, false, Projection);
            GL.UniformMatrix4(GL.GetUniformLocation(program, "viewMatrix"), 1, false, Identity);
            var ambient = GL.GetUniformLocation(program, "rgbaAmbientIn");
            if (ambient >= 0) GL.Uniform4(ambient, 0.9f, 0.9f, 1f, 1f);
        }

        private static void Uniform(int program, string name, float x, float y, float z, float w) =>
            GL.Uniform4(GL.GetUniformLocation(program, name), x, y, z, w);

        private void Place(int i)
        {
            (_model[12], _model[13], _model[14]) = (i % 11 * 0.15f - 0.8f, i / 11 % 11 * 0.15f - 0.8f, -2f - i % 3);
            _model[0] = _model[5] = _model[10] = 0.05f + i % 4 * 0.01f;
        }

        private int Buffer(BufferTarget target, int bytes, float[] data)
        {
            var buffer = GL.GenBuffer();
            GL.BindBuffer(target, buffer);
            GL.BufferData(target, bytes, data, BufferUsageHint.DynamicDraw);
            _buffers.Add(buffer);
            return buffer;
        }

        // A cube of 24 corners: positions, uvs, colors as normalized bytes (alpha picks the animation element), indices
        private int Mesh(int seed)
        {
            var vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            var (positions, uvs, colors) = (new float[24 * 3], new float[24 * 2], new byte[24 * 4]);
            for (var v = 0; v < 24; v++)
            {
                (positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]) =
                    ((v & 1) - 0.5f, ((v >> 1) & 1) - 0.5f, ((v >> 2) % 2) - 0.5f + seed * 0.01f);
                (uvs[2 * v], uvs[2 * v + 1]) = (v & 1, (v >> 1) & 1);
                (colors[4 * v], colors[4 * v + 1], colors[4 * v + 2], colors[4 * v + 3]) =
                    ((byte)(200 + seed), 220, 255, (byte)(v * 10 % 256));
            }

            Attribute(Buffer(BufferTarget.ArrayBuffer, positions.Length * 4, positions), 0, 3, VertexAttribPointerType.Float,
                false);
            Attribute(Buffer(BufferTarget.ArrayBuffer, uvs.Length * 4, uvs), 1, 2, VertexAttribPointerType.Float, false);
            var color = GL.GenBuffer();
            _buffers.Add(color);
            GL.BindBuffer(BufferTarget.ArrayBuffer, color);
            GL.BufferData(BufferTarget.ArrayBuffer, colors.Length, colors, BufferUsageHint.StaticDraw);
            Attribute(color, 2, 4, VertexAttribPointerType.UnsignedByte, true);
            var indices = Enumerable.Range(0, 36).Select(i => (ushort)((i / 6 * 4 + Corners[i % 6]) % 24))
                .ToArray();
            var elements = GL.GenBuffer();
            _buffers.Add(elements);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, elements);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * 2, indices, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
            return vao;
        }

        private (int Vao, int Instances) Particles()
        {
            var vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            float[] quad = [-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0];
            Attribute(Buffer(BufferTarget.ArrayBuffer, quad.Length * 4, quad), 0, 3, VertexAttribPointerType.Float, false);
            ushort[] indices = [0, 1, 2, 0, 2, 3];
            var elements = GL.GenBuffer();
            _buffers.Add(elements);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, elements);
            GL.BufferData(BufferTarget.ElementArrayBuffer, 12, indices, BufferUsageHint.StaticDraw);
            for (var i = 0; i < 64; i++)
                (_instances[8 * i], _instances[8 * i + 1], _instances[8 * i + 2], _instances[8 * i + 3]) =
                    (i % 8 * 0.2f - 0.8f, i / 8 * 0.2f - 0.8f, -3f, 0.05f);
            for (var i = 0; i < 64; i++)
                (_instances[8 * i + 4], _instances[8 * i + 5], _instances[8 * i + 6], _instances[8 * i + 7]) =
                    (1f, 0.5f, 0.2f, 0.5f);
            var instances = Buffer(BufferTarget.ArrayBuffer, _instances.Length * 4, _instances);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 32, 0);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 32, 16);
            GL.EnableVertexAttribArray(1);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribDivisor(1, 1);
            GL.VertexAttribDivisor(2, 1);
            GL.BindVertexArray(0);
            return (vao, instances);
        }

        private static void Attribute(int buffer, int location, int size, VertexAttribPointerType type, bool normalized)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.VertexAttribPointer(location, size, type, normalized, 0, 0);
            GL.EnableVertexAttribArray(location);
        }

        private static int Texture(int seed, int width, int height)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            var pixels = Enumerable.Range(0, width * height * 4).Select(i => (byte)(i % 4 == 3 ? 255 : (i * 7 + seed * 40) % 256))
                .ToArray();
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return texture;
        }

        private static FrameBufferRef Made(int colors)
        {
            var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
            framebuffer.ColorTextureIds =
                [.. Enumerable.Range(0, colors).Select(i => Target(false, FramebufferAttachment.ColorAttachment0 + i))];
            framebuffer.DepthTextureId = Target(true, FramebufferAttachment.DepthAttachment);
            GL.DrawBuffers(colors, [.. Enumerable.Range(0, colors).Select(i => DrawBuffersEnum.ColorAttachment0 + i)]);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return framebuffer;
        }

        public static int Target(bool depth, FramebufferAttachment attachment)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, depth ? (PixelInternalFormat)0x81A7 : PixelInternalFormat.Rgba8, Side,
                Side, 0, depth ? PixelFormat.DepthComponent : PixelFormat.Rgba, depth ? PixelType.Float : PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
            return texture;
        }

        public void Release(TerrainRenderer renderer)
        {
            foreach (var framebuffer in _framebuffers) renderer.Release(framebuffer);
        }

        public void Dispose()
        {
            foreach (var program in (int[])[_entity, _block, _particle, _post, _gui]) GL.DeleteProgram(program);
            GL.DeleteTextures(_gui2d.Length, _gui2d);
            GL.DeleteVertexArrays(_vaos.Length, _vaos);
            GL.DeleteVertexArrays(_particles.Length, _particles);
            GL.DeleteVertexArray(_empty);
            GL.DeleteTextures(_textures.Length, _textures);
            GL.DeleteBuffers(_buffers.Count, [.. _buffers]);
            if (_remade != 0) GL.DeleteTexture(_remade);
            foreach (var (texture, framebuffer) in _backdrop.Where(b => b.Texture != 0))
            {
                GL.DeleteFramebuffer(framebuffer);
                GL.DeleteTexture(texture);
            }
            foreach (var framebuffer in _framebuffers) Parity.Delete(framebuffer);
        }
    }
}

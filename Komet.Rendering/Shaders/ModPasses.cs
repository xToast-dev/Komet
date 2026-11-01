using Komet.Api;
using OpenTK.Graphics.OpenGL;

namespace Komet.Rendering;

// KometShaders' fullscreen passes, after the game's final composition (KometPassStage.Final): the primary framebuffer's
// picture is blitted into a copy of the same size, then each enabled pass draws one triangle over the primary framebuffer's
// first colour attachment, reading the copy (scene) and the scene's depth. Plain GL calls; under Vulkan-only the tap takes
// the blit and the draws into Vulkan's frame and ports the programs at link, as for any mod's raw GL. The GL state the
// engine relies on is put back after the stage. Also tells KometShaders when the renderer switched.
internal sealed class ModPasses : IRenderer
{
    private const int MaxSource = 64 << 10, MaxSize = 16384, DrawBuffer0 = 0x8825, MaxDrawBuffers = 4;

    private const string Vertex = """
        #version 330 core
        out vec2 uv;
        void main()
        {
            vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            uv = corner;
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    // After the version line and the defines (KOMET, KOMET_VULKAN under Vulkan, the mod's own)
    internal const string Prelude = """
        in vec2 uv;
        out vec4 color;
        uniform sampler2D scene;
        uniform sampler2D depth;
        uniform vec2 resolution;
        uniform float time;
        #line 1
        """;

    // A pass and its program, built at its first draw; a source that fails stays failed (logged once)
    private sealed class Entry(KometPass pass)
    {
        public KometPass Pass { get; } = pass;
        public int Program { get; set; }
        public bool Failed { get; set; }
        public string Why { get; set; } = "";
        public (int Scene, int Depth, int Resolution, int Time) Locations { get; set; } = (-1, -1, -1, -1);
        public Dictionary<string, int> OwnAt { get; } = [];
    }

    private static readonly List<Entry> Passes = [];
    private static ModPasses? _renderer;
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;
    private static KometRenderer _drawing;

    private readonly int[] _drawBuffers = new int[MaxDrawBuffers], _viewport = new int[4];
    private readonly DrawBuffersEnum[] _restored = new DrawBuffersEnum[MaxDrawBuffers];
    private readonly Entry[] _frame = new Entry[KometShaders.MaxPasses]; // this frame's passes: a callback may add or remove one
    private readonly bool[] _on = new bool[KometShaders.MaxPasses]; // and their Enabled, asked once a frame
    private int _copy, _copyFbo, _vao;
    private (int W, int H) _size;
    private float _time;

    public double RenderOrder => 0.99; // after the stage's other renderers: the finished picture
    public int RenderRange => 0;

    public static void Install(ICoreClientAPI api, ILogger logger)
    {
        (_api, _logger, _drawing) = (api, logger, KometShaders.Renderer);
        if (!NotNull(api) || !NotNull(api.Event) || _renderer is not null) return;
        var renderer = new ModPasses();
        api.Event.RegisterRenderer(renderer, EnumRenderStage.AfterFinalComposition, "komet-mod-passes");
        _renderer = renderer;
    }

    // The world closed: passes and programs go; the renderer is the world's and goes with it
    public static void Clear()
    {
        foreach (var entry in Passes.Bounded(KometShaders.MaxPasses)) Drop(entry);
        Passes.Clear();
        if (_renderer is { } renderer)
        {
            _api?.Event?.UnregisterRenderer(renderer, EnumRenderStage.AfterFinalComposition);
            renderer.Dispose();
        }

        (_renderer, _api) = (null, null);
    }

    // Every pass for the overview page: its mod, id, and why it is left out ("" while it is fine)
    internal static IEnumerable<(string Mod, string Id, string Failure)> Listed() =>
        [.. Passes.Take(KometShaders.MaxPasses).Select(e => (e.Pass.ModId, e.Pass.Id, e.Why))];

    public static IDisposable Add(KometPass pass)
    {
        if (!NotNull(pass) || pass.ModId.Length == 0 || pass.Id.Length == 0 || pass.Fragment.Length is 0 or > MaxSource ||
            !Assert(Passes.Count < KometShaders.MaxPasses)) return Registration.None;
        // a mod's pass again: the new one, and the old one's program goes (its registration no longer finds it to drop it)
        var old = Passes.FindIndex(e => e.Pass.ModId == pass.ModId && e.Pass.Id == pass.Id);
        if (old >= 0)
        {
            Drop(Passes[old]);
            Passes.RemoveAt(old);
        }

        var entry = new Entry(pass);
        Passes.Add(entry);
        Passes.Sort((a, b) => a.Pass.Order.CompareTo(b.Pass.Order));
        ModShaders.Touch();
        return new Registration(() =>
        {
            if (!Passes.Remove(entry)) return;
            Drop(entry);
            ModShaders.Touch();
        });
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Finite(deltaTime)) return;
        Switched();
        if (Passes.Count == 0 || _api?.Render?.FrameBuffers is not { Count: > 0 } buffers ||
            buffers[(int)EnumFrameBuffer.Primary] is not { Width: > 0, Height: > 0, FboId: > 0 } primary) return;
        _time += deltaTime;
        var (count, any) = Snapshot();
        if (any && Target(primary.Width, primary.Height)) DrawAll(count, primary);
        Array.Clear(_frame, 0, count);
    }

    // This frame's passes, each Enabled callback asked once (a mod's callback may be costly), and whether any is on
    private (int Count, bool Any) Snapshot()
    {
        var count = Math.Min(Passes.Count, KometShaders.MaxPasses);
        Passes.CopyTo(0, _frame, 0, count);
        var any = false;
        for (var i = 0; i < Math.Min(count, KometShaders.MaxPasses); i++)
        {
            _on[i] = Enabled(_frame[i]);
            any |= _on[i];
        }

        return (count, any);
    }

    private void DrawAll(int count, FrameBufferRef primary)
    {
        var saved = Save();
        try
        {
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.ScissorTest);
            GL.DepthMask(false);
            GL.Viewport(0, 0, primary.Width, primary.Height);
            GL.BindVertexArray(_vao);
            for (var i = 0; i < Math.Min(count, KometShaders.MaxPasses); i++)
                if (_on[i] && Built(_frame[i]))
                    Draw(_frame[i], primary);
        }
        finally
        {
            Restore(saved);
        }
    }

    // The renderer switched (options menu, a fault): KometShaders.RendererChanged
    private static void Switched()
    {
        var now = KometShaders.Renderer;
        if (now == _drawing) return;
        _drawing = now;
        KometShaders.RaiseRendererChanged();
    }

    private static bool Enabled(Entry entry)
    {
        if (entry.Failed || !ModShaders.IsOn(entry.Pass.ModId)) return false;
        try
        {
            return entry.Pass.Enabled?.Invoke() ?? true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(entry, "its Enabled threw: " + e.Message);
            return false;
        }
    }

    // The picture so far copied, then the pass over the primary framebuffer's first attachment
    private void Draw(Entry entry, FrameBufferRef primary)
    {
        var (w, h) = (primary.Width, primary.Height);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, primary.FboId);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, primary.FboId);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.UseProgram(entry.Program);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _copy);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, primary.DepthTextureId);
        GL.ActiveTexture(TextureUnit.Texture0);
        var at = entry.Locations;
        if (at.Scene >= 0) GL.Uniform1(at.Scene, 0);
        if (at.Depth >= 0) GL.Uniform1(at.Depth, 1);
        if (at.Resolution >= 0) GL.Uniform2(at.Resolution, (float)w, h);
        if (at.Time >= 0) GL.Uniform1(at.Time, _time);
        Own(entry);
        ModShaders.UsedBy(entry.Program); // KometShaders.Uniform reaches passes too
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    // The pass's own uniforms, by name
    private static void Own(Entry entry)
    {
        var uniforms = entry.Pass.Uniforms;
        _ = Assert(uniforms.Count <= KometShaders.MaxUniforms);
        using var each = uniforms.GetEnumerator(); // struct enumerator: no iterator nor boxed enumerator per pass per frame
        for (var i = 0; i < KometShaders.MaxUniforms && each.MoveNext(); i++)
        {
            var (name, value) = each.Current;
            if (!entry.OwnAt.TryGetValue(name, out var at)) entry.OwnAt[name] = at = GL.GetUniformLocation(entry.Program, name);
            if (at < 0) continue;
            float[]? v;
            try
            {
                v = value?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Fail(entry, "its uniform " + name + " threw: " + e.Message);
                return;
            }

            ModShaders.Upload(at, v);
        }
    }

    private static bool Built(Entry entry)
    {
        if (entry.Program != 0) return true;
        if (entry.Failed) return false;
        var (vertex, fragment) = (Stage(ShaderType.VertexShader, Vertex, entry), Stage(ShaderType.FragmentShader, Source(entry.Pass), entry));
        if (vertex == 0 || fragment == 0)
        {
            if (vertex != 0) GL.DeleteShader(vertex);
            if (fragment != 0) GL.DeleteShader(fragment);
            return false;
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            Fail(entry, "it did not link: " + GL.GetProgramInfoLog(program));
            GL.DeleteProgram(program);
            return false;
        }

        entry.Program = program;
        entry.Locations = (GL.GetUniformLocation(program, "scene"), GL.GetUniformLocation(program, "depth"),
            GL.GetUniformLocation(program, "resolution"), GL.GetUniformLocation(program, "time"));
        return true;
    }

    // The pass's whole fragment source: version, defines, prelude, the mod's body
    internal static string Source(KometPass pass)
    {
        if (!NotNull(pass)) return "";
        var vulkan = KometShaders.Renderer == KometRenderer.Vulkan ? "#define KOMET_VULKAN 1\n" : "";
        return "#version 330 core\n#define KOMET 1\n" + vulkan + ModShaders.PassDefines(pass.ModId, pass.Id) + Prelude + "\n" + pass.Fragment;
    }

    // Shaders reloaded (defines, the renderer): every pass is built again at its next draw, a failed one gets another try
    internal static void Rebuild()
    {
        foreach (var entry in Passes.Bounded(KometShaders.MaxPasses))
        {
            Drop(entry);
            (entry.Failed, entry.Why) = (false, "");
            entry.OwnAt.Clear();
        }
    }

    private static int Stage(ShaderType type, string source, Entry entry)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled != 0) return shader;
        Fail(entry, "it did not compile (line numbers are the mod's): " + GL.GetShaderInfoLog(shader));
        GL.DeleteShader(shader);
        return 0;
    }

    private static void Fail(Entry entry, string why)
    {
        if (entry.Failed) return;
        (entry.Failed, entry.Why) = (true, why.Split('\n', 2)[0]);
        ModShaders.Touch();
        _logger?.Warning("Komet shaders: pass {0}:{1} is left out, {2}", entry.Pass.ModId, entry.Pass.Id, why);
    }

    private static void Drop(Entry entry)
    {
        if (entry.Program == 0) return;
        GL.DeleteProgram(entry.Program);
        ModShaders.Forget();
        entry.Program = 0;
    }

    // The copy the size of the primary framebuffer (16-bit float: HDR or not, nothing clips), made anew with its size
    private bool Target(int width, int height)
    {
        if (_size == (width, height) && _copy != 0) return true;
        if (!Assert(width is > 0 and <= MaxSize) || !Assert(height is > 0 and <= MaxSize)) return false;
        Release();
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFb);
        GL.GetInteger(GetPName.TextureBinding2D, out int bound);
        try
        {
            _copy = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _copy);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.HalfFloat,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _copyFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _copy, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete) Release();
            if (_vao == 0) _vao = GL.GenVertexArray();
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.BindTexture(TextureTarget.Texture2D, bound);
        }

        _size = _copy != 0 ? (width, height) : default;
        return _copy != 0;
    }

    private void Release()
    {
        if (_copyFbo != 0) GL.DeleteFramebuffer(_copyFbo);
        if (_copy != 0) GL.DeleteTexture(_copy);
        (_copy, _copyFbo, _size) = (0, 0, default);
    }

    private readonly record struct State(int Program, int Vao, int Unit, int Texture0, int Texture1, int Read, int Draw, int ReadBuffer,
        bool Blend, bool Depth, bool Scissor, bool DepthMask);

    private State Save()
    {
        GL.GetInteger(GetPName.CurrentProgram, out int program);
        GL.GetInteger(GetPName.VertexArrayBinding, out int vao);
        GL.GetInteger(GetPName.ActiveTexture, out int unit);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture0);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int read);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        GL.GetInteger(GetPName.ReadBuffer, out int readBuffer);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        for (var i = 0; i < MaxDrawBuffers; i++) GL.GetInteger((GetPName)(DrawBuffer0 + i), out _drawBuffers[i]);
        GL.GetInteger(GetPName.Viewport, _viewport);
        return new State(program, vao, unit, texture0, texture1, read, draw, readBuffer, GL.IsEnabled(EnableCap.Blend),
            GL.IsEnabled(EnableCap.DepthTest), GL.IsEnabled(EnableCap.ScissorTest), depthMask);
    }

    private void Restore(State s)
    {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, s.Read);
        GL.ReadBuffer((ReadBufferMode)s.ReadBuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, s.Draw);
        var count = 0; // the saved list up to its first GL_NONE, without two arrays a frame
        for (var i = 0; i < MaxDrawBuffers && _drawBuffers[i] != 0; i++)
        {
            _restored[i] = (DrawBuffersEnum)_drawBuffers[i];
            count = i + 1;
        }

        if (count > 0) GL.DrawBuffers(count, _restored);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, s.Texture1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, s.Texture0);
        GL.ActiveTexture((TextureUnit)s.Unit);
        GL.UseProgram(s.Program);
        GL.BindVertexArray(s.Vao);
        GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
        GL.DepthMask(s.DepthMask);
        Toggle(EnableCap.Blend, s.Blend);
        Toggle(EnableCap.DepthTest, s.Depth);
        Toggle(EnableCap.ScissorTest, s.Scissor);
    }

    private static void Toggle(EnableCap cap, bool on)
    {
        if (on) GL.Enable(cap);
        else GL.Disable(cap);
    }

    public void Dispose()
    {
        Release();
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _vao = 0;
    }
}

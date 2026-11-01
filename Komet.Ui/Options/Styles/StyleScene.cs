using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;

namespace Komet.Options;

// A style's background: its fragment shader run over the whole frame into a texture of the frame's size, then drawn as
// the backdrop is (so Vulkan-only takes it the same way). The style file holds only functions and main(); the prelude
// below gives it what docs/site/articles/styles.md lists. A shader that does not compile is logged with its errors and
// the blur takes its place.
internal sealed class StyleScene(ICoreClientAPI capi) : IDisposable
{
    private const int MaxSize = 16384, MaxSource = 64 << 10;

    private const string Vertex = """
        #version 330 core
        out vec2 uv;
        void main()
        {
            vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            uv = vec2(corner.x, 1.0 - corner.y); // row 0 is the top: the texture is drawn rows upright
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    internal const string Prelude = """
        #version 330 core
        in vec2 uv;
        out vec4 color;
        uniform float time;
        uniform float fade;
        uniform float page;
        uniform vec2 resolution;
        uniform vec3 camera;
        uniform vec4 accent;
        uniform sampler2D scene;
        uniform vec4 panels[8];
        uniform int panelCount;
        uniform float veil;
        uniform sampler2D worldMap;
        uniform float worldKnown;
        uniform vec3 worldTints[5];
        uniform vec4 worldPlace;
        uniform vec4 worldWeather;
        uniform float planet;
        uniform vec4 worldScale;
        uniform vec4 worldChunk;
        uniform float worldYaw;
        vec3 world(vec2 at) { return texture(scene, vec2(at.x, 1.0 - at.y)).rgb; }
        vec4 worldAt(vec2 at) { return worldKnown > 0.5 ? texture(worldMap, at) : vec4(0.0); }
        vec3 worldTint(int kind, vec3 own) { return worldKnown > 0.5 && worldTints[kind].r >= 0.0 ? worldTints[kind] : own; }
        #define main styleMain
        #line 1
        """;

    // After the style's main: the scene darkened behind the panels (veil), soft at their edges, so text stays readable
    // over a bright background and the rest keeps its colour
    internal const string Epilogue = """

        #undef main
        void main()
        {
            styleMain();
            vec2 px = vec2(uv.x, 1.0 - uv.y) * resolution;
            float feather = resolution.y * 0.03, cover = 0.0;
            for (int i = 0; i < 8; i++)
            {
                if (i >= panelCount) break;
                vec2 d = max(panels[i].xy - px, px - (panels[i].xy + panels[i].zw));
                cover = max(cover, 1.0 - smoothstep(-feather, feather, max(d.x, d.y)));
            }
            vec3 grey = vec3(dot(color.rgb, vec3(0.299, 0.587, 0.114)));
            color.rgb = mix(color.rgb, grey, 0.45 * veil * cover) * (1.0 - veil * cover); // frosted: greyer and darker
        }
        """;

    private readonly Vec4f _tint = new(1, 1, 1, 1);
    private readonly int[] _viewport = new int[4];
    private int _program, _vao, _texture, _framebuffer;
    private int _time, _fade, _page, _resolution, _camera, _accent, _scene, _panels, _panelCount, _veil, _worldMap, _worldKnown, _worldTints, _worldPlace, _worldWeather, _planet, _worldScale, _worldChunk, _worldYaw;
    private string? _source; // the source the program was built from
    private (int Width, int Height) _size;
    private (int Width, int Height) _failed; // a frame size whose target was incomplete: not made and dropped each frame

    // false when this source does not run; the caller draws the blur instead
    public bool Draw(string source, float z, double fade, int blurred, in SceneState state)
    {
        if (!Finite(fade) || !Assert(source.Length <= MaxSource) || fade <= 0) return false;
        if (!ReferenceEquals(source, _source) && !Build(source)) return false;
        if (_program == 0) return false;
        int width = capi.Render.FrameWidth, height = capi.Render.FrameHeight;
        if (width <= 0 || height <= 0 || !Assert(width <= MaxSize && height <= MaxSize)) return false;
        if (_size != (width, height) && (_failed == (width, height) || !Target(width, height))) return false;
        Pass(width, height, (float)Math.Min(1, fade), blurred, state);
        capi.Render.Render2DTexture(_texture, 0, 0, width, height, z, _tint);
        return true;
    }

    private void Pass(int width, int height, float fade, int blurred, in SceneState s)
    {
        GL.GetInteger(GetPName.Viewport, _viewport);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFb);
        GL.GetInteger(GetPName.CurrentProgram, out int program);
        GL.GetInteger(GetPName.VertexArrayBinding, out int vao);
        GL.GetInteger(GetPName.ActiveTexture, out int unit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        var (blend, scissor) = (GL.IsEnabled(EnableCap.Blend), GL.IsEnabled(EnableCap.ScissorTest));
        var world = -1; // unit 1's texture before; -1: World did not run, unit 1 untouched
        try
        {
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
            GL.Viewport(0, 0, width, height);
            GL.UseProgram(_program);
            GL.BindVertexArray(_vao);
            GL.BindTexture(TextureTarget.Texture2D, blurred);
            GL.Uniform1(_scene, 0);
            GL.Uniform1(_time, s.Time);
            GL.Uniform1(_fade, fade);
            GL.Uniform1(_page, s.Page);
            GL.Uniform2(_resolution, (float)width, height);
            GL.Uniform3(_camera, s.X, s.Y, s.Zoom);
            GL.Uniform4(_accent, s.Accent.X, s.Accent.Y, s.Accent.Z, s.Accent.W);
            GL.Uniform4(_panels, SceneState.MaxPanels, s.Panels);
            GL.Uniform1(_panelCount, s.PanelCount);
            GL.Uniform1(_veil, s.Veil);
            world = World(s);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            if (world >= 0)
            {
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, world);
                GL.ActiveTexture(TextureUnit.Texture0);
            }

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
            GL.UseProgram(program);
            GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.ActiveTexture((TextureUnit)unit);
            if (blend) GL.Enable(EnableCap.Blend);
            if (scissor) GL.Enable(EnableCap.ScissorTest);
        }
    }

    // The world's uniforms and its map (unit 1); returns what that unit held before
    private int World(in SceneState s)
    {
        GL.Uniform1(_worldKnown, s.WorldMap != 0 ? 1f : 0f);
        GL.Uniform3(_worldTints, WorldGlobe.Kinds, s.WorldTints);
        GL.Uniform4(_worldPlace, s.Place.Latitude, s.Place.Hour, s.Place.Declination, s.Place.Known);
        GL.Uniform4(_worldWeather, s.Weather.Clouds, s.Weather.Rain, s.Weather.Temperature, s.Weather.Known);
        GL.Uniform1(_planet, s.Planet);
        GL.Uniform4(_worldScale, s.Scale.Radius, s.Scale.Reach, s.Scale.Dx, s.Scale.Dz);
        GL.Uniform4(_worldChunk, s.Scale.Cx, s.Scale.Cz, s.Scale.ChunkX, s.Scale.ChunkZ);
        GL.Uniform1(_worldYaw, s.Scale.Yaw);
        GL.Uniform1(_worldMap, 1);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.GetInteger(GetPName.TextureBinding2D, out int world);
        GL.BindTexture(TextureTarget.Texture2D, s.WorldMap);
        GL.ActiveTexture(TextureUnit.Texture0);
        return world;
    }

    // A failed source is remembered too: it is not compiled again each frame
    private bool Build(string source)
    {
        DropProgram();
        _source = source;
        var (vertex, fragment) = (Stage(ShaderType.VertexShader, Vertex), Stage(ShaderType.FragmentShader, Prelude + "\n" + source + Epilogue));
        if (vertex == 0 || fragment == 0)
        {
            if (vertex != 0) GL.DeleteShader(vertex);
            if (fragment != 0) GL.DeleteShader(fragment);
            return false;
        }

        _program = GL.CreateProgram();
        GL.AttachShader(_program, vertex);
        GL.AttachShader(_program, fragment);
        GL.LinkProgram(_program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            capi.Logger?.Warning("Komet styles: the background shader did not link: {0}", GL.GetProgramInfoLog(_program));
            DropProgram();
            _source = source;
            return false;
        }

        (_time, _fade, _page) = (Uniform("time"), Uniform("fade"), Uniform("page"));
        (_resolution, _camera, _accent, _scene) = (Uniform("resolution"), Uniform("camera"), Uniform("accent"), Uniform("scene"));
        (_panels, _panelCount, _veil) = (Uniform("panels"), Uniform("panelCount"), Uniform("veil"));
        (_worldMap, _worldKnown, _worldTints) = (Uniform("worldMap"), Uniform("worldKnown"), Uniform("worldTints"));
        (_worldPlace, _worldWeather, _planet) = (Uniform("worldPlace"), Uniform("worldWeather"), Uniform("planet"));
        (_worldScale, _worldChunk, _worldYaw) = (Uniform("worldScale"), Uniform("worldChunk"), Uniform("worldYaw"));
        _vao = GL.GenVertexArray();
        return true;
    }

    private int Uniform(string name) => GL.GetUniformLocation(_program, name);

    private int Stage(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled != 0) return shader;
        capi.Logger?.Warning("Komet styles: the background shader did not compile (line numbers are the file's): {0}",
            GL.GetShaderInfoLog(shader));
        GL.DeleteShader(shader);
        return 0;
    }

    private bool Target(int width, int height)
    {
        Release();
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFb);
        GL.GetInteger(GetPName.TextureBinding2D, out int bound);
        try
        {
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
                _texture, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete) Release();
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.BindTexture(TextureTarget.Texture2D, bound);
        }

        _size = _framebuffer != 0 ? (width, height) : default;
        if (_framebuffer == 0) _failed = (width, height);
        return _framebuffer != 0;
    }

    // The screen closed: the frame-sized texture goes; the program stays for the next opening, which tries a failed size again
    public void Release()
    {
        if (_framebuffer != 0) GL.DeleteFramebuffer(_framebuffer);
        if (_texture != 0) GL.DeleteTexture(_texture);
        (_framebuffer, _texture, _size, _failed) = (0, 0, default, default);
    }

    private void DropProgram()
    {
        if (_program != 0) GL.DeleteProgram(_program);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        (_program, _vao, _source) = (0, 0, null);
    }

    public void Dispose()
    {
        Release();
        DropProgram();
    }
}

// What a background shader is told each frame: seconds since the screen opened, the page as an eased index, the
// camera (x, y offsets and zoom, eased toward the page's), the style's accent colour; the panels the epilogue darkens
// behind by Veil (x, y, width, height in frame pixels from the top left, PanelCount of them); the world around the
// player (WorldGlobe: its texture, 0 when none, and tints)
internal readonly record struct SceneState(float Time, float Page, float X, float Y, float Zoom, Vec4f Accent, float[] Panels,
    int PanelCount, float Veil, int WorldMap, float[] WorldTints, (float Latitude, float Hour, float Declination, float Known) Place,
    float Planet, (float Clouds, float Rain, float Temperature, float Known) Weather,
    (float Radius, float Reach, float Dx, float Dz, float Cx, float Cz, float Yaw, float ChunkX, float ChunkZ) Scale)
{
    public const int MaxPanels = 8;
}

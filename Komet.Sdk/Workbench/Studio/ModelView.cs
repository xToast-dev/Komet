using System.Diagnostics;
using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// The preview's 3D view: the open shader drawn on a shape of its own (sphere, cube, plane, a screen-filling quad for
// post shaders) under an orbit camera, apart from the game. It links a program of its own from the editor's text, so
// it shows a shader the game is not drawing now and never touches the engine's program. What the shader asks for is
// read from the linked program: attributes by name (position, normal, uv from the shape, colors white, the rest
// zero), matrices from the camera, counters from a clock, samplers a test pattern (the frame for the screen quad),
// every other value copied from the game's program of the same name now and then (its last values: fog, light,
// tints), else a neutral default. A text that does not compile keeps the last good program and says why.
// Nothing exists while the view is off or the workbench closed.
internal sealed partial class ModelView : IDisposable
{
    internal const int Width = 640, ShapeCount = 5;
    private const int MaxInputs = 128;
    private const int SceneShape = 0, Screen = 4, CopyEvery = 30;
    private const float MinDistance = 1.4f, MaxDistance = 12;

    private readonly List<Input> _attributes = [], _uniforms = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly float[] _floats = new float[16];
    private readonly int[] _ints = new int[4];
    private int _frame, _copiedFrom;
    private string _log = "";
    private (string Name, string Vertex, string Fragment, bool FullscreenPass)? _pending;
    private float _yaw = -2.4f, _pitch = 0.26f, _distance = 6.1f;

    public bool On { get; set; }
    public int Shape { get; set; } // 0 the scene (the default), then sphere, cube, plane, screen quad
    public string Note { get; private set; } = ""; // why no picture shows ("" one does)
    public double LastMs { get; private set; }
    public bool Has => _program != 0;

    public bool SceneOnly => (_pending?.FullscreenPass ?? _fullscreenPass) ||
                             (_pending is null && (!Has || WeatherScene));

    private bool WeatherScene =>
        _family is FamilyCloudVolume or FamilyCloudMap or FamilyClassicCloud or FamilyAurora or FamilyCelestial or
            FamilyEffect;

    // The camera turns with a drag (pixels) and comes closer with the wheel
    public void Orbit(double dx, double dy)
    {
        _yaw += (float)dx * 0.01f;
        _pitch = Math.Clamp(_pitch + (float)dy * 0.01f, -1.5f, 1.5f);
    }

    public void Zoom(int lines) =>
        _distance = Math.Clamp(_distance * MathF.Pow(1.08f, Math.Clamp(lines, -30, 30)), MinDistance, MaxDistance);

    // The stages to link at the next picture (the GL context is the render thread's)
    public void Source(string name, string vertex, string fragment, bool fullscreenPass = false)
    {
        (_pending, Note) = ((name, vertex, fragment, fullscreenPass), "");
        if (fullscreenPass) Shape = SceneShape;
    }

    // No shader to show: the last program goes, the note says why
    public void Clear(string note)
    {
        (_pending, Note) = (null, note);
        _drop = true;
    }

    // A failed link's log, once
    public string TakeLog()
    {
        var log = _log;
        _log = "";
        return log;
    }

    // The view's texture this frame, 0 none. engine: the game's GL program of the same name (0 none), read only.
    public int Picture(ICoreClientAPI capi, int sharp, int engine)
    {
        var height = Math.Max(1, (int)((double)Width * capi.Render.FrameHeight / Math.Max(1, capi.Render.FrameWidth)));
        var clock = Stopwatch.StartNew();
        var saved = Save();
        try
        {
            if (_drop) DropProgram();

            if (_pending is { } pending)
            {
                _pending = null;
                Link(pending.Name, pending.Vertex, pending.Fragment, pending.FullscreenPass);
            }

            if (Note.Length > 0 || !Ready(height)) return 0;

            Shapes();
            Draw(sharp, engine, height);
        }
        finally
        {
            Restore(saved);
            LastMs = clock.Elapsed.TotalMilliseconds;
        }

        return _picture;
    }

    private void Draw(int sharp, int engine, int height)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        GL.Viewport(0, 0, Width, height);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.ColorMask(true, true, true, true);
        SceneBlend();
        GL.ClearColor(0.075f, 0.085f, 0.11f, 1);
        GL.ClearDepth(1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (SceneOnly || Shape == SceneShape)
        {
            DrawScene(engine, height);
        }
        else
        {
            var screen = Shape == Screen;
            var range = screen ? ScreenRange() : _ranges[Math.Clamp(Shape, 1, ShapeCount - 1)];
            _object = screen ? null : Vector3.Zero; // the shape is the movable object
            DrawUserPart(range, engine, Width / (float)height, height,
                screen && sharp != 0 ? Upright(sharp, height) : _pattern, screen);
            _object = null;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fb);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _pictureFb);
        GL.BlitFramebuffer(0, 0, Width, height, 0, height, Width, 0, ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest); // upside down: the screen draws the picture's first row on top
    }

    // Matrices by their names (projection, model-view, view, model; any other the identity), counters and times
    private void Camera(float aspect, bool screen, float worldScale)
    {
        var (view, projection) = Eye(aspect, screen);
        if (worldScale > 1f) view = Matrix4x4.CreateScale(1f / worldScale) * view;
        if (_family == FamilyAurora) view = Matrix4x4.CreateTranslation(0, AuroraAltitude, 0) * view;
        var seconds = (float)_clock.Elapsed.TotalSeconds;
        var model = _object is { } pivot ? ObjectMatrix(pivot) : Matrix4x4.Identity; // the movable object only
        for (var i = 0; i < Math.Min(_uniforms.Count, MaxInputs); i++)
        {
            var u = _uniforms[i];
            switch (u.Role)
            {
                case RoleProjection: Matrix(u.Location, projection); break;
                case RoleView: Matrix(u.Location, view); break;
                case RoleModelView: Matrix(u.Location, model * view); break;
                case RoleModel: Matrix(u.Location, model); break;
                case RoleIdentity: Matrix(u.Location, Matrix4x4.Identity); break;
                case RoleTime: GL.Uniform1(u.Location, u.Name == "auroraCounter" ? seconds * 0.2f : seconds); break;
            }
        }
    }

    // These uniforms refer to the test scene's targets and lighting. Live values use the game's camera and size.
    private void SceneUniforms()
    {
        if (_fullscreenPass) return;

        foreach (var uniform in _uniforms.Bounded(MaxInputs))
        {
            var location = uniform.Location;
            if ((_godrays && SunUniform(uniform.Name, location)) || GradeUniform(uniform.Name, location) ||
                ClimateUniform(uniform.Name, location)) continue;
            switch (uniform.Name, (ActiveUniformType)uniform.Type)
            {
                case ("frameSize" or "resolution", ActiveUniformType.FloatVec2):
                    GL.Uniform2(location, (float)Width, _height);
                    break;
                case ("frameSize" or "resolution", ActiveUniformType.IntVec2):
                    GL.Uniform2(location, Width, _height);
                    break;
                case ("invFrameSizeIn" or "invFrameSize", ActiveUniformType.FloatVec2):
                    GL.Uniform2(location, 1f / Width, 1f / _height);
                    break;
                case ("horizontalResolution", ActiveUniformType.Int):
                    GL.Uniform1(location, Width);
                    break;
                case ("blockTextureSize", ActiveUniformType.FloatVec2):
                    GL.Uniform2(location, 1f, 1f);
                    break;
                case ("textureAtlasSize", ActiveUniformType.FloatVec2):
                    GL.Uniform2(location, (float)AtlasW, AtlasH);
                    break;
                case ("sunPosition" or "lightPosition", ActiveUniformType.FloatVec3):
                    GL.Uniform3(location, 0.45f, 0.8f, 0.35f);
                    break;
                case ("rgbaAmbientIn", ActiveUniformType.FloatVec3):
                    GL.Uniform3(location, 0.75f, 0.77f, 0.80f);
                    break;
                case ("color", ActiveUniformType.FloatVec4) when _family == FamilyAurora:
                    GL.Uniform4(location, 1f, 1f, 1f, 0.85f);
                    break;
                case ("rgbaFogIn", ActiveUniformType.FloatVec4):
                    GL.Uniform4(location, 0.72f, 0.79f, 0.88f, 1f);
                    break;
                case ("fogMinIn" or "fogDensityIn" or "flatFogDensity", ActiveUniformType.Float):
                    GL.Uniform1(location, 0f);
                    break;
                case ("viewDistance" or "viewDistanceLod0", ActiveUniformType.Float):
                    GL.Uniform1(location, 80f);
                    break;
                case ("cloudBrightness" or "cloudOpaqueness" or "globalCloudBrightness" or "dayLight", ActiveUniformType
                    .Float):
                    GL.Uniform1(location, uniform.Name == "dayLight" && _linkedName == "nightsky" ? 0f : 1f);
                    break;
                case ("cloudsLength", ActiveUniformType.Float):
                    GL.Uniform1(location, 4000f);
                    break;
            }
        }
    }

    private void Matrix(int location, Matrix4x4 m)
    {
        // System.Numerics keeps row vectors in row order: the same sixteen floats OpenGL reads as column vectors
        (_floats[0], _floats[1], _floats[2], _floats[3]) = (m.M11, m.M12, m.M13, m.M14);
        (_floats[4], _floats[5], _floats[6], _floats[7]) = (m.M21, m.M22, m.M23, m.M24);
        (_floats[8], _floats[9], _floats[10], _floats[11]) = (m.M31, m.M32, m.M33, m.M34);
        (_floats[12], _floats[13], _floats[14], _floats[15]) = (m.M41, m.M42, m.M43, m.M44);
        GL.UniformMatrix4(location, 1, false, _floats);
    }

    // The game's last values of the plain uniforms (floats, vectors, ints), read from its program and set on the
    // view's; a value the game never set (all zero) leaves the view's default
    private void CopyFrom(int engine)
    {
        if (engine == 0 || engine == _program || !GL.IsProgram(engine)) return;

        for (var i = 0; i < Math.Min(_uniforms.Count, MaxInputs); i++)
        {
            var u = _uniforms[i];
            if (u.Role != RoleValue || Placed(u.Name.ToLowerInvariant()) ||
                GL.GetUniformLocation(engine, u.Name) is not (>= 0 and var from)) continue;

            var n = Components((ActiveUniformType)u.Type, out var integer);
            if (integer)
            {
                _ints[0] = 0;
                GL.GetUniform(engine, from, _ints);
                if (_ints[0] != 0) GL.Uniform1(u.Location, _ints[0]);

                continue;
            }

            Array.Clear(_floats, 0, 4);
            GL.GetUniform(engine, from, _floats);
            if (_floats.AsSpan(0, 4).IndexOfAnyExcept(0f) < 0) continue;

            switch (n)
            {
                case 1: GL.Uniform1(u.Location, _floats[0]); break;
                case 2: GL.Uniform2(u.Location, _floats[0], _floats[1]); break;
                case 3: GL.Uniform3(u.Location, _floats[0], _floats[1], _floats[2]); break;
                case 4: GL.Uniform4(u.Location, _floats[0], _floats[1], _floats[2], _floats[3]); break;
            }
        }
    }

    // 1..4 for float, vector, int and bool uniforms; 0 for what is not copied
    private static int Components(ActiveUniformType type, out bool integer)
    {
        integer = type is ActiveUniformType.Int or ActiveUniformType.Bool;
        return type switch
        {
            ActiveUniformType.Float or ActiveUniformType.Int or ActiveUniformType.Bool => 1,
            ActiveUniformType.FloatVec2 => 2,
            ActiveUniformType.FloatVec3 => 3,
            ActiveUniformType.FloatVec4 => 4,
            _ => 0
        };
    }

    private readonly record struct Input(string Name, int Location, int Type, int Role);
}

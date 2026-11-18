using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// The 3D view's scene: a small world of its own (a sky dome, a hill of blocks, a pond) drawn by a built-in program, the
// open shader put in for the part it belongs to by its family: sky shaders on the dome, liquid shaders on the pond,
// post shaders over the finished scene (its colour as their input), every other shader on the blocks. So a shader
// shows the way it would look in the game instead of on a lone shape fed with guesses.
internal sealed partial class ModelView
{
    private const int FamilyBlocks = 0, FamilySky = 1, FamilyLiquid = 2, FamilyPost = 3,
        FamilyCloudVolume = 4, FamilyAurora = 5, FamilyCloudMap = 6, FamilyClassicCloud = 7, FamilyCelestial = 8,
        FamilyEffect = 9;
    private const int PartDome = 0, PartBlocks = 1, PartWater = 2, Parts = 3;
    private const float Block = 0.4f, DomeRadius = 24;
    private const int Grid = 11;

    private static readonly string[] PostNames =
        ["final", "blur", "bloom", "godray", "colorgrade", "luma", "ssao", "blit", "autocamera", "tonemap", "fxaa",
            "compose", "composite", "findbright", "texture2texture", "post", "chromatic", "vignette", "sharpen", "dof"];
    private static readonly string[] SkyNames = ["sky", "celestial", "aurora", "cloud", "sun", "moon", "star"];
    private static readonly string[] LiquidNames = ["liquid", "water"];

    // Effects the game draws on one quad somewhere in the world, reading the scene behind (its colour and depth): a
    // single portal standing in the scene, not every block of it
    private static readonly string[] EffectNames = ["rift", "galaxy", "portal", "temporal"];

    // Values that place or size things in the game's world (block origins, the player, wind offsets, the view's
    // depth range): copied into a scene a few units wide they move or fog it away, so the view keeps its own
    private static readonly string[] Placing =
        ["origin", "offset", "playerpos", "viewdistance", "zfar", "znear", "framesize", "camera", "world"];

    private readonly (int First, int Count)[] _parts = new (int, int)[Parts];
    private int _builtin, _builtinVao, _blocks, _water, _family, _skyGradient, _sunGlow, _stars;
    private int _uProj = -1, _uView = -1, _uKind = -1, _uTex = -1, _uSun = -1, _uDaylight = -1;

    // The family by the program's name; a vertex stage without a position input draws a screen quad: a post shader
    private static int Family(string name)
    {
        var n = name.ToLowerInvariant();
        switch (n)
        {
            case "cloudvolumetric": return FamilyCloudVolume;
            case "cloudmap": return FamilyCloudMap;
            case "clouds": return FamilyClassicCloud;
            case "aurora": return FamilyAurora;
            case "celestialobject": return FamilyCelestial;
        }

        if (Array.Exists(EffectNames, n.Contains)) return FamilyEffect;

        if (Array.Exists(PostNames, n.Contains)) return FamilyPost;

        if (Array.Exists(SkyNames, n.Contains)) return FamilySky;

        return Array.Exists(LiquidNames, n.Contains) ? FamilyLiquid : FamilyBlocks;
    }

    private void FamilyFromInputs()
    {
        if (_family == FamilyBlocks && !_attributes.Exists(a => a.Role == AttrPosition)) _family = FamilyPost;
    }

    private static bool Placed(string name) => Array.Exists(Placing, name.Contains);

    private void DrawScene(int engine, int height)
    {
        var aspect = Width / (float)height;
        var (view, projection) = Eye(aspect, false);
        var user = Has ? _family : -1;

        // the dome first, without depth: everything stands in front of it
        GL.DepthMask(false);
        if (user == FamilySky)
        {
            DrawUserPart(_parts[PartDome], engine, aspect, height, _blocks);
        }
        else
        {
            Builtin(PartDome, user == FamilyAurora ? 3 : 2, view, projection);
        }

        GL.DepthMask(true);
        if (user == FamilyBlocks)
        {
            DrawUserPart(_parts[PartBlocks], engine, aspect, height, _blocks);
        }
        else
        {
            Builtin(PartBlocks, 0, view, projection);
        }

        if (user == FamilyLiquid)
        {
            DrawUserPart(_parts[PartWater], engine, aspect, height, _water);
        }
        else
        {
            Builtin(PartWater, 1, view, projection);
        }

        DrawSceneEffect(user, engine, aspect, height, view, projection);
    }

    private void DrawSceneEffect(int user, int engine, float aspect, int height, Matrix4x4 view, Matrix4x4 projection)
    {
        switch (user)
        {
            case FamilyCloudVolume:
                DrawCloudVolume(_program, view, projection, height, false);
                break;
            case FamilyCloudMap:
                if (DrawWeatherMap(view, height))
                {
                    DrawCloudVolume(_companion, view, projection, height, true);
                }

                break;
            case FamilyAurora:
                DrawUserPart(_ribbon, engine, aspect, height, _blocks, worldScale: AuroraUnits);
                break;
            case FamilyClassicCloud:
                DrawUserPart(_cloudTiles, engine, aspect, height, _blocks, worldScale: WorldUnits);
                break;
            case FamilyEffect:
                CopySceneTargets(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, height);
                _object = new Vector3(0, Block, 0); // the portal moves, stands on its foot
                DrawUserPart(_effect, engine, aspect, height, _scene);
                _object = null;
                break;
            case FamilyCelestial:
                DrawUserPart(_celestial, engine, aspect, height, _sunDisk);
                break;
            case FamilyPost:
                DrawScenePost(engine, aspect, height);
                break;
        }
    }

    private void CopySceneTargets(ClearBufferMask mask, int height)
    {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fb);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFb);
        GL.BlitFramebuffer(0, 0, Width, height, 0, 0, Width, height, mask,
            BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
    }

    private void DrawScenePost(int engine, float aspect, int height)
    {
        CopySceneTargets(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, height);
        if (!_fullscreenPass && !_godrays) GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.DepthTest);
        if (_godrays)
        {
            // the game adds the rays' target onto the frame later: the view adds them onto the scene
            GL.Enable(IndexedEnableCap.Blend, 0);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        }
        else
        {
            GL.Disable(IndexedEnableCap.Blend, 0); // a post shader's alpha is no coverage
        }
        var range = ScreenRange();
        DrawUserPart(range, engine, aspect, height, _scene, true);
    }

    // The engine's generated screen triangle expects vertex IDs 0 through 2.
    private (int First, int Count) ScreenRange() => _screenVertexIds && _attributes.Count == 0
        ? (0, 3)
        : _ranges[Screen];

    private void DrawUserPart((int First, int Count) range, int engine, float aspect, int height, int texture,
        bool screen = false, float worldScale = 1f)
    {
        if (_usesOit)
        {
            if (!screen) CopySceneTargets(ClearBufferMask.DepthBufferBit, height);

            if (!BeginTransparency(height)) return;
        }

        UserPart(range, engine, aspect, texture, screen, worldScale);
        if (_usesOit) CompositeTransparency();
    }

    // The open shader on one part of the scene (or the screen quad)
    private void UserPart((int First, int Count) range, int engine, float aspect, int texture, bool screen = false,
        float worldScale = 1f)
    {
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        Textures(texture, screen || _usesOit || _family == FamilyEffect ? _sceneDepth : 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _usesOit ? _oitFb : _fb);
        if (engine != _copiedFrom || _frame++ % CopyEvery == 0)
        {
            _copiedFrom = engine;
            CopyFrom(engine);
        }

        Camera(aspect, screen, worldScale);
        RefreshClimate();
        SceneUniforms();
        if (_fullscreenPass) GL.Uniform2(_passResolution, (float)Width, _height);
        Constants(true);
        GL.DrawArrays(PrimitiveType.Triangles, range.First, range.Count);
        Constants(false);
    }

    private void Builtin(int part, int kind, Matrix4x4 view, Matrix4x4 projection)
    {
        if (_builtin == 0) return;

        GL.UseProgram(_builtin);
        GL.BindVertexArray(_builtinVao);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _blocks);
        GL.BindSampler(0, 0);
        Matrix(_uProj, projection);
        Matrix(_uView, view);
        GL.Uniform1(_uKind, kind);
        GL.Uniform1(_uTex, 0);
        GL.Uniform3(_uSun, 0.45f, 0.8f, 0.35f);
        GL.Uniform1(_uDaylight, _family == FamilyAurora && Has ? 0.24f : 1f);
        var (first, count) = _parts[part];
        GL.DrawArrays(PrimitiveType.Triangles, first, count);
    }

    // The orbit camera's view and projection (the identity for the screen quad)
    private (Matrix4x4 View, Matrix4x4 Projection) Eye(float aspect, bool screen)
    {
        if (screen) return (Matrix4x4.Identity, Matrix4x4.Identity);

        var eye = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch)) *
                  _distance;
        var target = SceneOnly || Shape == SceneShape ? new Vector3(0, Block, 0) : Vector3.Zero;
        const float near = 0.05f, far = 80f;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.95f, Math.Max(0.1f, aspect), near, far);
        projection.M33 = -(far + near) / (far - near); // OpenGL clip depth is -1..1
        projection.M43 = -2f * far * near / (far - near);
        return (Matrix4x4.CreateLookAt(eye + target, target, Vector3.UnitY), projection);
    }

    // Once with the shapes: the scene's parts in the same buffer, the built-in program and its arrays, the block texture
    private void SceneParts(List<float> v)
    {
        var first = v.Count / 8;
        Dome(v);
        _parts[PartDome] = (first, v.Count / 8 - first);
        first = v.Count / 8;
        Hill(v);
        _parts[PartBlocks] = (first, v.Count / 8 - first);
        first = v.Count / 8;
        var (w, y) = (Grid / 2f * Block, 0.8f * Block);
        foreach (var (s, t) in Corners.Bounded(6))
        {
            Vertex(v, -w + s * Block * 4, y, -w + t * Block * 4, 0, 1, 0, s * 4, t * 4); // the pond in the hill's corner
        }

        _parts[PartWater] = (first, v.Count / 8 - first);
        MakeAuroraRibbon(v);
        MakeCloudTiles(v);
        MakeCelestial(v);
        MakeEffect(v);
    }

    private static readonly (float S, float T)[] Corners = [(0, 0), (1, 0), (1, 1), (0, 0), (1, 1), (0, 1)];

    private static void Dome(List<float> v)
    {
        const int around = 24, up = 12;
        for (var i = 0; i < around * up; i++)
        {
            var (a, b) = (i % around, i / around);
            foreach (var (cs, ct) in Corners.Bounded(6))
            {
                var (s, t) = ((a + cs) / around, (b + ct) / up);
                var (phi, theta) = (s * MathF.Tau, t * MathF.PI);
                var (x, y, z) = (MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Cos(phi));
                Vertex(v, x * DomeRadius, y * DomeRadius, z * DomeRadius, -x, -y, -z, s, t); // seen from inside
            }
        }
    }

    // Column heights in blocks: a hill falling to a pond (height 0) in one corner
    private static int Height(int x, int z)
    {
        if (x < 0 || z < 0 || x >= Grid || z >= Grid) return -1;

        if (x < 4 && z < 4) return 0;

        var (dx, dz) = (x - 7, z - 7);
        return Math.Clamp(4 - (int)MathF.Sqrt((float)dx * dx + (float)dz * dz) + ((x * 7 + z * 3) % 5 == 0 ? 1 : 0), 1, 5);
    }

    // The hill as block faces: every top, and the sides that stand above their neighbour
    private static void Hill(List<float> v)
    {
        var half = Grid / 2f * Block;
        for (var x = 0; x < Grid; x++)
        {
            for (var z = 0; z < Grid; z++)
            {
                var h = Height(x, z);
                var (x0, z0) = (x * Block - half, z * Block - half);
                Face(v, x0, h * Block - Block, z0, 1, 2); // the top
                ReadOnlySpan<(int Dx, int Dz, int Axis, int Sign)> sides = [(1, 0, 0, 1), (-1, 0, 0, -1), (0, 1, 2, 1),
                    (0, -1, 2, -1)];
                foreach (var (dx, dz, axis, sign) in sides.ToArray().Bounded(4))
                {
                    var below = Math.Max(Height(x + dx, z + dz), -1);
                    for (var level = 0; level <= Math.Min(h, 5); level++)
                    {
                        if (level > below) Face(v, x0, level * Block - Block, z0, axis, sign);
                    }
                }
            }
        }
    }

    // One block face of the cell at (x, y, z) (its low corner): axis 0 x, 1 y up, 2 z; sign the side (y: 2 the top)
    private static void Face(List<float> v, float x, float y, float z, int axis, int sign)
    {
        foreach (var (s, t) in Corners.Bounded(6))
        {
            var (px, py, pz, nx, ny, nz) = axis switch
            {
                1 => (x + s * Block, y + Block, z + Block - t * Block, 0f, 1f, 0f),
                0 => (sign > 0 ? x + Block : x, y + t * Block, sign > 0 ? z + Block - s * Block : z + s * Block,
                    sign, 0f, 0f),
                _ => (sign > 0 ? x + s * Block : x + Block - s * Block, y + t * Block, sign > 0 ? z + Block : z, 0f, 0f,
                    sign)
            };
            Vertex(v, px, py, pz, nx, ny, nz, s * 0.5f, 1 - t); // the block: the atlas' left half
        }
    }

    private const string BuiltinVertex = """
        #version 330 core
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec3 normal;
        layout(location = 2) in vec2 uv;
        uniform mat4 uProj;
        uniform mat4 uView;
        out vec3 vNormal;
        out vec2 vUv;
        out vec3 vPos;
        void main() {
            vNormal = normal; vUv = uv; vPos = position;
            gl_Position = uProj * uView * vec4(position, 1.0);
        }
        """;

    private const string BuiltinFragment = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        in vec3 vPos;
        uniform sampler2D uTex;
        uniform int uKind;
        uniform vec3 uSun;
        uniform float uDaylight;
        out vec4 outColor;
        vec3 sky(vec3 d) {
            float h = clamp(d.y, -0.2, 1.0);
            vec3 c = mix(vec3(0.78, 0.86, 0.95), vec3(0.32, 0.52, 0.86), smoothstep(0.0, 0.7, h));
            float sun = max(dot(normalize(d), normalize(uSun)), 0.0);
            return c + vec3(1.0, 0.9, 0.7) * (pow(sun, 400.0) * 4.0 + pow(sun, 8.0) * 0.25);
        }
        void main() {
            if (uKind == 2) { outColor = vec4(sky(normalize(vPos)), 1.0); return; }
            if (uKind == 3) { outColor = vec4(sky(normalize(vPos)) * vec3(0.07, 0.08, 0.13), 1.0); return; }
            vec3 n = normalize(vNormal);
            float light = (0.45 + 0.6 * max(dot(n, normalize(uSun)), 0.0)) * uDaylight;
            if (uKind == 1) {
                float f = 0.25 + 0.5 * pow(1.0 - abs(n.y), 2.0);
                outColor = vec4(mix(vec3(0.12, 0.32, 0.55), sky(reflect(vec3(0.0, -1.0, 0.0), n)), f) * light, 0.75);
                return;
            }
            float grain = texture(uTex, vUv).r;
            vec3 base = n.y > 0.5 ? vec3(0.36, 0.58, 0.22) : vec3(0.45, 0.32, 0.2);
            if (n.y <= 0.5 && vUv.y < 0.2 && vPos.y > -0.01) base = mix(base, vec3(0.36, 0.58, 0.22), 0.6);
            outColor = vec4(base * (0.7 + 0.6 * grain) * light, 1.0);
        }
        """;

    private void BuiltinProgram()
    {
        if (_builtin != 0) return;

        var (v, f) = (Stage(ShaderType.VertexShader, BuiltinVertex), Stage(ShaderType.FragmentShader, BuiltinFragment));
        if (v != 0 && f != 0)
        {
            _builtin = GL.CreateProgram();
            GL.AttachShader(_builtin, v);
            GL.AttachShader(_builtin, f);
            GL.LinkProgram(_builtin);
            GL.GetProgram(_builtin, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0)
            {
                GL.DeleteProgram(_builtin);
                _builtin = 0;
            }
        }

        GL.DeleteShader(v);
        GL.DeleteShader(f);
        _log = ""; // the built-in program's own trouble is no message about the user's shader
        if (_builtin == 0) return;

        (_uProj, _uView, _uKind, _uTex, _uSun) = (GL.GetUniformLocation(_builtin, "uProj"),
            GL.GetUniformLocation(_builtin, "uView"), GL.GetUniformLocation(_builtin, "uKind"),
            GL.GetUniformLocation(_builtin, "uTex"), GL.GetUniformLocation(_builtin, "uSun"));
        _uDaylight = GL.GetUniformLocation(_builtin, "uDaylight");
        _builtinVao = GL.GenVertexArray();
        GL.BindVertexArray(_builtinVao);
        GL.BindBuffer(OpenTK.Graphics.OpenGL.BufferTarget.ArrayBuffer, _vbo);
        for (var location = 0; location < 3; location++)
        {
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, location == 2 ? 2 : 3, VertexAttribPointerType.Float, false, Stride,
                location * 3 * sizeof(float));
        }
    }

    // A block texture: 16 texels a side, each a grey of its own (stone- and soil-like grain under any tint)
    private static int BlockTexture(bool water = false)
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        var seed = 0x2545F491u;
        for (var i = 0; i < size * size; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            var g = (byte)(150 + seed % 90);
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = water
                ? ((byte)(g / 4), (byte)(g / 2), g, (byte)91)
                : (g, g, g, (byte)255);
        }

        var texture = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return texture;
    }

    private void DisposeScene()
    {
        if (_builtin != 0) GL.DeleteProgram(_builtin);

        if (_builtinVao != 0) GL.DeleteVertexArray(_builtinVao);

        if (_blocks != 0) GL.DeleteTexture(_blocks);

        if (_water != 0) GL.DeleteTexture(_water);

        if (_skyGradient != 0) GL.DeleteTexture(_skyGradient);

        if (_sunGlow != 0) GL.DeleteTexture(_sunGlow);

        if (_stars != 0) GL.DeleteTexture(_stars);

        if (_sunDisk != 0) GL.DeleteTexture(_sunDisk);

        (_builtin, _builtinVao, _blocks, _water) = (0, 0, 0, 0);
        (_skyGradient, _sunGlow, _stars) = (0, 0, 0);
        _sunDisk = 0;
    }
}

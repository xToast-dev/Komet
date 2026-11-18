using OpenTK.Graphics.OpenGL;
using GlBuffer = OpenTK.Graphics.OpenGL.BufferTarget;

namespace Komet.Sdk;

// The 3D view's GL: its program and what the program asks for, the shapes in one buffer, the two targets (drawn with
// depth, then turned upright into the picture), the test textures, and the state saved around its draw
internal sealed partial class ModelView
{
    private const int RoleValue = 0, RoleProjection = 1, RoleView = 2, RoleModelView = 3, RoleModel = 4,
        RoleIdentity = 5, RoleTime = 6, RoleSkip = 7;
    private const int AttrPosition = 0, AttrNormal = 1, AttrUv = 2, AttrWhite = 3, AttrZero = 4, AttrInt = 5;
    private const int AttrRibbonX = 6, AttrRenderFlags = 7, AttrTint = 8, AttrMatrix = 9;
    private const int Stride = 8 * sizeof(float), Units = 8, StarUnit = 7;
    private static readonly string[] Lit = ["color", "rgba", "alpha", "tint", "light", "bright", "opacity", "scale", "size"];

    private readonly record struct Saved(int Draw, int Read, int Program, int Vao, int Buffer, int Unit,
        bool Scissor, bool Depth, bool Cull, bool DepthMask, int DepthFunc);

    private readonly (int First, int Count)[] _ranges = new (int, int)[ShapeCount];
    private readonly int[] _viewport = new int[4];
    private readonly float[] _clear = new float[4];
    private readonly bool[] _mask = new bool[4];
    private readonly int[] _savedTextures = new int[Units], _savedSamplers = new int[Units];
    private int _savedCube;
    private string? _linkedName;
    private int _program, _vao, _vbo, _fb, _color, _depth, _picture, _pictureFb, _pattern, _white, _shadow, _height;
    private int _scene, _sceneDepth, _sceneFb, _readFb;
    private int _flags, _black, _tints;
    private float _clearDepth = 1;
    private bool _drop, _fullscreenPass, _screenVertexIds;
    private int _passResolution = -1;

    private void Link(string name, string vertex, string fragment, bool fullscreenPass)
    {
        var program = PreviewProgram(vertex, fragment);
        if (program == 0)
        {
            if (_linkedName != name) DropProgram();

            Note = _program == 0 ? FirstLine(_log) : ""; // the last good program keeps drawing
            return;
        }

        DropProgram();
        (_program, Note, _log, _copiedFrom) = (program, "", "", -1);
        _fullscreenPass = fullscreenPass;
        _linkedName = name;
        _screenVertexIds = vertex.Contains("gl_VertexID", StringComparison.Ordinal);
        _family = fullscreenPass ? FamilyPost : Family(name);
        _usesOit = GL.GetFragDataLocation(program, "OITaccumulation0") >= 0;
        _passResolution = fullscreenPass ? GL.GetUniformLocation(program, "resolution") : -1;
        Shapes();
        Reflect();
        FamilyFromInputs();
    }

    private int Stage(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled != 0) return shader;

        _log = (type == ShaderType.VertexShader ? "vertex: " : "fragment: ") + GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        return 0;
    }

    private static string FirstLine(string log)
    {
        var end = log.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? log : log[..end]).Trim();
    }

    private void DropProgram()
    {
        if (_program != 0) GL.DeleteProgram(_program);

        (_program, _drop, _fullscreenPass, _usesOit) = (0, false, false, false);
        _linkedName = null;
        _attributes.Clear();
        _uniforms.Clear();
        _godrays = false;
    }

    private const int AnimationBinding = 9;
    private int _animation;

    // Animated entities and the GUI read their joints from the "Animation" block: identity joints, else every vertex
    // lands on the origin (the shader test showed them empty)
    private void AnimationBlock()
    {
        var block = GL.GetUniformBlockIndex(_program, "Animation");
        if (block < 0) return;

        GL.GetActiveUniformBlock(_program, block, ActiveUniformBlockParameter.UniformBlockDataSize, out var bytes);
        var floats = new float[Math.Clamp(bytes, 64, 1 << 20) / sizeof(float)];
        for (var i = 0; i < Math.Min(floats.Length, 1 << 18); i++) floats[i] = i % 16 % 5 == 0 ? 1f : 0f;

        if (_animation == 0) _animation = GL.GenBuffer();

        GL.BindBuffer(GlBuffer.UniformBuffer, _animation);
        GL.BufferData(GlBuffer.UniformBuffer, floats.Length * sizeof(float), floats, BufferUsageHint.StaticDraw);
        GL.BindBuffer(GlBuffer.UniformBuffer, 0);
        GL.UniformBlockBinding(_program, block, AnimationBinding);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, AnimationBinding, _animation);
    }

    // What the linked program reads: each attribute's and uniform's part by its name and type, the shape's arrays
    // bound, samplers on their units, defaults set
    private void Reflect()
    {
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(GlBuffer.ArrayBuffer, _vbo);
        for (var location = 0; location < 16; location++)
        {
            GL.DisableVertexAttribArray(location); // the last program's arrays
        }

        UsesClimate = false;
        GL.GetProgram(_program, GetProgramParameterName.ActiveAttributes, out var attributes);
        for (var i = 0; i < Math.Min(attributes, MaxInputs); i++)
        {
            GL.GetActiveAttrib(_program, i, 256, out _, out _, out ActiveAttribType type, out string name);
            var location = GL.GetAttribLocation(_program, name);
            if (location < 0)
            {
                continue; // gl_VertexID and the like
            }

            var role = AttributeRole(name.ToLowerInvariant(), type);
            _attributes.Add(new Input(name, location, (int)type, role));
            if (OwnBuffer(role, location, type)) continue;

            if (role > AttrUv && role != AttrRibbonX)
            {
                GL.DisableVertexAttribArray(location);
                continue;
            }

            GL.EnableVertexAttribArray(location);
            var offset = role == AttrRibbonX ? 3 : role * 3;
            var components = role switch { AttrRibbonX => 1, AttrUv => 2, _ => 3 };
            GL.VertexAttribPointer(location, components, VertexAttribPointerType.Float, false, Stride,
                offset * sizeof(float));
        }

        AnimationBlock();
        GL.GetProgram(_program, GetProgramParameterName.ActiveUniforms, out var uniforms);
        for (var i = 0; i < Math.Min(uniforms, MaxInputs); i++)
        {
            GL.GetActiveUniform(_program, i, 256, out _, out _, out ActiveUniformType type, out string name);
            var location = GL.GetUniformLocation(_program, name);
            if (location >= 0)
            {
                _uniforms.Add(new Input(name, location, (int)type, UniformRole(location, name.ToLowerInvariant(), type)));
                _godrays |= name == "sunPosScreenIn";
                UsesClimate |= name is "seasonRel" or "colorMapRects[0]";
            }
        }
    }

    // The attributes with a buffer of their own: the tints (a vertex colour or light: the hill's grass and dirt as the
    // game's climate tint and block light colour them) and the render flags
    private bool OwnBuffer(int role, int location, ActiveAttribType type)
    {
        if (role is not (AttrTint or AttrRenderFlags or AttrColormap)) return false;

        UsesClimate |= role == AttrColormap;
        GL.EnableVertexAttribArray(location);
        GL.BindBuffer(GlBuffer.ArrayBuffer, role switch { AttrTint => _tints, AttrColormap => _colormap, _ => _flags });
        if (role == AttrTint)
        {
            GL.VertexAttribPointer(location, type == ActiveAttribType.FloatVec3 ? 3 : 4, VertexAttribPointerType.Float,
                false, 4 * sizeof(float), IntPtr.Zero);
        }
        else
        {
            GL.VertexAttribIPointer(location, 1, VertexAttribIntegerType.Int, sizeof(int), IntPtr.Zero);
        }

        GL.BindBuffer(GlBuffer.ArrayBuffer, _vbo);
        return true;
    }

    private static int AttributeRole(string name, ActiveAttribType type)
    {
        if (type == ActiveAttribType.Int && name == "colormapdata") return AttrColormap;

        if (type == ActiveAttribType.Int && name.StartsWith("renderflags", StringComparison.Ordinal))
        {
            return AttrRenderFlags;
        }

        if (type == ActiveAttribType.FloatMat4) return AttrMatrix; // an instance's transform: the identity

        if (type is not (ActiveAttribType.Float or ActiveAttribType.FloatVec2 or ActiveAttribType.FloatVec3
            or ActiveAttribType.FloatVec4))
        {
            return type is ActiveAttribType.Int or ActiveAttribType.IntVec2 or ActiveAttribType.IntVec3
                or ActiveAttribType.IntVec4 or ActiveAttribType.UnsignedInt ? AttrInt : AttrZero;
        }

        if (name.Contains("uv", StringComparison.Ordinal) || name.Contains("texcoord", StringComparison.Ordinal))
        {
            return AttrUv;
        }

        if (name.Contains("normal", StringComparison.Ordinal)) return AttrNormal;

        if (name == "xposin" && type == ActiveAttribType.Float) return AttrRibbonX;

        if (name is "xyz" or "p" || name.Contains("pos", StringComparison.Ordinal) ||
            name.Contains("vertex", StringComparison.Ordinal))
        {
            return AttrPosition;
        }

        if (!Array.Exists(Lit, name.Contains)) return AttrZero;

        return type is ActiveAttribType.FloatVec3 or ActiveAttribType.FloatVec4 &&
               (name.Contains("rgba", StringComparison.Ordinal) || name.Contains("color", StringComparison.Ordinal)) &&
               !name.Contains("fog", StringComparison.Ordinal) && !name.Contains("glow", StringComparison.Ordinal)
            ? AttrTint
            : AttrWhite;
    }

    // A uniform's part; samplers get their unit and plain values their default here, once
    private static int UniformRole(int location, string name, ActiveUniformType type)
    {
        if (type == ActiveUniformType.FloatMat4) return MatrixRole(name);

        if (SamplerUnit(name, type) is >= 0 and var unit)
        {
            GL.Uniform1(location, unit);
            return RoleSkip;
        }

        var n = Components(type, out var integer);
        if (n == 0) return RoleSkip;

        if (!integer && n == 1 && (name.Contains("counter", StringComparison.Ordinal) ||
                                   name.Contains("time", StringComparison.Ordinal)))
        {
            return RoleTime;
        }

        if (!integer && Array.Exists(Lit, name.Contains))
        {
            switch (n)
            {
                case 1: GL.Uniform1(location, 1f); break;
                case 2: GL.Uniform2(location, 1f, 1f); break;
                case 3: GL.Uniform3(location, 1f, 1f, 1f); break;
                default: GL.Uniform4(location, 1f, 1f, 1f, 1f); break;
            }
        }

        return RoleValue;
    }

    private static int MatrixRole(string name)
    {
        if (name.Contains("proj", StringComparison.Ordinal)) return RoleProjection;

        if (name.Contains("modelview", StringComparison.Ordinal)) return RoleModelView;

        if (name.Contains("view", StringComparison.Ordinal)) return RoleView;

        return name.Contains("model", StringComparison.Ordinal) ? RoleModel : RoleIdentity;
    }

    // A sampler's texture unit, -1 for what is no sampler: the pattern on 0, white on 1 (depth and shadow lookups),
    // the far depth on 2, every other sampler type on a unit of its own (two types on one unit draw nothing)
    private static int SamplerUnit(string name, ActiveUniformType type) =>
        type switch
        {
            ActiveUniformType.Sampler2D when name is "glowparts" or "bloomparts" or "godrayparts" => 3,
            ActiveUniformType.Sampler2D when name is "ssaoscene" or "revealage" => 4,
            ActiveUniformType.Sampler2D when name == "sky" => 5,
            ActiveUniformType.Sampler2D when name == "glow" => 6,
            ActiveUniformType.Sampler2D when name.Contains("depth", StringComparison.Ordinal) ||
                                             name.Contains("shadow", StringComparison.Ordinal) => 1,
            ActiveUniformType.Sampler2D => 0,
            ActiveUniformType.Sampler2DShadow => 2,
            ActiveUniformType.SamplerCube => StarUnit,
            ActiveUniformType.Sampler3D => 8,
            ActiveUniformType.Sampler2DArray => 9,
            _ when type.ToString().Contains("Sampler", StringComparison.Ordinal) => 10,
            _ => -1
        };

    // Attributes the shape has no array for: colors white, integers zero, while the view draws (they are context
    // state, so they go back to OpenGL's default after it)
    private void Constants(bool set)
    {
        if (set && _animation != 0) GL.BindBufferBase(BufferRangeTarget.UniformBuffer, AnimationBinding, _animation);

        for (var i = 0; i < Math.Min(_attributes.Count, MaxInputs); i++)
        {
            var a = _attributes[i];
            if (a.Role == AttrInt && set)
            {
                GL.VertexAttribI4(a.Location, 0, 0, 0, 0);
            }
            else if (a.Role == AttrMatrix)
            {
                for (var column = 0; column < 4; column++)
                {
                    GL.VertexAttrib4(a.Location + column, column == 0 ? 1f : 0f, column == 1 ? 1f : 0f,
                        column == 2 ? 1f : 0f, column == 3 || !set ? 1f : 0f);
                }
            }
            else if (a.Role is AttrWhite or AttrZero)
            {
                var one = set && a.Role == AttrWhite ? 1f : 0f;
                if (set && _family == FamilyClassicCloud)
                {
                    one = a.Name switch { "selfThickness" => 0.45f, "cloudOpaqueness" => 0.95f, _ => one };
                }

                GL.VertexAttrib4(a.Location, one, one, one, 1f);
            }
        }
    }

    private void Textures(int first, int depth = 0)
    {
        ReadOnlySpan<int> textures =
            [first, depth != 0 ? depth : _white, _shadow, _godrays ? _sunParts : _black, _white, _skyGradient, _sunGlow];
        for (var unit = 0; unit < StarUnit; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, textures[unit]);
            GL.BindSampler(unit, 0);
        }

        GL.ActiveTexture(TextureUnit.Texture0 + StarUnit);
        GL.BindTexture(TextureTarget.TextureCubeMap, _stars);
        GL.BindSampler(StarUnit, 0);
    }

    // The targets at the frame's shape, the shapes and the test textures; false: no complete framebuffer
    private bool Ready(int height)
    {
        if (_height == height && _fb != 0) return true;

        ReleaseTargets();
        (_color, _picture) = (Texture<byte>(Width, height, PixelInternalFormat.Rgba8, PixelFormat.Rgba, null),
            Texture<byte>(Width, height, PixelInternalFormat.Rgba8, PixelFormat.Rgba, null));
        _depth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, Width, height);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        (_fb, _pictureFb) = (GL.GenFramebuffer(), GL.GenFramebuffer());
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _color, 0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depth);
        var whole = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pictureFb);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _picture, 0);
        whole &= GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete;
        _scene = Texture<byte>(Width, height, PixelInternalFormat.Rgba8, PixelFormat.Rgba, null);
        _sceneDepth = Texture<float>(Width, height, PixelInternalFormat.DepthComponent24, PixelFormat.DepthComponent, null);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        (_sceneFb, _readFb) = (GL.GenFramebuffer(), GL.GenFramebuffer());
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFb);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _scene, 0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _sceneDepth, 0);
        whole &= GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete;
        _height = whole ? height : 0;
        return whole;
    }

    // The frame so far as OpenGL reads a texture (first row at the bottom): the screen keeps it first row on top, and
    // the view's picture is turned once at its end, so a post shader gets its input the way the game gives it
    private int Upright(int sharp, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, sharp);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
        if (w <= 0 || h <= 0) return _pattern;

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFb);
        GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, sharp, 0);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFb);
        GL.BlitFramebuffer(0, 0, w, h, 0, height, Width, 0, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, 0, 0);
        return _scene;
    }

    private static int Texture<T>(int width, int height, PixelInternalFormat format, PixelFormat pixels, T[]? data)
        where T : struct
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var type = pixels == PixelFormat.DepthComponent || typeof(T) == typeof(float)
            ? PixelType.Float
            : PixelType.UnsignedByte;
        if (data is null)
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, pixels, type, IntPtr.Zero);
        }
        else
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, pixels, type, data);
        }

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return texture;
    }

    // Once: the buffer with the four shapes, a checked test pattern (each cell tinted by its place, so a mapping
    // shows), a white texel and a far depth texel for shadow samplers (nothing in shadow)
    private void Shapes()
    {
        if (_vbo != 0) return;

        List<float> v = new(32 * 16 * 6 * 8 + 48 * 8);
        SceneParts(v);
        _ranges[1] = Sphere(v);
        _ranges[2] = Cube(v);
        _ranges[3] = Quad(v, false);
        _ranges[4] = Quad(v, true);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindBuffer(GlBuffer.ArrayBuffer, _vbo);
        var data = v.ToArray();
        GL.BufferData(GlBuffer.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        MakeFlags(data);

        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var (x, y) = (i % size, i / size);
            var light = ((x / 8) + (y / 8)) % 2 == 0 ? 210 : 120;
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) =
                ((byte)(light * (0.55 + 0.45 * x / size)), (byte)(light * (0.55 + 0.45 * y / size)), (byte)light, 255);
        }

        BuiltinProgram();
        _blocks = AtlasTexture();
        _water = BlockTexture(water: true);
        _pattern = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, pixels);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _white = Texture<byte>(1, 1, PixelInternalFormat.Rgba8, PixelFormat.Rgba, [255, 255, 255, 255]);
        _black = Texture<byte>(1, 1, PixelInternalFormat.Rgba8, PixelFormat.Rgba, [0, 0, 0, 255]);
        _sunParts = SunParts();
        _shadow = Texture(1, 1, PixelInternalFormat.DepthComponent24, PixelFormat.DepthComponent, [1f]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        SkyTextures();
    }

    private void MakeFlags(float[] vertices)
    {
        var flags = new int[vertices.Length / 8];
        for (var i = 0; i < Math.Min(flags.Length, 64 * 1024); i++)
        {
            var normal = i * 8 + 3;
            flags[i] = VertexFlags.PackNormal(vertices[normal], vertices[normal + 1], vertices[normal + 2]);
        }

        var tints = new float[flags.Length * 4];
        var (first, count) = _parts[PartBlocks];
        for (var i = 0; i < Math.Min(flags.Length, 64 * 1024); i++)
        {
            var up = vertices[i * 8 + 4] > 0.5f;
            var block = i >= first && i < first + count;
            (tints[i * 4], tints[i * 4 + 1], tints[i * 4 + 2], tints[i * 4 + 3]) = Tint(block, up);
        }

        _tints = GL.GenBuffer();
        GL.BindBuffer(GlBuffer.ArrayBuffer, _tints);
        GL.BufferData(GlBuffer.ArrayBuffer, tints.Length * sizeof(float), tints, BufferUsageHint.StaticDraw);
        _flags = GL.GenBuffer();
        GL.BindBuffer(GlBuffer.ArrayBuffer, _flags);
        GL.BufferData(GlBuffer.ArrayBuffer, flags.Length * sizeof(int), flags, BufferUsageHint.StaticDraw);
        GL.BindBuffer(GlBuffer.ArrayBuffer, _vbo);
        _vertices = vertices;
        ClimateBuffer(vertices);
    }

    private static (float, float, float, float) Tint(bool block, bool up)
    {
        if (!block) return (1f, 1f, 1f, 1f);

        return up ? (0.55f, 0.82f, 0.38f, 1f) : (0.72f, 0.54f, 0.4f, 1f); // grass on top, dirt on the sides
    }

    private static void Vertex(List<float> v, float x, float y, float z, float nx, float ny, float nz, float s, float t)
    {
        v.AddRange([x, y, z, nx, ny, nz, s, t]);
    }

    private static (int, int) Sphere(List<float> v)
    {
        const int around = 32, up = 16;
        var first = v.Count / 8;
        for (var i = 0; i < around * up; i++)
        {
            var (a, b) = (i % around, i / around);
            ReadOnlySpan<(int A, int B)> corners = [(0, 0), (1, 0), (1, 1), (0, 0), (1, 1), (0, 1)];
            for (var c = 0; c < 6; c++)
            {
                var (s, t) = ((a + corners[c].A) / (float)around, (b + corners[c].B) / (float)up);
                var (phi, theta) = (s * MathF.Tau, t * MathF.PI);
                var (x, y, z) = (MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Cos(phi));
                Vertex(v, x, y, z, x, y, z, s, t);
            }
        }

        return (first, v.Count / 8 - first);
    }

    private static (int, int) Cube(List<float> v)
    {
        var first = v.Count / 8;
        ReadOnlySpan<(int A, int B)> corners = [(0, 0), (1, 0), (1, 1), (0, 0), (1, 1), (0, 1)];
        for (var face = 0; face < 6; face++)
        {
            var (axis, sign) = (face / 2, face % 2 == 0 ? 1f : -1f);
            for (var c = 0; c < 6; c++)
            {
                var (s, t) = ((float)corners[c].A, (float)corners[c].B);
                Span<float> p = [0, 0, 0];
                Span<float> n = [0, 0, 0];
                (p[axis], n[axis]) = (sign * 0.8f, sign);
                p[(axis + 1) % 3] = (s * 2 - 1) * 0.8f;
                p[(axis + 2) % 3] = (t * 2 - 1) * 0.8f;
                Vertex(v, p[0], p[1], p[2], n[0], n[1], n[2], s, 1 - t);
            }
        }

        return (first, v.Count / 8 - first);
    }

    // The plane lies flat under the camera; the screen quad fills clip space (post shaders)
    private static (int, int) Quad(List<float> v, bool screen)
    {
        var first = v.Count / 8;
        ReadOnlySpan<(int A, int B)> corners = [(0, 0), (1, 0), (1, 1), (0, 0), (1, 1), (0, 1)];
        for (var c = 0; c < 6; c++)
        {
            var (s, t) = ((float)corners[c].A, (float)corners[c].B);
            if (screen)
            {
                Vertex(v, s * 2 - 1, t * 2 - 1, 0, 0, 0, 1, s, t);
            }
            else
            {
                Vertex(v, (s * 2 - 1) * 1.2f, 0, (t * 2 - 1) * 1.2f, 0, 1, 0, s, t);
            }
        }

        return (first, v.Count / 8 - first);
    }

    private Saved Save()
    {
        GL.GetInteger(GetPName.Viewport, _viewport);
        GL.GetFloat(GetPName.ColorClearValue, _clear);
        GL.GetFloat(GetPName.DepthClearValue, out _clearDepth);
        GL.GetBoolean(GetPName.ColorWritemask, _mask);
        GL.GetInteger(GetPName.ActiveTexture, out var unit);
        for (var i = 0; i < Units; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + i);
            GL.GetInteger(GetPName.TextureBinding2D, out _savedTextures[i]);
            GL.GetInteger(GetPName.SamplerBinding, out _savedSamplers[i]);
        }

        GL.GetInteger(GetPName.TextureBindingCubeMap, out _savedCube); // StarUnit is the last unit
        SavePreviewBlend();

        return new Saved(GL.GetInteger(GetPName.DrawFramebufferBinding), GL.GetInteger(GetPName.ReadFramebufferBinding),
            GL.GetInteger(GetPName.CurrentProgram), GL.GetInteger(GetPName.VertexArrayBinding),
            GL.GetInteger(GetPName.ArrayBufferBinding), unit, GL.IsEnabled(EnableCap.ScissorTest),
            GL.IsEnabled(EnableCap.DepthTest), GL.IsEnabled(EnableCap.CullFace), GL.GetBoolean(GetPName.DepthWritemask),
            GL.GetInteger(GetPName.DepthFunc));
    }

    private void Restore(Saved s)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, s.Draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, s.Read);
        GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
        GL.ClearColor(_clear[0], _clear[1], _clear[2], _clear[3]);
        GL.ClearDepth(_clearDepth);
        GL.ColorMask(_mask[0], _mask[1], _mask[2], _mask[3]);
        GL.UseProgram(s.Program);
        GL.BindVertexArray(s.Vao);
        GL.BindBuffer(GlBuffer.ArrayBuffer, s.Buffer);
        for (var i = 0; i < Units; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + i);
            GL.BindTexture(TextureTarget.Texture2D, _savedTextures[i]);
            GL.BindSampler(i, _savedSamplers[i]);
        }

        GL.BindTexture(TextureTarget.TextureCubeMap, _savedCube);

        GL.ActiveTexture((TextureUnit)s.Unit);
        RestorePreviewBlend();
        Toggle(EnableCap.ScissorTest, s.Scissor);
        Toggle(EnableCap.DepthTest, s.Depth);
        Toggle(EnableCap.CullFace, s.Cull);
        GL.DepthMask(s.DepthMask);
        GL.DepthFunc((DepthFunction)s.DepthFunc);
    }

    private static void Toggle(EnableCap cap, bool on)
    {
        if (on)
        {
            GL.Enable(cap);
        }
        else
        {
            GL.Disable(cap);
        }
    }

    private void ReleaseTargets()
    {
        ReleaseTransparencyTargets();
        ReleaseWeatherDepth();
        if (_fb != 0)
        {
            GL.DeleteFramebuffer(_fb);
            GL.DeleteFramebuffer(_pictureFb);
            GL.DeleteFramebuffer(_sceneFb);
            GL.DeleteFramebuffer(_readFb);
            GL.DeleteTexture(_scene);
            GL.DeleteTexture(_sceneDepth);
            GL.DeleteRenderbuffer(_depth);
            GL.DeleteTexture(_color);
            GL.DeleteTexture(_picture);
        }

        (_fb, _pictureFb, _depth, _color, _picture, _height) = (0, 0, 0, 0, 0, 0);
        (_scene, _sceneDepth, _sceneFb, _readFb) = (0, 0, 0, 0);
    }

    // The workbench closed (or the view went off): every GL object goes
    public void Dispose()
    {
        ReleaseTargets();
        DropProgram();
        DisposeScene();
        DisposeTransparency();
        DisposeWeather();
        if (_vbo != 0)
        {
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_flags);
            GL.DeleteBuffer(_tints);
            if (_colormap != 0) GL.DeleteBuffer(_colormap);
            if (_animation != 0) GL.DeleteBuffer(_animation);

            _animation = 0;
            GL.DeleteVertexArray(_vao);
            GL.DeleteTexture(_pattern);
            GL.DeleteTexture(_white);
            GL.DeleteTexture(_black);
            GL.DeleteTexture(_sunParts);
            GL.DeleteTexture(_shadow);
        }

        (_vbo, _vao, _pattern, _white, _shadow, _pending, Note) = (0, 0, 0, 0, 0, null, "");
        (_flags, _black, _tints, _colormap, _vertices, _climateDirty) = (0, 0, 0, 0, null, true);
    }
}

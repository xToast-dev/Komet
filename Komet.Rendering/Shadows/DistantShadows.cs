using HarmonyLib;
using Komet.Core;
using OpenTK.Graphics.OpenGL;
using System.Reflection;
using System.Text.RegularExpressions;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Client;
using Vintagestory.Common;
using static Komet.Rendering.Fields;
using Komet.Diagnostics;

namespace Komet.Rendering;

// Engine shadow maps fade out between 40 and 90 % of their range (nothing past ~200 blocks at best quality). A third
// map covers the view distance (up to 2048 blocks), fading out round its last fifth so shadows end where the terrain
// does; distant terrain and sun change slowly, so it is redrawn one tile a frame into a back texture. Light view
// snapped to the world texel grid so edges don't crawl.
internal static partial class DistantShadows
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x0EB00BD70865F789UL;

    private const int Unit = 15, Tiles = 4, MaxPrograms = 512, Placeholder = 16;
    private const double MaxAngle = 0.3 * Math.PI / 180, Margin = 96, MinRadius = 256, MaxRadius = 2048, Bias = 1.5;
    private const long MaxAge = 20000;

    private static bool _shaped, _injected, _failed;
    private static ILogger? _logger;
    private static string? _rejected; // the vertex include last found not to be the engine's ("" when missing)

    // _front is sampled, the other is being drawn
    private static readonly int[] Textures = new int[2], Buffers = new int[2];
    private static readonly Map[] Maps = [new(), new()];
    private static int _front, _size, _tile = -1;

    // For the shaders: matrix into the front map for this frame's camera (_version bumps on change)
    private static readonly float[] Matrix = new float[16], Mvp = new float[16], OnTexel = new float[3];

    // Per-frame matrix scratch (no allocation); main thread
    private static readonly double[] Lit = new double[3], Cam = new double[3], Eye = new double[3];
    private static readonly double[] Step1 = Mat4d.Create(), Step2 = Mat4d.Create(), Clip = Mat4d.Create();
    private static readonly double[] Sample = Mat4d.Create(), Part = Mat4d.Create(), World = Mat4d.Create();
    private static readonly double[] Offset =
        Mat4d.Scale(Mat4d.Create(), Mat4d.Translate(Mat4d.Create(), Mat4d.Create(), 0.5, 0.5, 0.5), [0.5, 0.5, 0.5]);
    private static readonly double[] Up = [0, 1, 0], North = [0, 0, 1], Origin = [0, 0, 0];
    private static float _on;
    private static int _version = 1, _bound, _maps = 1; // _maps: new whenever the front texture's content changes
    private static readonly Dictionary<int, Program> Programs = [];

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => _shaped;
    internal static bool Injected => _injected;
    internal static bool Failed => _failed;

    // Map being drawn: kept across frames, so its casters are not the view's (ShadowView)
    internal static bool Drawing { get; private set; }


    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, _injected, _failed, _logger, _tile, _rejected) = (false, false, false, logger, -1, null);
        Programs.Clear();
        var seams = Seams();
        if (!NotNull(harmony) || seams[0] is not MethodInfo done ||
            seams[1] is not MethodInfo load || seams[3] is not MethodInfo use) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(DistantShadows), logger);
        if (!_shaped) return;
        _ = harmony.Patch(load, new HarmonyMethod(Inject), new HarmonyMethod(Define));
        _ = harmony.Patch(done, postfix: new HarmonyMethod(Frame));
        _ = harmony.Patch(use, postfix: new HarmonyMethod(Use));
    }

    internal static MethodBase?[] Seams() =>
    [
        AccessTools.DeclaredMethod(typeof(SystemRenderShadowMap), "OnRenderShadowFarDone", [typeof(float)]),
        AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
            [typeof(ShaderProgram), typeof(bool)]),
        AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]),
        AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use))
    ];

    // Once per full shader load (reload re-reads assets), both includes or neither. Includes found foreign are
    // remembered by reference, so the programs after the first of a load neither search nor log again.
    internal static void Inject()
    {
        if (ShaderEdits.Includes() is not { } includes) return;
        var vertex = includes.GetValueOrDefault(VertexFile);
        if (ReferenceEquals(vertex ?? "", _rejected) || vertex is not null && vertex.Contains(Marker, StringComparison.Ordinal)) return;
        Programs.Clear(); // programs are rebuilt
        var (v, f) = (Vertex(vertex), Fragment(includes.GetValueOrDefault(FragmentFile)));
        _injected = v is not null && f is not null;
        if (!_injected)
        {
            _rejected = vertex ?? "";
            // nothing samples the maps from here (2 x 64 MiB at 4096²); Vulkan lets go of deleted GL textures (GlTap)
            Forget();
            _logger?.Notification("Komet: the shadow shaders are not the engine's, distant shadows stay off");
            return;
        }

        (includes[VertexFile], includes[FragmentFile]) = (v!, f!);
    }

    internal static void Define(ShaderProgram program)
    {
        if (!_injected || !NotNull(program) || program.FragmentShader is not { } fragment) return;
        if (program.includes.Contains(VertexFile) && program.includes.Contains(FragmentFile))
            fragment.PrefixCode = (fragment.PrefixCode ?? "") + Definition;
    }

    internal static void Frame(SystemRenderShadowMap __instance, float dt)
    {
        if (NotNull(__instance) && Game(__instance) is { } game) Tick(game, dt);
    }

    // Also where FarShadowReuse leaves the far pass out
    internal static void Tick(ClientMain game, float dt)
    {
        if (!_injected || !NotNull(game)) return;
        try
        {
            if (_failed) Placeheld();
            else Step(game, dt);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!_failed) _logger?.Error("Komet: distant shadows failed, they stay off: {0}", e);
            (_failed, _on) = (true, 0);
            _version++;
        }
    }

    private static void Step(ClientMain game, float dt)
    {
        var (quality, player) = (FrameSettings.ShadowMapQuality, game.EntityPlayer);
        var intensity = game.shUniforms.DropShadowIntensity;
        if (!Enabled || quality <= 0 || intensity <= 0.01f || player is null)
        {
            Off();
            Placeheld();
            return;
        }

        if (!Ready(quality >= 3 ? 4096 : 2048)) return;
        (Cam[0], Cam[1], Cam[2]) = (player.CameraPos.X, player.CameraPos.Y, player.CameraPos.Z);
        Light(game, Lit);
        var radius = Math.Clamp(FrameSettings.ViewDistance, MinRadius, MaxRadius) + Margin;
        if (_tile < 0 && Due(Maps[_front], Lit, Cam, radius))
            (_tile, Maps[1 - _front]) = (0, Plan(Lit, Cam, radius, _size));
        if (_tile >= 0) Draw(game, Maps[1 - _front], dt);
        Publish(Maps[_front], Cam);
    }

    // As the engine: stronger of moon and sun, normalised
    internal static void Light(ClientMain game, Span<double> into)
    {
        var calendar = game.Calendar as ClientGameCalendar;
        var v = calendar is not null && calendar.MoonLightStrength > calendar.SunLightStrength
            ? game.Calendar.MoonPosition
            : game.Calendar.SunPosition;
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (!Assert(into.Length == 3)) return;
        (into[0], into[1], into[2]) = Assert(length > 0) ? (v.X / length, v.Y / length, v.Z / length) : (0d, 1d, 0d);
    }

    internal static bool Due(Map map, double[] light, double[] camera, double radius)
    {
        if (!map.Valid || Math.Abs(map.Radius - radius) > 0.5) return true;
        var dot = light[0] * map.Direction[0] + light[1] * map.Direction[1] + light[2] * map.Direction[2];
        var (dx, dz) = (camera[0] - map.Center[0], camera[2] - map.Center[2]);
        return Math.Acos(Math.Clamp(dot, -1, 1)) > MaxAngle || dx * dx + dz * dz > Margin * Margin ||
               Environment.TickCount64 - map.Drawn > MaxAge;
    }

    // Camera snapped in the light plane to the nearest texel corner, so the grid stays put in the world
    internal static Map Plan(double[] light, double[] camera, double radius, int size)
    {
        var map = new Map { Radius = radius, Depth = 4 * radius + 1024, Drawn = Environment.TickCount64 };
        if (!Assert(size > 0) || !Assert(radius > 0)) return map;
        _ = Mat4d.LookAt(map.View, light, Origin, Math.Abs(light[1]) > 0.999 ? North : Up);
        _ = Mat4d.Ortho(map.Projection, -radius, radius, -radius, radius, -map.Depth, map.Depth);
        var texel = 2 * radius / size;
        var v = map.View;
        ReadOnlySpan<double> right = [v[0], v[4], v[8]], above = [v[1], v[5], v[9]];
        var (x, y) = (right[0] * camera[0] + right[1] * camera[1] + right[2] * camera[2],
            above[0] * camera[0] + above[1] * camera[1] + above[2] * camera[2]);
        var (dx, dy) = (Math.Round(x / texel) * texel - x, Math.Round(y / texel) * texel - y);
        for (var i = 0; i < 3; i++) map.Center[i] = camera[i] + right[i] * dx + above[i] * dy;
        (map.Direction[0], map.Direction[1], map.Direction[2]) = (light[0], light[1], light[2]);
        return map;
    }

    // Camera-relative position to the map's clip space
    internal static double[] Transform(Map map, double[] camera)
    {
        if (!Assert(camera.Length == 3) || !NotNull(map)) return Clip;
        _ = Mat4d.Identity(Step1);
        var c = map.Center;
        _ = Mat4d.Translate(Step1, Step1, camera[0] - c[0], camera[1] - c[1], camera[2] - c[2]);
        _ = Mat4d.Mul(Step2, map.View, Step1);
        return Mat4d.Mul(Clip, map.Projection, Step2);
    }

    private static void Publish(Map front, double[] camera)
    {
        var on = front.Valid ? 1f : 0f;
        _ = Mat4d.Mul(Sample, Offset, Transform(front, camera));
        var changed = Differ(on, _on);
        for (var i = 0; i < 16; i++)
        {
            changed |= Differ(Matrix[i], (float)Sample[i]);
            Matrix[i] = (float)Sample[i];
        }

        _on = on;
        if (changed) _version++;
    }

    private static bool Differ(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b);

    // Off, weak, failed or not drawn yet: the injected shaders still declare kometDistantMap, and Vulkan draws no program
    // whose sampler has nothing bound (the terrain went missing, water alone left). A small map at depth 1 stays bound,
    // weighted 0. Textures only, no framebuffer: Use makes it too, mid-pass, since the engine skips the far shadow pass
    // (and with it Tick) at night.
    private static void Placeheld()
    {
        if (Textures[0] != 0 && Textures[1] != 0) return;
        // half a pair (Ready threw on the first map's framebuffer): a front of 1 would bind no texture at all
        if (Textures[0] != 0 || Textures[1] != 0) Forget();
        GL.GetInteger(GetPName.TextureBinding2D, out int bound);
        var depth = new float[Placeholder * Placeholder];
        Array.Fill(depth, 1f);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                Textures[i] = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, Textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, Placeholder, Placeholder, 0,
                    PixelFormat.DepthComponent, PixelType.Float, depth);
                Parameters();
            }
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, bound);
        }

        (_size, _front, _tile, Maps[0], Maps[1]) = (Placeholder, 0, -1, new Map(), new Map());
        _version++; // every program binds it anew
    }

    private static void Off()
    {
        if (!Differ(_on, 0)) return;
        (_on, _tile) = (0, -1);
        _version++;
    }

    private static void Draw(ClientMain game, Map map, float dt)
    {
        if (!Index(_tile, Tiles * Tiles)) return;
        var (tx, ty, step) = (_tile % Tiles, _tile / Tiles, _size / Tiles);
        var (culler, shader, terrain) = (game.frustumCuller, ShaderPrograms.Chunkshadowmap, TerrainOf(game));
        if (!NotNull(culler) || !NotNull(shader) || !NotNull(terrain)) return;
        var (rangeX, rangeZ, stage) = (culler.shadowRangeX, culler.shadowRangeZ, Stage(game));
        Cull(game, map, tx, ty);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Buffers[1 - _front]);
        GL.Viewport(0, 0, _size, _size);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(tx * step, ty * step, step, step);
        GL.DepthMask(true);
        // RenderShadow culls as far pass in that stage; Vulkan takes the tile's clear and draws in it
        Stage(game) = EnumRenderStage.ShadowFar;
        try
        {
            GL.Clear(ClearBufferMask.DepthBufferBit);
            shader.Use();
            var clip = Transform(map, Cam);
            for (var i = 0; i < 16; i++) Mvp[i] = (float)clip[i];
            shader.MvpMatrix = Mvp;
            Drawing = true;
            terrain.RenderShadow(dt);
        }
        finally
        {
            (Stage(game), Drawing) = (stage, false);
            shader.Stop();
            GL.Disable(EnableCap.ScissorTest);
            (culler.shadowRangeX, culler.shadowRangeZ) = (rangeX, rangeZ);
            // as the engine leaves its own shadow pass
            game.Platform.UnloadFrameBuffer(EnumFrameBuffer.ShadowmapFar);
            game.Platform.LoadFrameBuffer(EnumFrameBuffer.Primary);
        }

        Tiled(map);
    }

    private static void Cull(ClientMain game, Map map, int tx, int ty)
    {
        var (r, part) = (map.Radius, 2 * map.Radius / Tiles);
        _ = Mat4d.Ortho(Part, -r + tx * part, -r + (tx + 1) * part, -r + ty * part, -r + (ty + 1) * part, -map.Depth,
            map.Depth);
        for (var i = 0; i < 3; i++) Eye[i] = map.Center[i] + map.Direction[i];
        _ = Mat4d.LookAt(World, Eye, map.Center, Math.Abs(map.Direction[1]) > 0.999 ? North : Up);
        (game.frustumCuller.shadowRangeX, game.frustumCuller.shadowRangeZ) = (r, r);
        game.frustumCuller.CalcFrustumEquations(game.EntityPlayer.Pos.AsBlockPos, Part, World);
    }

    private static void Tiled(Map map)
    {
        if (++_tile < Tiles * Tiles) return;
        (_tile, map.Valid, _front) = (-1, true, 1 - _front);
        (_version, _maps) = (_version + 1, _maps + 1);
    }

    internal static void Use(ShaderProgramBase __instance)
    {
        if (!_injected || !NotNull(__instance) || Programs.Count > MaxPrograms) return;
        if (Textures[_front] == 0) Placeheld(); // before the first far shadow pass, or a night without one
        if (!Programs.TryGetValue(__instance.ProgramId, out var p))
            Programs[__instance.ProgramId] = p = Locate(__instance);
        if (p.SamplerAt < 0 || p.Version == _version) return;
        GL.UniformMatrix4(p.MatrixAt, 1, false, Matrix);
        GL.Uniform1(p.OnAt, _on);
        GL.Uniform2(p.TexelAt, 1f / Math.Max(1, _size), (float)(Bias / (2 * Maps[_front].Depth)));
        GL.Uniform1(p.SamplerAt, Unit);
        p.Version = _version;
        if (_bound == _version) return;
        GL.ActiveTexture(TextureUnit.Texture0 + Unit);
        GL.BindTexture(TextureTarget.Texture2D, Textures[_front]);
        GL.ActiveTexture(TextureUnit.Texture0); // engine code binds to unit 0 without selecting it
        _bound = _version;
    }

    // For a non-GL reader (VulkanRenderer); version changes per redraw, not on matrix moves alone
    internal static bool Published(out int texture, out int version, out float[] matrix, out float[] onTexel)
    {
        (texture, version, matrix, onTexel) = (Textures[_front], _maps, Matrix, []);
        if (!_injected || texture == 0) return false;
        (OnTexel[0], OnTexel[1], OnTexel[2], onTexel) =
            (_on, 1f / Math.Max(1, _size), (float)(Bias / (2 * Maps[_front].Depth)), OnTexel); // called every frame
        return true;
    }

    private static Program Locate(ShaderProgramBase program)
    {
        var id = program.ProgramId;
        return new Program
        {
            MatrixAt = GL.GetUniformLocation(id, "kometDistantMatrix"),
            OnAt = GL.GetUniformLocation(id, "kometDistantOn"),
            SamplerAt = GL.GetUniformLocation(id, "kometDistantMap"),
            TexelAt = GL.GetUniformLocation(id, "kometDistantTexel")
        };
    }

    private static bool Ready(int size)
    {
        if (_size == size && Textures[0] != 0) return true;
        Forget();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                Textures[i] = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, Textures[i]);
                // 32-bit float like the engine's maps on Mesa, copyable by Vulkan
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, size, size, 0,
                    PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
                Parameters();
                Buffers[i] = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, Buffers[i]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D, Textures[i], 0);
                GL.DrawBuffer(DrawBufferMode.None);
                GL.ReadBuffer(ReadBufferMode.None);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new InvalidOperationException("the distant shadow framebuffer is incomplete");
                GL.Clear(ClearBufferMask.DepthBufferBit);
            }
        }
        finally
        {
            // the frame draws on into the engine's primary target, not into the new map
            GL.BindTexture(TextureTarget.Texture2D, 0);
            ScreenManager.Platform.LoadFrameBuffer(EnumFrameBuffer.Primary);
        }

        (_size, _front, _tile, Maps[0], Maps[1]) = (size, 0, -1, new Map(), new Map());
        return Assert(Textures[1] != 0) && Assert(Buffers[1] != 0);
    }

    // As the engine's shadow maps
    private static void Parameters()
    {
        var target = TextureTarget.Texture2D;
        GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(target, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        GL.TexParameter(target, TextureParameterName.TextureBorderColor, [1f, 1f, 1f, 1f]);
        GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
    }

    internal static void Forget()
    {
        for (var i = 0; i < 2; i++)
        {
            if (Textures[i] != 0) GL.DeleteTexture(Textures[i]);
            if (Buffers[i] != 0) GL.DeleteFramebuffer(Buffers[i]);
            (Textures[i], Buffers[i]) = (0, 0);
        }

        (_size, _tile, _on) = (0, -1, 0);
        (_version, _maps) = (_version + 1, _maps + 1);
    }

    internal sealed class Map
    {
        public readonly double[] Direction = new double[3], Center = new double[3];
        public readonly double[] View = Mat4d.Create(), Projection = Mat4d.Create();
        public double Radius, Depth;
        public long Drawn;
        public bool Valid;
    }

    private sealed class Program
    {
        public int MatrixAt, OnAt, SamplerAt, TexelAt, Version;
    }
}

// FS part compiles only with KOMET_DISTANT (an unwritten input fails to link). Uniforms carry a precision qualifier to
// stay out of the engine's uniform scan, where a sampler would take a texture unit and shift later ones.
internal static partial class DistantShadows
{
    internal const string VertexFile = "shadowcoords.vsh", FragmentFile = "fogandlight.fsh", Marker = "kometDistant";
    internal const string DistantDefine = "KOMET_DISTANT", Definition = "#define " + DistantDefine + " 1\r\n",
        DistantMatrix = "kometDistantMatrix";

    private const string VertexDeclarations = """
        #if SHADOWQUALITY > 0
        uniform highp mat4 kometDistantMatrix;
        uniform highp float kometDistantOn;
        out vec4 shadowCoordsDistant;
        #endif

        """;

    // After the far cascade: shadowCoordsFar.w already lost the near weight, so the three sum to at most 1
    private const string VertexBody = """

        #if SHADOWQUALITY > 0
        	shadowCoordsDistant = kometDistantMatrix * worldPos;
        	float kometEdge = smoothstep(0.75, 0.97, length((shadowCoordsDistant.xy - 0.5) * 2.0)); // a round fade, no edge
        	shadowCoordsDistant.w = kometDistantOn * max(0.0, 1.0 - kometEdge - shadowCoordsFar.w - nearSub);
        	if (shadowCoordsDistant.z >= 0.999) shadowCoordsDistant.w = 0.0;
        #endif

        """;

    private const string FragmentDeclarations = """

        #if SHADOWQUALITY > 0 && defined(KOMET_DISTANT)
        in vec4 shadowCoordsDistant;
        uniform highp sampler2DShadow kometDistantMap;
        uniform highp vec2 kometDistantTexel;
        #endif

        """;

    // kometDistantTexel: x the size of a texel, y the depth bias. Nine filtered taps a texel apart: far away a pixel covers
    // more than a texel, and fewer taps let edges crawl as the camera moves
    private const string FragmentBody = """

        	#if defined(KOMET_DISTANT)
        	if (shadowCoordsDistant.w > 0) {
        		float kometLit = 0.0;
        		for (int x = -1; x <= 1; x++) {
        			for (int y = -1; y <= 1; y++) {
        				kometLit += texture(kometDistantMap, vec3(shadowCoordsDistant.xy + vec2(x, y) * kometDistantTexel.x,
        					shadowCoordsDistant.z - kometDistantTexel.y));
        			}
        		}
        		b -= shadowIntensity * (1.0 - kometLit / 9.0) * shadowCoordsDistant.w * 0.5;
        	}
        	#endif

        """;

    // Anchors: after the far cascade's declarations, and after the statement setting its weight or brightness
    private static readonly Regex VertexDeclared = Pattern(@"out vec4 shadowCoordsFar;\s*#endif[^\n]*\n");
    private static readonly Regex VertexWeighted =
        Pattern(@"shadowCoordsFar\.w = max\(0\.0, clamp\(1\.0 - distanceFar, 0\.0, 1\.0\) - nearSub\);[^\n]*\n" +
                @"\s*if \(shadowCoordsFar\.z >= 0\.999\) shadowCoordsFar\.w = 0\.0;[^\n]*\n\s*#endif[^\n]*\n");
    private static readonly Regex FragmentDeclared = Pattern(@"uniform float shadowMapHeightInv;\s*#endif[^\n]*\n");
    private static readonly Regex FragmentShaded =
        Pattern(@"float b = 1\.0 - shadowIntensity \* totalFar \* shadowCoordsFar\.w \* 0\.5;[^\n]*\n");

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));

    // Include with the distant cascade, or null for unexpected text (shader pack, new engine)
    internal static string? Vertex(string? source) =>
        Add(source, VertexDeclared, VertexDeclarations, VertexWeighted, VertexBody);

    internal static string? Fragment(string? source) =>
        Add(source, FragmentDeclared, FragmentDeclarations, FragmentShaded, FragmentBody);

    private static string? Add(string? source, Regex declared, string declarations, Regex anchor, string body)
    {
        if (source is null || source.Contains(Marker, StringComparison.Ordinal)) return null;
        var (first, second) = (declared.Matches(source), anchor.Matches(source));
        if (first.Count != 1 || second.Count != 1 || !Assert(first[0].Index < second[0].Index)) return null;
        var (at, to) = (first[0].Index + first[0].Length, second[0].Index + second[0].Length);
        return Assert(at <= to) ? source[..at] + declarations + source[at..to] + body + source[to..] : null;
    }
}

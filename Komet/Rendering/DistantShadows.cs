using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Rendering;

// The engine casts shadows from two maps around the camera: near, and far, which fades out between 40 and 90 % of its range - at the
// highest quality nothing past about 200 blocks has a shadow, however far the view reaches. A third map covers the view distance.
// Terrain there changes slowly and the sun moves a fraction of a degree a second, so the map is not drawn every frame: it is redrawn
// when the light turned by MaxAngle, the camera moved Margin blocks from its centre, or MaxAge passed, one of Tiles x Tiles tiles a
// frame, into a second texture that replaces the first once whole. It is drawn after the engine's far map by the same terrain draw
// call and culling (ChunkRenderer.RenderShadow, far pass), from a light view aligned to the texel grid in world space, so its edges
// do not crawl as the camera moves. The shaders sample it only where the near and far maps leave off (DistantShadows.Glsl.cs).
internal static partial class DistantShadows
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x0EB00BD70865F789UL;

    private const int Unit = 15, Tiles = 4, MaxPrograms = 512;
    private const double MaxAngle = 0.3 * Math.PI / 180, Margin = 96, MinRadius = 256, MaxRadius = 1024, Bias = 1.5;
    private const long MaxAge = 20000;

    private static bool _shaped, _injected, _failed;
    private static ILogger? _logger;
    private static FieldInfo? _includes;

    // Two maps, the one sampled (_front) and the one being drawn; each with the light and centre it was drawn for
    private static readonly int[] Textures = new int[2], Buffers = new int[2];
    private static readonly Map[] Maps = [new(), new()];
    private static int _front, _size, _tile = -1;

    // What the shaders get: the matrix into the front map for this frame's camera, bumped _version when it changes
    private static readonly float[] Matrix = new float[16], Mvp = new float[16];

    // Scratch for the per-frame matrices, so that a frame allocates nothing; main thread
    private static readonly double[] Lit = new double[3], Cam = new double[3], Eye = new double[3];
    private static readonly double[] Step1 = Mat4d.Create(), Step2 = Mat4d.Create(), Clip = Mat4d.Create();
    private static readonly double[] Sample = Mat4d.Create(), Part = Mat4d.Create(), World = Mat4d.Create();
    private static readonly double[] Offset =
        Mat4d.Scale(Mat4d.Create(), Mat4d.Translate(Mat4d.Create(), Mat4d.Create(), 0.5, 0.5, 0.5), [0.5, 0.5, 0.5]);
    private static readonly double[] Up = [0, 1, 0], North = [0, 0, 1], Origin = [0, 0, 0];
    private static float _on;
    private static int _version = 1, _bound;
    private static readonly Dictionary<int, Program> Programs = [];

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => _shaped;
    internal static bool Injected => _injected;
    internal static bool Failed => _failed;
    public static long Redraws { get; private set; } // whole maps drawn, and tiles, totals while Counting.Hud
    public static long TilesDrawn { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentRenderStage")]
    private static extern ref EnumRenderStage Stage(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkRenderer")]
    private static extern ref ChunkRenderer Terrain(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain Game(ClientSystem system);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, _injected, _failed, _logger, _tile) = (false, false, false, logger, -1);
        Programs.Clear();
        _includes = AccessTools.Field(typeof(ShaderRegistry), "includes");
        var seams = Seams();
        if (!NotNull(harmony) || !NotNull(_includes) || !Assert(seams.Length == 4) || seams[0] is not MethodInfo done ||
            seams[1] is not MethodInfo load || seams[3] is not MethodInfo use) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(DistantShadows), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(Inject), new HarmonyMethod(Define)));
        _ = NotNull(harmony.Patch(done, postfix: new HarmonyMethod(Frame)));
        _ = NotNull(harmony.Patch(use, postfix: new HarmonyMethod(Use)));
    }

    // The far pass's end, after which the map is drawn; the shader loader; the terrain's shadow draw, which it calls; Use, which
    // hands the shaders their uniforms
    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(SystemRenderShadowMap), "OnRenderShadowFarDone", [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
                [typeof(ShaderProgram), typeof(bool)]),
            AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use))
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // Prefix on ShaderRegistry.LoadShaderProgram: the two includes get the cascade once per load of all shaders (a reload reads
    // them from the assets again); both or neither
    internal static void Inject()
    {
        if (_includes?.GetValue(null) is not Dictionary<string, string> includes || !NotNull(includes)) return;
        var vertex = includes.GetValueOrDefault(VertexFile);
        if (vertex is not null && vertex.Contains(Marker, StringComparison.Ordinal)) return;
        Programs.Clear(); // the programs are built again
        var (v, f) = (Vertex(vertex), Fragment(includes.GetValueOrDefault(FragmentFile)));
        _injected = v is not null && f is not null;
        if (!_injected)
        {
            _logger?.Notification("Komet: the shadow shaders are not the engine's, distant shadows stay off");
            return;
        }

        (includes[VertexFile], includes[FragmentFile]) = (v!, f!);
    }

    // Postfix: a program with both includes compiles the fragment part
    internal static void Define(ShaderProgram program)
    {
        if (!_injected || !NotNull(program) || program.FragmentShader is not { } fragment) return;
        if (program.includes.Contains(VertexFile) && program.includes.Contains(FragmentFile))
            fragment.PrefixCode = (fragment.PrefixCode ?? "") + Definition;
    }

    // Postfix on SystemRenderShadowMap.OnRenderShadowFarDone: the matrix for this frame, and a tile of the map when one is due
    internal static void Frame(SystemRenderShadowMap __instance, float dt)
    {
        if (!_injected || _failed || !NotNull(__instance)) return;
        try
        {
            if (Game(__instance) is { } game) Step(game, dt);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            (_failed, _on) = (true, 0);
            _version++;
            _logger?.Error("Komet: distant shadows failed, they stay off: {0}", e);
        }
    }

    private static void Step(ClientMain game, float dt)
    {
        var (quality, player) = (ClientSettings.ShadowMapQuality, game.EntityPlayer);
        var intensity = game.shUniforms.DropShadowIntensity;
        if (!Enabled || quality <= 0 || intensity <= 0.01f || player is null)
        {
            Off();
            return;
        }

        if (!Ready(quality >= 3 ? 4096 : 2048)) return;
        (Cam[0], Cam[1], Cam[2]) = (player.CameraPos.X, player.CameraPos.Y, player.CameraPos.Z);
        Light(game);
        var radius = Math.Clamp(ClientSettings.ViewDistance, MinRadius, MaxRadius) + Margin;
        if (_tile < 0 && Due(Maps[_front], Lit, Cam, radius))
            (_tile, Maps[1 - _front]) = (0, Plan(Lit, Cam, radius, _size));
        if (_tile >= 0) Draw(game, Maps[1 - _front], dt);
        Publish(Maps[_front], Cam);
    }

    // The light the engine casts its shadows from, into Lit: the moon when it is the stronger, else the sun; normalized
    private static void Light(ClientMain game)
    {
        var calendar = game.Calendar as ClientGameCalendar;
        var v = calendar is not null && calendar.MoonLightStrength > calendar.SunLightStrength
            ? game.Calendar.MoonPosition
            : game.Calendar.SunPosition;
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (Assert(length > 0)) (Lit[0], Lit[1], Lit[2]) = (v.X / length, v.Y / length, v.Z / length);
        else (Lit[0], Lit[1], Lit[2]) = (0, 1, 0);
    }

    // A new map is due when there is none, or the light turned, the camera left the margin, or the map is old
    internal static bool Due(Map map, double[] light, double[] camera, double radius)
    {
        if (!map.Valid || Math.Abs(map.Radius - radius) > 0.5) return true;
        var dot = light[0] * map.Direction[0] + light[1] * map.Direction[1] + light[2] * map.Direction[2];
        var (dx, dz) = (camera[0] - map.Center[0], camera[2] - map.Center[2]);
        return Math.Acos(Math.Clamp(dot, -1, 1)) > MaxAngle || dx * dx + dz * dz > Margin * Margin ||
               Environment.TickCount64 - map.Drawn > MaxAge;
    }

    // A map's light view, projection and centre: the camera moved in the light's plane onto the nearest texel corner, so that the
    // grid of texels stays put in the world
    internal static Map Plan(double[] light, double[] camera, double radius, int size)
    {
        var map = new Map { Radius = radius, Size = size, Depth = 4 * radius + 1024, Drawn = Environment.TickCount64 };
        if (!Assert(size > 0) || !Assert(radius > 0)) return map;
        _ = Mat4d.LookAt(map.View, light, Origin, Math.Abs(light[1]) > 0.999 ? North : Up);
        _ = Mat4d.Ortho(map.Projection, -radius, radius, -radius, radius, -map.Depth, map.Depth);
        var texel = 2 * radius / size;
        var (right, above) = (Row(map.View, 0), Row(map.View, 1));
        var (x, y) = (Dot(right, camera), Dot(above, camera));
        var (dx, dy) = (Math.Round(x / texel) * texel - x, Math.Round(y / texel) * texel - y);
        for (var i = 0; i < 3; i++) map.Center[i] = camera[i] + right[i] * dx + above[i] * dy;
        (map.Direction[0], map.Direction[1], map.Direction[2]) = (light[0], light[1], light[2]);
        return map;
    }

    private static double[] Row(double[] m, int row) => [m[row], m[4 + row], m[8 + row]];

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    // Light projection * view * the step from the camera to the map's centre, into Clip: what takes a camera-relative position into
    // the map's clip space
    internal static double[] Transform(Map map, double[] camera)
    {
        if (!Assert(camera.Length == 3) || !NotNull(map)) return Clip;
        _ = Mat4d.Identity(Step1);
        var c = map.Center;
        _ = Mat4d.Translate(Step1, Step1, camera[0] - c[0], camera[1] - c[1], camera[2] - c[2]);
        _ = Mat4d.Mul(Step2, map.View, Step1);
        return Mat4d.Mul(Clip, map.Projection, Step2);
    }

    // The front map's matrix for this frame's camera, from camera-relative positions into texture space
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
        if (changed && Assert(_version < int.MaxValue)) _version++;
    }

    private static bool Differ(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b);

    private static void Off()
    {
        if (!Differ(_on, 0) || !Assert(_version < int.MaxValue)) return;
        (_on, _tile) = (0, -1);
        _version++;
    }

    // One tile of the map being drawn: the terrain's far shadow pass, culled to the tile and clipped to its square of the texture
    private static void Draw(ClientMain game, Map map, float dt)
    {
        if (!Index(_tile, Tiles * Tiles) || !NotNull(map)) return;
        var (tx, ty, step) = (_tile % Tiles, _tile / Tiles, _size / Tiles);
        var (culler, shader, terrain) = (game.frustumCuller, ShaderPrograms.Chunkshadowmap, Terrain(game));
        if (!NotNull(culler) || !NotNull(shader) || !NotNull(terrain)) return;
        var (rangeX, rangeZ, stage) = (culler.shadowRangeX, culler.shadowRangeZ, Stage(game));
        Cull(game, map, tx, ty);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Buffers[1 - _front]);
        GL.Viewport(0, 0, _size, _size);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(tx * step, ty * step, step, step);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        shader.Use();
        var clip = Transform(map, Cam);
        for (var i = 0; i < 16; i++) Mvp[i] = (float)clip[i];
        shader.MvpMatrix = Mvp;
        Stage(game) = EnumRenderStage.ShadowFar; // RenderShadow culls as the far pass when the stage is the far pass
        try
        {
            terrain.RenderShadow(dt);
        }
        finally
        {
            Stage(game) = stage;
            shader.Stop();
            GL.Disable(EnableCap.ScissorTest);
            (culler.shadowRangeX, culler.shadowRangeZ) = (rangeX, rangeZ);
            // as the engine leaves its own shadow pass: the viewport back to the screen, the primary framebuffer bound
            game.Platform.UnloadFrameBuffer(EnumFrameBuffer.ShadowmapFar);
            game.Platform.LoadFrameBuffer(EnumFrameBuffer.Primary);
        }

        Tiled(map);
    }

    // The culler set to the tile: its part of the projection, the map's light view in world space, and the map's range
    private static void Cull(ClientMain game, Map map, int tx, int ty)
    {
        var (r, part) = (map.Radius, 2 * map.Radius / Tiles);
        _ = Mat4d.Ortho(Part, -r + tx * part, -r + (tx + 1) * part, -r + ty * part, -r + (ty + 1) * part, -map.Depth,
            map.Depth);
        for (var i = 0; i < 3; i++) Eye[i] = map.Center[i] + map.Direction[i];
        _ = Mat4d.LookAt(World, Eye, map.Center, Math.Abs(map.Direction[1]) > 0.999 ? North : Up);
        (game.frustumCuller.shadowRangeX, game.frustumCuller.shadowRangeZ) = (r, r);
        game.frustumCuller.CalcFrustumEquations(game.EntityPlayer.Pos.AsBlockPos, Part, World);
        _ = Assert(Finite(part)) && Assert(r > 0);
    }

    // A tile done; the last makes the map the front one
    private static void Tiled(Map map)
    {
        if (Counting.Hud) TilesDrawn++;
        if (++_tile < Tiles * Tiles) return;
        (_tile, map.Valid, _front) = (-1, true, 1 - _front);
        if (Counting.Hud) Redraws++;
        _ = Assert(Maps[_front] == map) && Assert(_version < int.MaxValue);
        _version++; // the front texture changed
    }

    // Postfix on ShaderProgramBase.Use: a program with the cascade gets its uniforms when they changed since it last had them, and
    // the front map on its texture unit once per change
    internal static void Use(ShaderProgramBase __instance)
    {
        if (!_injected || !NotNull(__instance) || Programs.Count > MaxPrograms) return;
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

    private static Program Locate(ShaderProgramBase program)
    {
        var id = program.ProgramId;
        var p = new Program
        {
            MatrixAt = GL.GetUniformLocation(id, "kometDistantMatrix"),
            OnAt = GL.GetUniformLocation(id, "kometDistantOn"),
            SamplerAt = GL.GetUniformLocation(id, "kometDistantMap"),
            TexelAt = GL.GetUniformLocation(id, "kometDistantTexel")
        };
        _ = Assert(id > 0) && NotNull(p);
        return p;
    }

    // The two maps as textures and framebuffers of the size the quality asks for, cleared to lit; false while they cannot be
    private static bool Ready(int size)
    {
        if (_size == size && Textures[0] != 0) return true;
        Forget();
        for (var i = 0; i < 2; i++)
        {
            Textures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Textures[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, size, size, 0,
                PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
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

        GL.BindTexture(TextureTarget.Texture2D, 0);
        (_size, _front, _tile, Maps[0], Maps[1]) = (size, 0, -1, new Map(), new Map());
        return Assert(Textures[1] != 0) && Assert(Buffers[1] != 0);
    }

    // As the engine's shadow maps: filtered depth comparison, lit outside the map
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
        _ = Assert(Textures[0] != 0 || Textures[1] != 0);
    }

    // The GL objects deleted: a world ends, or the size changes
    internal static void Forget()
    {
        for (var i = 0; i < 2; i++)
        {
            if (Textures[i] != 0) GL.DeleteTexture(Textures[i]);
            if (Buffers[i] != 0) GL.DeleteFramebuffer(Buffers[i]);
            (Textures[i], Buffers[i]) = (0, 0);
        }

        (_size, _tile, _on) = (0, -1, 0);
        _ = Assert(Textures[0] == 0) && Assert(_version < int.MaxValue);
        _version++;
    }

    // A map: the light it was drawn for, its centre in the world, its light view and projection
    internal sealed class Map
    {
        public readonly double[] Direction = new double[3], Center = new double[3];
        public readonly double[] View = Mat4d.Create(), Projection = Mat4d.Create();
        public double Radius, Depth;
        public int Size;
        public long Drawn;
        public bool Valid;
    }

    private sealed class Program
    {
        public int MatrixAt, OnAt, SamplerAt, TexelAt, Version;
    }
}

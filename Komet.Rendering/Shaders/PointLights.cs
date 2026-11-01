using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Dynamic lights (held torches, glowing items on the ground, lightning) had three faults, all vanilla:
// - Their positions went to the shaders in view space through the modelview matrix of the Before stage, which is the
//   previous frame's camera (this frame's is loaded after it): every light lagged a frame and swam as the camera moved.
//   Here the frame's lights are moved again into this frame's camera as the opaque stage begins.
// - The shader took a light's reach as plc.r / normalize(plc).r: 0/0 for a light without red.
// - A weak light reached as far as its colour's length: a level 1 light (the temporal amulet) 0.15 blocks, so with
//   lighting per vertex only the odd vertex of the item or of a block corner caught its full brightness, and the lit
//   spot jumped with every move. A light now reaches at least 2 blocks at its own brightness (dimmed in step with
//   the reach it was 10 % bright: the amulet's light was gone).
internal static class PointLights
{
    internal const string File = "fogandlight.vsh", Marker = "kometPointLight";
    private const int MaxLights = 100; // DefaultShaderUniforms.PointLights3: 300 floats
    private const double Order = -1; // before every opaque renderer

    internal const string Reach = "\t\tfloat range = plc.r / color.r;";
    internal const string Falloff = "\t\trange = min(25, range);\r\n\t\tif (lightDistance > range) continue;\r\n\t\t\t\r\n" +
                                    "\t\tfloat bright = (color.r + color.g + color.b) / 3;";

    internal const string KometReach = "\t\tfloat range = length(plc); // " + Marker + ": a light without red has a reach too";
    internal const string KometFalloff = "\t\trange = min(25, range);\r\n" +
                                         "\t\trange = max(range, 2.0); // kometReach: a weak light as bright, softly over 2 blocks\r\n" +
                                         "\t\tif (lightDistance > range) continue;\r\n\t\t\t\r\n" +
                                         "\t\tfloat bright = (color.r + color.g + color.b) / 3;";

    private static readonly double[] World = new double[MaxLights * 3];
    private static readonly long[] Noted = new long[MaxLights]; // the frame each slot's World was noted in
    private static long _frame = 1; // counts Reproject's calls: a slot noted since the last one is this frame's
    private static readonly Vec4d In = new(), Out = new();
    private static ICoreClientAPI? _api;
    private static Stage? _stage;

    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            ShaderEdits.Reload();
        }
    } = true;

    public static bool Patched { get; private set; }
    public static bool Injected { get; private set; } // the shader edit found its lines (the join's reload comes after Install)

    public static void Install(Harmony harmony, ICoreClientAPI? api, ILogger? logger)
    {
        (Patched, Injected, _api) = (false, false, api);
        var type = typeof(SystemRenderPlayerEffects);
        var (hsv, color) = (AccessTools.DeclaredMethod(type, "AddPointLight", [typeof(byte[]), typeof(EntityPos)]),
            AccessTools.DeclaredMethod(type, "AddPointLight", [typeof(Vec3f), typeof(Vec3d)]));
        var load = AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram", [typeof(ShaderProgram), typeof(bool)]);
        if (!NotNull(harmony) || api is null) return;
        if (hsv is null || color is null || load is null)
        {
            logger?.Warning("Komet PointLights: the engine's light methods were not found; lights stay as in vanilla");
            return;
        }

        _ = harmony.Patch(hsv, new HarmonyMethod(AccessTools.Method(typeof(PointLights), nameof(Added))));
        _ = harmony.Patch(color, new HarmonyMethod(AccessTools.Method(typeof(PointLights), nameof(AddedAt))));
        _ = harmony.Patch(load, new HarmonyMethod(AccessTools.Method(typeof(PointLights), nameof(Inject))));
        _stage ??= new Stage(); // the game owns it once registered
        api.Event.RegisterRenderer(_stage, EnumRenderStage.Opaque, "komet-pointlights");
        Patched = true;
    }

    // The feature's Stop: a closed world's API is not kept
    internal static void Forget() => (_api, Patched, Injected) = (null, false, false);

    // Prefixes on SystemRenderPlayerEffects.AddPointLight: where the light at the next index is, in the world
    internal static void Added(EntityPos pos) => Note(pos.X, pos.InternalY, pos.Z);

    internal static void AddedAt(Vec3d pos) => Note(pos.X, pos.Y, pos.Z);

    private static void Note(double x, double y, double z)
    {
        if (_api?.Render.ShaderUniforms is not { } uniforms) return;
        var at = uniforms.PointLightsCount;
        if (at is < 0 or >= MaxLights) return; // the engine adds none past its own limit either
        (World[at * 3], World[at * 3 + 1], World[at * 3 + 2]) = (x, y, z);
        Noted[at] = _frame;
    }

    // As the opaque stage begins the modelview matrix is this frame's camera. Only the slots noted this frame: a light a mod
    // wrote straight into ShaderUniforms has no world position here and keeps the engine's
    internal static void Reproject()
    {
        var frame = _frame;
        _frame++;
        if (!Enabled || _api?.Render is not { } render || render.ShaderUniforms is not { } uniforms || render.MvMatrix?.Top is not { } camera)
            return;
        var lights = uniforms.PointLights3;
        var count = Math.Min(uniforms.PointLightsCount, Math.Min(MaxLights, lights.Length / 3));
        for (var i = 0; i < Math.Min(count, MaxLights); i++)
        {
            if (Noted[i] != frame) continue;
            In.Set(World[i * 3], World[i * 3 + 1], World[i * 3 + 2], 1.0);
            Mat4d.MulWithVec4(camera, In, Out);
            (lights[i * 3], lights[i * 3 + 1], lights[i * 3 + 2]) = ((float)Out.X, (float)Out.Y, (float)Out.Z);
        }
    }

    // Prefix on ShaderRegistry.LoadShaderProgram: the include's two lines, once per load (a reload re-reads the assets)
    internal static void Inject()
    {
        if (!Enabled || ShaderEdits.Includes() is not { } includes ||
            includes.GetValueOrDefault(File) is not { } code || code.Contains(Marker, StringComparison.Ordinal)) return;
        var edited = Edit(code);
        Injected = edited is not null;
        if (edited is not null) includes[File] = edited;
    }

    // Null unless both lines are found once each; the assets' line ends as they were read
    internal static string? Edit(string code)
    {
        if (!NotNull(code)) return null;
        return Edit(code, "\r\n") ?? Edit(code, "\n");
    }

    private static string? Edit(string code, string line)
    {
        var (reach, falloff) = (Ends(Reach, line), Ends(Falloff, line));
        var (kometReach, kometFalloff) = (Ends(KometReach, line), Ends(KometFalloff, line));
        var (r, f) = (code.IndexOf(reach, StringComparison.Ordinal), code.IndexOf(falloff, StringComparison.Ordinal));
        if (r < 0 || f < 0 || code.IndexOf(reach, r + 1, StringComparison.Ordinal) >= 0 ||
            code.IndexOf(falloff, f + 1, StringComparison.Ordinal) >= 0) return null;
        return code.Replace(falloff, kometFalloff, StringComparison.Ordinal).Replace(reach, kometReach, StringComparison.Ordinal);
    }

    private static string Ends(string text, string line) => line == "\r\n" ? text : text.Replace("\r\n", line, StringComparison.Ordinal);

    private sealed class Stage : IRenderer
    {
        public double RenderOrder => Order;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => Reproject();

        public void Dispose()
        {
        }
    }
}

using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace Komet.Sdk;

// "Force in the game": the workbench makes the live game draw a shader whose conditions keep it hidden, as the 3D view
// always does. A post effect's graphics setting is switched on (and back to the player's value when the force ends or
// the world closes); the aurora draws at full strength whatever the climate and the time of day.
internal static class LiveForce
{
    private const string Aurora = "aurora";

    // Program -> the setting that keeps it from drawing and the value that lets it draw
    private static readonly Dictionary<string, (string Setting, int On)> Settings = new(StringComparer.Ordinal)
    {
        ["godrays"] = ("GodRayQuality", 1),
        ["ssao"] = ("SSAOQuality", 2),
        ["bilateralblur"] = ("SSAOQuality", 2),
        ["findbright"] = ("Bloom", 1),
        ["blur"] = ("Bloom", 1),
        ["luma"] = ("FXAA", 1),
        ["cloudvolumetric"] = ("CloudRenderMode", 1),
        ["cloudmap"] = ("CloudRenderMode", 1),
        ["clouds"] = ("CloudRenderMode", 0),
        ["chunkshadowmap"] = ("ShadowMapQuality", 1),
        ["shadowmapentityanimated"] = ("ShadowMapQuality", 1),
        ["particlescube"] = ("ParticleLevel", 100),
        ["particlesquad"] = ("ParticleLevel", 100),
        ["particlesquad2d"] = ("ParticleLevel", 100),
        ["transparentcompose"] = ("TransparentRenderPass", 1)
    };

    private static readonly HashSet<string> Forced = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Kept = new(StringComparer.Ordinal); // setting -> the player's value
    private static bool _patched;

    private static readonly AccessTools.FieldRef<AuroraRenderer, ICoreClientAPI> AuroraApi =
        AccessTools.FieldRefAccess<AuroraRenderer, ICoreClientAPI>("capi");

    private static readonly AccessTools.FieldRef<AuroraRenderer, IShaderProgram> AuroraProgram =
        AccessTools.FieldRefAccess<AuroraRenderer, IShaderProgram>("prog");

    private static readonly AccessTools.FieldRef<AuroraRenderer, MeshRef> AuroraMesh =
        AccessTools.FieldRefAccess<AuroraRenderer, MeshRef>("quadTilesRef");

    private static readonly AccessTools.FieldRef<AuroraRenderer, Matrixf> AuroraMatrix =
        AccessTools.FieldRefAccess<AuroraRenderer, Matrixf>("mvMat");

    private static readonly AccessTools.FieldRef<AuroraRenderer, Vec4f> AuroraColor =
        AccessTools.FieldRefAccess<AuroraRenderer, Vec4f>("col");

    private static readonly AccessTools.FieldRef<AuroraRenderer, bool> AuroraOn =
        AccessTools.FieldRefAccess<AuroraRenderer, bool>("renderAurora");

    // Whether the workbench can force this program in the game
    public static bool Can(string? program) =>
        program is not null && (program == Aurora || Settings.ContainsKey(program));

    public static bool IsForced(string? program) => program is not null && Forced.Contains(program);

    public static void Install(Harmony harmony)
    {
        if (_patched || AccessTools.DeclaredMethod(typeof(AuroraRenderer), nameof(AuroraRenderer.OnRenderFrame)) is not
                { } render) return;

        _ = harmony.Patch(render, postfix: new HarmonyMethod(typeof(LiveForce), nameof(AuroraPostfix)));
        _patched = true;
    }

    public static void Set(string program, bool on)
    {
        if (!Can(program) || on == Forced.Contains(program)) return;

        _ = on ? Forced.Add(program) : Forced.Remove(program);
        if (!Settings.TryGetValue(program, out var setting)) return;

        if (on)
        {
            _ = Kept.TryAdd(setting.Setting, Read(setting.Setting));
            Write(setting.Setting, setting.On);
        }
        else if (!Forced.Any(p => Settings.TryGetValue(p, out var s) && s.Setting == setting.Setting) &&
                 Kept.Remove(setting.Setting, out var player))
        {
            Write(setting.Setting, player); // no other forced program needs it: the player's value back
        }
    }

    // World end: every setting back to the player's value
    public static void Clear()
    {
        foreach (var (setting, player) in Kept.Bounded(16)) Write(setting, player);

        Kept.Clear();
        Forced.Clear();
    }

    private static int Read(string setting) => setting switch
    {
        "GodRayQuality" => ClientSettings.GodRayQuality,
        "SSAOQuality" => ClientSettings.SSAOQuality,
        "Bloom" => ClientSettings.Bloom ? 1 : 0,
        "FXAA" => ClientSettings.FXAA ? 1 : 0,
        "ShadowMapQuality" => ClientSettings.ShadowMapQuality,
        "ParticleLevel" => ClientSettings.ParticleLevel,
        "TransparentRenderPass" => ClientSettings.TransparentRenderPass ? 1 : 0,
        _ => ClientSettings.CloudRenderMode
    };

    private static void Write(string setting, int value)
    {
        switch (setting)
        {
            case "GodRayQuality": ClientSettings.GodRayQuality = value; break;
            case "SSAOQuality": ClientSettings.SSAOQuality = value; break;
            case "Bloom": ClientSettings.Bloom = value != 0; break;
            case "FXAA": ClientSettings.FXAA = value != 0; break;
            case "ShadowMapQuality": ClientSettings.ShadowMapQuality = value; break;
            case "ParticleLevel": ClientSettings.ParticleLevel = value; break;
            case "TransparentRenderPass": ClientSettings.TransparentRenderPass = value != 0; break;
            default: ClientSettings.CloudRenderMode = value; break;
        }
    }

    // The game skipped the aurora (too warm, too bright): drawn as the game draws it, at full strength
    private static void AuroraPostfix(AuroraRenderer __instance)
    {
        if (!Forced.Contains(Aurora) || !AuroraOn(__instance) || AuroraColor(__instance) is not { W: <= 0f } col ||
            AuroraApi(__instance) is not { } capi || AuroraProgram(__instance) is not { LoadError: false } prog ||
            capi.Render.FrameWidth == 0) return;

        col.W = 1f;
        var camera = capi.World.Player.Entity.CameraPos;
        prog.Use();
        prog.Uniform("color", col);
        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
        prog.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
        prog.Uniform("flatFogDensity", capi.Ambient.BlendedFlatFogDensity);
        prog.Uniform("flatFogStart", capi.Ambient.BlendedFlatFogYPosForShader - (float)camera.Y);
        var speed = capi.World.Calendar.SpeedOfTime / 60f;
        prog.Uniform("auroraCounter", (float)(capi.InWorldEllapsedMilliseconds / 5000.0 * speed % 579f));
        var matrix = AuroraMatrix(__instance);
        _ = matrix.Set(capi.Render.MvMatrix.Top).FollowPlayer()
            .Translate(0.0, 1.1f * capi.World.BlockAccessor.MapSizeY + 0.5 - camera.Y, 0.0);
        prog.UniformMatrix("modelViewMatrix", matrix.Values);
        capi.Render.RenderMesh(AuroraMesh(__instance));
        prog.Stop();
    }
}

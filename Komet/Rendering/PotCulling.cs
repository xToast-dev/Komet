using System.Reflection;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// PotInFirepitRenderer (survival mod) draws its pot and wobbling lid in Opaque every frame wherever the firepit is, with no
// frustum test: a standard-shader setup and draw call each (a tenth of the main thread in a base with a kitchen of them). The
// prefix skips both draws when the sphere holding everything they place lies wholly outside the Opaque stage's clip volume (as
// AnimatableCulling reasons): the pot fills its block cell, the lid sits 13/32 up and wobbles 5/16 and a sixtieth of a turn
// (Lid, generously), plus AnimatableCulling.Warp.
//
// A skipped draw leaves the GL state a drawn one does: face culling off, standard blending on, its shader stopped.
internal static class PotCulling
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xE7ED08273DA67B79UL;

    private const string Renderer = "Vintagestory.GameContent.PotInFirepitRenderer";
    // Half the cell's diagonal; what the lid adds beyond the cell, generously: too far out costs a draw, too tight a hole
    private const float Cell = 0.8661f, Lid = 1.25f;

    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // Body not verified, survival mod missing or another mod patches it: every draw is the engine's
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;

    // Totals while Counting.Hud (main thread)
    public static long Culled { get; private set; }
    public static long Drawn { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _seams, _logger, _foreign) = (false, true, Seams(), logger, false);
        if (!NotNull(harmony) || !Assert(_seams.Length == 1) || _seams[0] is null) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(PotCulling), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Frame)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == 1);
        _foreign = EngineShape.Report(_logger, nameof(PotCulling), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.Replacing, null, typeof(PotCulling)));
        Blocked = !seamed || _foreign;
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Renderer);
        MethodBase?[] seams =
            [type is null ? null : AccessTools.DeclaredMethod(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Frame(BlockPos ___pos, ICoreClientAPI ___capi, EnumRenderStage stage)
    {
        if (!Enabled || Blocked || stage != EnumRenderStage.Opaque || ___pos is null ||
            ___capi?.Render is not { } render || render.DefaultFrustumCuller is not { } culler) return true;
        var radius = Cell + Lid + AnimatableCulling.Warp(render.ShaderUniforms);
        if (!Finite(radius) || culler.SphereInFrustum(___pos.X + 0.5, ___pos.Y + 0.5, ___pos.Z + 0.5, radius))
        {
            if (Counting.Hud) Drawn++;
            return true;
        }

        if (!LeftState.Holds(LeftState.Kind.Pot))
        {
            render.GlDisableCullFace();
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            LeftState.Left(LeftState.Kind.Pot);
        }

        if (Counting.Hud) Culled++;
        _ = Assert(radius > 0);
        return false;
    }
}

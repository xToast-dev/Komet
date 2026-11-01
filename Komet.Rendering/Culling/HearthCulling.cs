using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// FirepitContentsRenderer and ForgeContentsRenderer (survival) draw what lies in a firepit or forge in Opaque every frame,
// with no frustum test, wherever it stands: in a base of 90 firepits and 44 forges 17 000 calls a second. Skipped when
// the bounding sphere lies outside the Opaque clip volume (as PotCulling): the contents sit in their cell, a firepit's
// item lifted and turned by its transform (Reach), plus Warp. A skipped draw leaves GL state as the draw would: a
// firepit culling off and standard blending (its pot's too), a forge culling off; shaders stopped. A firepit's other
// content renderers (mods') draw as before.
internal static class HearthCulling
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x3F4A55EDB7E7FFB8UL;

    private const string Firepit = "Vintagestory.GameContent.FirepitContentsRenderer",
        Forge = "Vintagestory.GameContent.ForgeContentsRenderer", Pot = "Vintagestory.GameContent.PotInFirepitRenderer";
    private const float Cell = 0.8661f, Reach = 1.25f;

    private static readonly SeamGuard Guard = new(nameof(HearthCulling), PatchKinds.Replacing, [typeof(HearthCulling)]);
    private static readonly Tally Counts = new();
    private static Type? _pot;

    public static bool Enabled { get; set; } = true;

    // Unverified bodies, survival missing or another mod's patch: the engine draws
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Counting.Hud totals (main thread)
    public static long Culled => Counts.Skipped;
    public static long Drawn => Counts.Shown;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        _pot = AccessTools.TypeByName(Pot);
        if (!NotNull(harmony) || seams[0] is null || seams[1] is null ||
            !Guard.Match(seams, fingerprint)) return;
        _ = harmony.Patch(seams[0], new HarmonyMethod(AccessTools.Method(typeof(HearthCulling), nameof(FirepitFrame))));
        _ = harmony.Patch(seams[1], new HarmonyMethod(AccessTools.Method(typeof(HearthCulling), nameof(ForgeFrame))));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        Type?[] types = [AccessTools.TypeByName(Firepit), AccessTools.TypeByName(Forge)];
        return
        [
            types[0] is { } firepit ? AccessTools.DeclaredMethod(firepit, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) : null,
            types[1] is { } forge ? AccessTools.DeclaredMethod(forge, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) : null
        ];
    }

    // Draws when its mesh or a pot is there and could show; nothing to draw, or a mod's content renderer: as the engine
    internal static bool FirepitFrame(object? ___meshref, ICoreClientAPI ___api, BlockPos ___pos, object? ___contentStackRenderer,
        EnumRenderStage stage)
    {
        if (!Enabled || Blocked || stage != EnumRenderStage.Opaque || !NotNull(___api)) return true;
        var content = ___contentStackRenderer;
        if ((content is null && ___meshref is null) || (content is not null && content.GetType() != _pot)) return true;
        if (Visible(___api, ___pos, Reach, Counts)) return true;
        Leave(___api.Render, LeftState.Kind.Pot);
        return false;
    }

    // Draws when there is a work item or fuel and it could show
    internal static bool ForgeFrame(ItemStack? ___stack, float ___fuelLevel, ICoreClientAPI ___capi, BlockPos ___pos,
        EnumRenderStage stage)
    {
        if (!Enabled || Blocked || stage != EnumRenderStage.Opaque || !NotNull(___capi) || (___stack is null && ___fuelLevel <= 0f) ||
            Visible(___capi, ___pos, 0f, Counts)) return true;
        Leave(___capi.Render, LeftState.Kind.Forge);
        return false;
    }

    // The cell's sphere, reach blocks wider, may lie in the Opaque clip volume (true when unsure)
    internal static bool Visible(ICoreClientAPI? capi, BlockPos? pos, float reach, Tally counts)
    {
        if (pos is null || capi?.Render is not { } render ||
            render.DefaultFrustumCuller is not { } culler) return true;
        var radius = Cell + reach + AnimatableCulling.Warp(render.ShaderUniforms);
        var shown = !Finite(radius) || culler.SphereInFrustum(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, radius);
        if (Counting.Hud && shown) counts.Shown++;
        else if (Counting.Hud) counts.Skipped++;
        return shown;
    }

    // Pot: culling off and standard blending (a firepit's and its pot's); Forge: culling off, blending untouched
    internal static void Leave(IRenderAPI render, LeftState.Kind kind)
    {
        if (LeftState.Holds(kind)) return;
        render.GlDisableCullFace();
        if (kind == LeftState.Kind.Pot) render.GlToggleBlend(true, EnumBlendMode.Standard);
        LeftState.Left(kind);
    }

    // Counting.Hud totals of one culling's draws (main thread); PotCulling keeps its own
    internal sealed class Tally
    {
        public long Skipped, Shown;
    }
}

// PotInFirepitRenderer (survival) draws pot and wobbling lid in Opaque every frame without frustum test: a
// standard-shader setup and draw each (a tenth of the main thread in a kitchen base). Skipped when the bounding sphere
// lies outside the Opaque clip volume (HearthCulling's test): pot fills its cell, lid sits 13/32 up and wobbles 5/16
// and 1/60 turn (Lid), plus Warp. Skipped draws leave GL state as drawn: culling off, blending on, shader stopped.
internal static class PotCulling
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0xE7ED08273DA67B79UL;

    private const string Renderer = "Vintagestory.GameContent.PotInFirepitRenderer";
    // The lid's reach beyond the cell, generous: too far costs a draw, too tight a hole
    private const float Lid = 1.25f;

    private static readonly SeamGuard Guard = new(nameof(PotCulling), PatchKinds.Replacing, [typeof(PotCulling)]);
    private static readonly HearthCulling.Tally Counts = new();

    public static bool Enabled { get; set; } = true;

    // Unverified body, survival missing or another mod's patch: engine draws
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Counting.Hud totals (main thread)
    public static long Culled => Counts.Skipped;
    public static long Drawn => Counts.Shown;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || seams[0] is null || !Guard.Match(seams, fingerprint)) return;
        _ = harmony.Patch(seams[0], new HarmonyMethod(Frame));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Renderer);
        return [type is null ? null : AccessTools.DeclaredMethod(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])];
    }

    internal static bool Frame(BlockPos ___pos, ICoreClientAPI ___capi, EnumRenderStage stage)
    {
        if (!Enabled || Blocked || stage != EnumRenderStage.Opaque || ___pos is null ||
            ___capi?.Render is not { } render || HearthCulling.Visible(___capi, ___pos, Lid, Counts)) return true;
        HearthCulling.Leave(render, LeftState.Kind.Pot);
        return false;
    }
}

// GL state a skipped draw leaves (AnimatableCulling, PotCulling, HearthCulling), set once per run of skips: GlTap bumps
// Version on every state call, so an unchanged version needs no GL calls. Untapped: every call made.
internal static class LeftState
{
    // AnimatableCulling's with or without face culling. PotCulling's and a firepit's leave culling off and standard
    // blending, a forge's culling off with blending as it was
    public enum Kind
    {
        None,
        Culling,
        Unculled,
        Pot,
        Forge
    }

    private static long _version = -1;
    private static Kind _state;

    public static bool Holds(Kind state) => Links.Tapped() && _state == state && Links.TapVersion() == _version;

    public static void Left(Kind state) => (_state, _version) = (state, Links.Tapped() ? Links.TapVersion() : -1);
}

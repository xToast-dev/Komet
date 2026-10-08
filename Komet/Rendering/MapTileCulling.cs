using System.Reflection;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// ChunkMapLayer (essentials mod) draws every map tile it holds whenever a map shows, the minimap included: at a view distance of
// 1536 a thousand 96x96 tiles a frame, all but a dozen outside the map's scissor box. Under Vulkan each one sampled needs a copy
// of its own (exported: OpenGL draws the tiles) that every handoff names, so joining a world, while the tiles stream in, falls to a
// few frames a second. The prefix skips the draw of a tile whose rectangle, worked out as MultiChunkMapComponent.Render places it,
// lies wholly outside the map element's inner bounds (PushScissor's box), with a pixel to spare for the engine's rounding.
//
// A skipped draw leaves the gui shader's uniforms as the last drawn tile left them; the layers after set their own.
internal static class MapTileCulling
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xF2927D95A8831334UL;

    private const string Component = "Vintagestory.GameContent.MultiChunkMapComponent",
        Map = "Vintagestory.GameContent.GuiElementMap";
    private const double Slack = 1;

    private static MethodBase?[] _seams = [];
    private static AccessTools.FieldRef<object, Cuboidd>? _view;
    private static AccessTools.FieldRef<object, float>? _zoom;
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // Body not verified, essentials mod missing or another mod patches it: every tile is drawn
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _seams, _logger, _foreign) = (false, true, Seams(), logger, false);
        if (!NotNull(harmony) || !Assert(_seams.Length == 1) || _seams[0] is null ||
            AccessTools.TypeByName(Map) is not { } map) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(MapTileCulling), logger);
        if (!_shaped) return;
        (_view, _zoom) = (AccessTools.FieldRefAccess<Cuboidd>(map, "CurrentBlockViewBounds"),
            AccessTools.FieldRefAccess<float>(map, "ZoomLevel"));
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Render)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == 1);
        _foreign = EngineShape.Report(_logger, nameof(MapTileCulling), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.Replacing, null, typeof(MapTileCulling)));
        Blocked = !seamed || _foreign;
    }

    internal static MethodBase?[] Seams()
    {
        var (type, map) = (AccessTools.TypeByName(Component), AccessTools.TypeByName(Map));
        MethodBase?[] seams =
            [type is null || map is null ? null : AccessTools.DeclaredMethod(type, "Render", [map, typeof(float)])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Render(GuiElement map, LoadedTexture? ___Texture, Vec3d? ___worldPos)
    {
        if (!Enabled || Blocked || _view is null || _zoom is null || ___Texture is null || ___worldPos is null ||
            map?.Bounds is not { } bounds || _view(map) is not { } view) return true;
        var (width, height, zoom) = (view.X2 - view.X1, view.Z2 - view.Z1, _zoom(map));
        if (!double.IsFinite(width + height + zoom + bounds.renderX + bounds.renderY) || !(width > 0 && height > 0)) return true;
        var x = (int)(bounds.renderX + (float)((___worldPos.X - view.X1) / width * bounds.InnerWidth));
        var y = (int)(bounds.renderY + (float)((___worldPos.Z - view.Z1) / height * bounds.InnerHeight));
        var (right, bottom) = (x + (int)(___Texture.Width * zoom), y + (int)(___Texture.Height * zoom));
        _ = Assert(right >= x) && Assert(bottom >= y);
        return right >= bounds.renderX - Slack && x <= bounds.renderX + bounds.InnerWidth + Slack &&
               bottom >= bounds.renderY - Slack && y <= bounds.renderY + bounds.InnerHeight + Slack;
    }
}

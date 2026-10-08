using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;

namespace Komet.Rendering;

// A map tile (MultiChunkMapComponent, essentials mod) is a 96x96 texture of 3x3 chunk pieces. FinishSetChunks, a game tick listener
// on the render thread, writes each new piece by uploading it to a shared 32x32 texture and drawing that into the tile through a
// framebuffer of its own, binding the primary framebuffer back after every piece: while chunks stream in, up to 200 pieces a tick
// and a quarter of the render thread (tick-gtentity in the frame profile), each draw a separate render pass for the driver and,
// under Vulkan, a segment of its own. The draw is a texel-exact copy: the quad's UVs run with its corners (v = 0 at the bottom, as
// the upload's first row), the target is the piece's 32x32 cell at (32 * dx, 32 * dz), nearest sampling at scale 1, and with every
// pixel opaque the alpha test passes and standard blending writes the source. The prefix uploads such pieces straight into their cell
// (glTexSubImage2D, the same format and rows), then makes the mipmaps as the engine does; a piece with any pixel not opaque, or not
// 32x32, leaves the whole tile to the engine.
//
// Skipped against the engine's body: the framebuffer, the draws and the state they leave (primary framebuffer bound, depth test and
// blending on), which every render stage after the game tick sets for itself.
internal static class MapTileUpload
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xAEBCE3250C8B0D68UL;

    private const string Component = "Vintagestory.GameContent.MultiChunkMapComponent";
    private const int Side = 32, Cells = 3, Tile = Side * Cells, Opaque = unchecked((int)0xFF000000);

    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // Body not verified, essentials mod missing or another mod patches it: the engine draws every piece
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _seams, _logger, _foreign) = (false, true, Seams(), logger, false);
        if (!NotNull(harmony) || !Assert(_seams.Length == 1) || _seams[0] is null) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(MapTileUpload), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Finish)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == 1);
        _foreign = EngineShape.Report(_logger, nameof(MapTileUpload), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.Replacing, null, typeof(MapTileUpload)));
        Blocked = !seamed || _foreign;
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Component);
        MethodBase?[] seams = [type is null ? null : AccessTools.DeclaredMethod(type, "FinishSetChunks", [])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Finish(ref LoadedTexture? ___Texture, ref int[]?[]? ___pixelsToSet, int[]? ___emptyPixels,
        ICoreClientAPI? ___capi)
    {
        if (!Enabled || Blocked || ___pixelsToSet is not { Length: Cells * Cells } pieces || ___capi?.Render is not { } render ||
            !Uploadable(pieces)) return true;
        if (___Texture is null || ___Texture.Disposed)
        {
            if (___emptyPixels is null) return true;
            ___Texture = new LoadedTexture(___capi, 0, Tile, Tile);
            render.LoadOrUpdateTextureFromRgba(___emptyPixels, false, 0, ref ___Texture);
        }

        if (!Assert(___Texture.Width == Tile) || !Assert(___Texture.Height == Tile)) return true;
        render.BindTexture2d(___Texture.TextureId);
        for (var i = 0; i < Cells * Cells; i++)
            if (pieces[i] is { } pixels)
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, Side * (i % Cells), Side * (i / Cells), Side, Side,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                World.MapPixels.Release(pixels); // OpenGL copied it
            }
        render.GlGenerateTex2DMipmaps();
        ___pixelsToSet = null;
        return false;
    }

    // Every piece 32x32 and every pixel opaque: the engine's draw copies it texel for texel
    internal static bool Uploadable(int[]?[] pieces)
    {
        for (var i = 0; i < Math.Min(pieces.Length, Cells * Cells); i++)
        {
            if (pieces[i] is not { } pixels) continue;
            if (pixels.Length != Side * Side) return false;
            for (var p = 0; p < Side * Side; p++)
                if ((pixels[p] & Opaque) != Opaque)
                    return false;
        }

        return true;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// The JSON slot (8): JsonMesher draws the plain blocks, the engine's JsonTesselator it replaced every other one. Its public field
// jsonTesselator stays the engine's, so AddDecor and the bleeding tesselator still reach the engine's instance.
internal sealed class OwnJson(IBlockTesselator engine) : IBlockTesselator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        _ = Assert(engine is not OwnJson);
        var ours = NotNull(vars) && vars.block is { } block && JsonMesher.Tesselate(vars, block, EnumDrawType.JSON);
        OwnTessellation.Count(ours, true);
        if (!ours) engine.Tesselate(vars);
    }
}

// The JSONAndSnowLayer slot (17), JsonAndSnowLayerTesselator: where the block below allows snow cover, a cube of 1/8 height under the
// block in the opaque pass, with the snow texture (fastBlockTextureSubidsByFace[6]), at the cell's x and z without random offset, its
// flags without wind and z offset and colour map 0; then the block itself as a JSON block - JsonMesher's where it takes it, else the
// engine's own JsonTesselator inside JsonAndSnowLayerTesselator, as the engine would have gone on. The snow faces are drawn as
// CubeTesselator.DrawBlockFace draws them (OwnQuads), down to the AddVertexWithFlagsSkipColor it reaches, which only the cube path
// watches: while either path has stood down the whole block goes to the engine. Everything the snow needs is checked before the block
// below is asked; where something is missing the whole block goes to the engine's tesselator, which then asks it.
internal sealed class OwnJsonSnow(JsonAndSnowLayerTesselator engine) : IBlockTesselator
{
    private const float Height = 0.125f;
    private const int SnowFlags = 0x1FFFFFF & ~0x700; // the engine's & 0x1FFFFFF & -1793: no wind, no z offset

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "json")]
    private static extern ref IBlockTesselator? Inner(JsonAndSnowLayerTesselator tesselator);

    // JsonAndSnowLayerTesselator's own JSON tesselator, the field the accessor reads: there and of the type it has in 1.22.7
    internal static bool Fields(ILogger? logger)
    {
        var field = AccessTools.DeclaredField(typeof(JsonAndSnowLayerTesselator), "json");
        if (field?.FieldType == typeof(IBlockTesselator)) return Assert(!field.IsStatic);
        logger?.Warning("Komet OwnTessellation: JsonAndSnowLayerTesselator.json is missing or of another type, the engine draws " +
                        "the JSON blocks with snow");
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        var block = NotNull(vars) ? vars.block : null;
        var faces = block is null ? 0 : vars.drawFaceFlags & OwnQuads.AllFaces;
        if (!OwnTessellation.Active || !OwnTessellation.JsonActive || block is null || Inner(engine) is not { } json ||
            !Snow(vars, faces, out var rgbs, out var below, out var game, out var snow))
        {
            OwnTessellation.Count(false);
            engine.Tesselate(vars);
            return;
        }

        var at = JsonMesher.Below(vars.dimension, vars.posX, vars.posY - 1, vars.posZ);
        if (below.AllowSnowCoverage(game, at)) Draw(vars, block, faces, rgbs, snow.Position, snow.Mesh);
        var ours = JsonMesher.Tesselate(vars, block, EnumDrawType.JSONAndSnowLayer);
        OwnTessellation.Count(ours, true);
        if (!ours) json.Tesselate(vars);
    }

    // The block below, the world it is asked in, the snow's atlas position and its pool mesh at Opaque lod 1: false, with nothing
    // written, for the engine
    private static bool Snow(TCTCache vars, int faces, [NotNullWhen(true)] out int[]? rgbs, [NotNullWhen(true)] out Block? below,
        [NotNullWhen(true)] out ClientMain? game, out Snowed snow)
    {
        (below, game, snow) = (null, Game(vars.tct), default);
        if (!OwnQuads.Ready(vars, faces, out rgbs) || BlocksExt(vars.tct) is not { Length: ExtCells } blocks ||
            game is null || vars.fastBlockTextureSubidsByFace is not { Length: > Faces } fast ||
            vars.textureAtlasPositionsByTextureSubId is not { } positions ||
            OwnQuads.Pools(vars.tct, EnumChunkRenderPass.Opaque) is not { } pools ||
            !OwnQuads.Take(positions, pools, fast[Faces], out var position, out var mesh)) return false;
        var under = vars.extIndex3d + Moves[Down];
        if (!Index(under, ExtCells)) return false; // Ready: a cell of the chunk
        (below, snow) = (blocks[under], new Snowed(position, mesh));
        return NotNull(below);
    }

    // The snow cube's drawn faces, each lit just before it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Draw(TCTCache vars, Block block, int faces, int[] rgbs, TextureAtlasPosition position, MeshData mesh)
    {
        var (quads, corners, e) = (vars.blockFaceVertices, vars.CurrentLightRGBByCorner, vars.extIndex3d);
        var (flags, x, y, z) = (vars.VertexFlags & SnowFlags, (float)vars.lx, vars.finalY, (float)vars.lz);
        _ = Assert(quads.Length >= Faces) && Assert(corners.Length >= OwnQuads.Corners); // Ready checked both
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            OwnQuads.Light(vars, block, i, e + Moves[i], rgbs, corners);
            OwnQuads.Face(vars, i, quads[i], position, mesh, flags | BlockFacing.ALLFACES[i].NormalPackedFlags, 0, Height,
                x, y, z);
        }
    }

    private readonly record struct Snowed(TextureAtlasPosition Position, MeshData Mesh);
}

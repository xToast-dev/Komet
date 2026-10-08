using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// The engine's cube tesselators: BleedingCubeTesselator draws every Cube block (rock, ores, soil without grass, logs, ice - 1719 vanilla
// variants, most of what the terrain shows), CubeTesselator the layers (snow, ember) and the Transparent cubes. In the world-join traces
// BleedingCubeTesselator alone took 11-14 % of the tessellation time: per face the Harmony entry of CalcBlockFaceLight, three bleed
// helpers, four AddVertexWithFlags with their capacity check, bounds checks and pinned colour write, Add4, AddQuadIndices and two
// UpdateChunkMinMax. This one first chooses every drawn face's texture and pool mesh, writing nothing, and hands the block to the
// engine's tesselator it replaced wherever the engine would read anything else or fault: a block that may receive bleed (none in
// vanilla), tiled textures, a texture or mesh the engine would not find. Then it lights and writes the faces in the engine's order with
// the engine's float operations (OwnQuads).
//
// The two differ in the texture: BleedingCubeTesselator takes an alternate only where the block's variants have the face, falls back to
// the face's own texture for an id <= 0 and reads the atlas positions live from the atlas manager; CubeTesselator takes the variant as
// it is and reads the pass's snapshot. The height (the layers') scales every y and the sides' share of the texture.
internal sealed class OwnCube(IBlockTesselator engine, float height, bool bleeding) : OwnSlot(engine)
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override bool Draw(TCTCache vars, Block block)
    {
        OwnQuads.Chosen chosen = default;
        var faces = vars.drawFaceFlags & OwnQuads.AllFaces;
        _ = NotNull(block); // OwnSlot's
        if (faces == 0 || !Finite(height) || !OwnQuads.Ready(vars, faces, out var rgbs) ||
            !Choose(vars, block, faces, ref chosen)) return false;
        Draw(vars, block, faces, rgbs, ref chosen);
        return true;
    }

    // Each drawn face's atlas position and pool mesh as the engine picks them; false, with nothing written, where it has to run
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Choose(TCTCache vars, Block block, int faces, ref OwnQuads.Chosen chosen)
    {
        var (tct, fast) = (vars.tct, vars.fastBlockTextureSubidsByFace);
        if (block.HasTiles || (bleeding && block.CanReceiveBleed) || fast is not { Length: >= Faces }) return false;
        var pools = OwnQuads.Pools(tct, vars.RenderPass);
        var positions = bleeding
            ? Game(tct)?.BlockAtlasManager?.TextureAtlasPositionsByTextureSubId
            : vars.textureAtlasPositionsByTextureSubId;
        var variants = block.HasAlternates ? block.FastTextureVariants : null;
        if (!NotNull(pools) || !NotNull(positions) || (block.HasAlternates && variants is null && !bleeding)) return false;
        var hash = variants is null ? 0 : GameMath.MurmurHash3(vars.posX, vars.posY, vars.posZ);
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            var id = OwnQuads.Texture(variants, fast, i, hash, bleeding);
            if (id == OwnQuads.Unknown) return false;
            if (bleeding && id <= 0) id = fast[i];
            if (!OwnQuads.Take(positions, pools, id, out var position, out var mesh)) return false;
            (chosen.Positions[i], chosen.Meshes[i]) = (position, mesh);
        }

        return true;
    }

    // CubeTesselator.DrawBlockFace for every drawn face, each lit just before it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Draw(TCTCache vars, Block block, int faces, int[] rgbs, ref OwnQuads.Chosen chosen)
    {
        var (quads, corners, e) = (vars.blockFaceVertices, vars.CurrentLightRGBByCorner, vars.extIndex3d);
        var (flags, colorMap) = (vars.VertexFlags, vars.ColorMapData.Value);
        _ = Assert(quads.Length >= Faces) && Assert(corners.Length >= OwnQuads.Corners); // Ready checked both
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            OwnQuads.Light(vars, block, i, e + Moves[i], rgbs, corners);
            var (position, mesh) = (chosen.Positions[i], chosen.Meshes[i]);
            if (!NotNull(position) || !NotNull(mesh)) continue; // Choose took every drawn face
            OwnQuads.Face(vars, i, quads[i], position, mesh, flags | BlockFacing.ALLFACES[i].NormalPackedFlags, colorMap,
                height, vars.finalX, vars.finalY, vars.finalZ);
        }
    }
}

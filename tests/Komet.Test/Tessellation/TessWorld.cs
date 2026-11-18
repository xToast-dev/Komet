using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// The extra block ids a tessellation world uses past ChunkRig's own; Plant is -1 where the world has no plant block
internal sealed record TessBlocks(
    int Soil,
    int Grass,
    int SnowCube,
    int Layer,
    int Json,
    int JsonSnow,
    int Leaves,
    int Cross,
    int Turned,
    int Plant = -1);

// The shared preparation of OwnTessellationGoldenTests and TessellationSynth: the game's tesselators in their slots on fresh pools,
// 7 texture ids a block, an atlas of 8 distinct positions, random JSON models
internal static class TessWorld
{
    private const int Positions = 8, Slots = 40;
    private static readonly int[] OneAtlas = [0];

    public static ChunkRig Prepare(ChunkRig rig, TessBlocks ids, bool ao, ShapeTesselatorManager shapes)
    {
        var (game, blocks) = (rig.Game, rig.Blocks);
        foreach (var id in (int[])[ids.Soil, ids.Grass])
        {
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].Textures) = (EnumDrawType.TopSoil,
                EnumChunkRenderPass.TopSoil,
                new Dictionary<string, CompositeTexture>
                    { ["snowed"] = new() { Baked = new BakedCompositeTexture { TextureSubId = 7 } } });
        }

        (blocks[ChunkRig.Glass].DrawType, blocks[ids.SnowCube].BlockMaterial) =
            (EnumDrawType.Transparent, EnumBlockMaterial.Snow);
        (blocks[ids.Layer].DrawType, blocks[ids.Layer].SideOpaque, blocks[ids.Layer].SideAo) =
            (EnumDrawType.BlockLayer_3, new SmallBoolArray(1 << 5), new SmallBoolArray(0));
        foreach (var id in (int[])[ids.Cross, ids.Turned])
        {
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].AllSidesOpaque) =
                (EnumDrawType.Cross, EnumChunkRenderPass.OpaqueNoCull, false);
        }

        var turned = blocks[ids.Turned];
        (turned.RandomizeRotations, turned.RandomizeAxes, turned.DoNotRenderAtLod2) = (true, EnumRandomizeAxes.XYZ, true);
        // The leaves are see-through JSON (a flags writer in their place already is)
        (blocks[ids.Leaves].DrawType, blocks[ids.Leaves].AllSidesOpaque, blocks[ids.Leaves].BlockMaterial) =
            (EnumDrawType.JSON, false, EnumBlockMaterial.Leaves);
        foreach (var id in (int[])[ChunkRig.Granite, ids.Grass, ids.Layer, ids.Cross])
        {
            (blocks[id].HasAlternates, blocks[id].FastTextureVariants) = (true, Variants());
        }

        (blocks[ChunkRig.Merge].RandomDrawOffset, blocks[ids.Grass].ShapeUsesColormap) = (1, true);
        JsonBlocks(blocks, ids);
        foreach (var b in blocks)
        {
            (b.VertexFlags, b.EmitSideAo) = (new VertexFlags(b.BlockId), b.AllSidesOpaque ? (byte)63 : (byte)0);
        }

        blocks[ids.Json].VertexFlags.WindMode = EnumWindBitMode.NormalWind;
        blocks[ids.JsonSnow].VertexFlags.WindMode = EnumWindBitMode.Leaves;
        blocks[ids.Leaves].VertexFlags.WindMode = EnumWindBitMode.Leaves;
        if (ids.Plant >= 0) blocks[ids.Plant].VertexFlags.WindMode = EnumWindBitMode.NormalWind;
        blocks[ids.Cross].VertexFlags.WindMode = EnumWindBitMode.NormalWind;
        blocks[ids.Turned].VertexFlags.WindMode = EnumWindBitMode.WeakWind;
        game.FastBlockTextureSubidsByBlockAndFace =
            [.. blocks.Select(b => Enumerable.Range(b.BlockId, 7).Select(f => f % 7).ToArray())];
        ChunkRig.Set(game.BlockAtlasManager, "TextureAtlasPositionsByTextureSubId", Enumerable.Range(0, Positions)
            .Select(i => new TextureAtlasPosition { x1 = i / 8f, x2 = (i + 1) / 8f, y1 = i / 16f, y2 = 1 - i / 16f })
            .ToArray());
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        ChunkRig.Set(platform, "uptimeStopWatch", Stopwatch.StartNew());
        (game.Platform, rig.Map.MapChunkSize) = (platform, ChunkRig.Size);
        var tesselators = new IBlockTesselator[Slots];
        for (var layer = 1; layer <= 7; layer++)
        {
            tesselators[layer] = new CubeTesselator(0.125f * layer);
        }

        tesselators[(int)EnumDrawType.Cube] = new BleedingCubeTesselator(1f);
        tesselators[(int)EnumDrawType.Transparent] = new CubeTesselator(1f);
        tesselators[(int)EnumDrawType.TopSoil] = new TopsoilTesselator();
        tesselators[(int)EnumDrawType.Cross] = new CrossTesselator();
        tesselators[(int)EnumDrawType.JSON] = new JsonTesselator();
        tesselators[(int)EnumDrawType.JSONAndSnowLayer] = new JsonAndSnowLayerTesselator();
        ChunkRig.Set(rig.Tesselator, "blockTesselators", tesselators);
        ((TCTCache)ChunkRig.Get(rig.Tesselator, "vars")).shapes = shapes;
        var (api, render) = ((ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI)),
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi"));
        render.SetValue(api, RuntimeHelpers.GetUninitializedObject(render.FieldType)); // AddJsonModelDataToMesh reads useSSBOs
        game.api = api;
        ChunkRig.Set(rig.Tesselator, "AoAndSmoothShadows", ao);
        ChunkRig.Set(rig.Tesselator, "regionSize", 16 * ChunkRig.Size); // the colour map's climate lookup
        _ = AccessTools.Method(typeof(ChunkTesselator), "UpdateForAtlasses").Invoke(rig.Tesselator, [OneAtlas]);
        return rig;
    }

    // The JSON blocks: one opaque with a colour map, frost, alternates by x, y and z; one with snow under it, see-through, in the
    // OpaqueNoCull pass, offset at random, collapsing into its material and drawn at lod 2
    private static void JsonBlocks(Block[] blocks, TessBlocks ids)
    {
        var (json, snow) = (blocks[ids.Json], blocks[ids.JsonSnow]);
        (json.DrawType, json.ShapeUsesColormap, json.Frostable, json.ExtraColorBits, json.RandomizeAxes) =
            (EnumDrawType.JSON, true, true, 5, EnumRandomizeAxes.XYZ);
        (snow.DrawType, snow.RenderPass, snow.RandomDrawOffset, snow.FaceCullMode, snow.DoNotRenderAtLod2) =
            (EnumDrawType.JSONAndSnowLayer, EnumChunkRenderPass.OpaqueNoCull, 1, EnumFaceCullMode.CollapseMaterial,
                true);
        (snow.AllSidesOpaque, snow.Lod0Shape) = (false, new CompositeShape());
    }

    // The models: the default and three lod 1 alternates of the first JSON block and its lod 2 model, the second one's default and
    // lod 0, the leaves' (and plant's) defaults and two lod 1 alternates of the leaves
    public static ShapeTesselatorManager Shapes(Random r, Block[] blocks, TessBlocks ids)
    {
        var shapes = (ShapeTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ShapeTesselatorManager));
        (shapes.blockModelDatas, shapes.altblockModelDatasLod0, shapes.altblockModelDatasLod1,
                shapes.altblockModelDatasLod2) =
            (new MeshData[blocks.Length], new MeshData[blocks.Length][], new MeshData[blocks.Length][],
                new MeshData[blocks.Length][]);
        (shapes.blockModelDatas[ids.Json], shapes.blockModelDatas[ids.JsonSnow]) =
            (Model(r, 14, true), Model(r, 10, false));
        shapes.altblockModelDatasLod1[ids.Json] = [Model(r, 14, true), Model(r, 9, true), Model(r, 20, true)];
        (blocks[ids.Json].Lod2Mesh, blocks[ids.JsonSnow].Lod0Mesh) = (Model(r, 6, true), Model(r, 8, false));
        shapes.blockModelDatas[ids.Leaves] = Model(r, 12, true);
        if (ids.Plant >= 0) shapes.blockModelDatas[ids.Plant] = Model(r, 8, false);
        shapes.altblockModelDatasLod1[ids.Leaves] = [Model(r, 12, true), Model(r, 7, false)];
        return shapes;
    }

    // Quads facing none or one of the six sides, every third one inside the block, the rest on its side (and culled there where the
    // side is hidden, unless a coordinate leaves the block); some DisableRandoms or Liquid faces where the model has passes
    private static MeshData Model(Random r, int faces, bool passes)
    {
        float[] steps = [0f, 0.25f, 0.5f, 1f, 1.25f];
        var mesh = new MeshData(4 * faces, 6 * faces);
        for (var l = 0; l < faces; l++)
        {
            var facing = l % 7 - 1;
            var axis = facing < 0 ? 1 : JsonMesh.FaceCoord[facing];
            var plane = facing is 1 or 2 or 4 ? 1f : 0f;
            if (l % 3 == 0) plane = 0.5f;

            for (var k = 0; k < 4; k++)
            {
                var p = Enumerable.Range(0, 3).Select(c => c == axis ? plane : steps[r.Next(steps.Length)]).ToArray();
                mesh.AddVertexWithFlags(p[0], p[1], p[2], r.NextSingle(), r.NextSingle(), 0,
                    (r.Next(4) << 25) | r.Next(256));
            }

            mesh.AddIndices(4 * l, 4 * l + 1, 4 * l + 2, 4 * l, 4 * l + 2, 4 * l + 3);
            mesh.AddXyzFace((byte)(facing + 1));
            mesh.AddTextureId(0);
            mesh.AddColorMapIndex((byte)r.Next(4), (byte)r.Next(4), r.Next(2) == 0);
            if (passes) mesh.AddRenderPass((l % 5) switch { 0 => 1024, 4 => 4, _ => 0 });
        }

        return mesh;
    }

    // Two or three alternates a face, one of texture id 0 (BleedingCubeTesselator then takes the face's own), none for the down face
    private static BakedCompositeTexture[]?[] Variants() =>
        [
            .. Enumerable.Range(0, 6).Select(f => f == 5
                ? null
                : Enumerable.Range(0, 2 + f % 2).Select(k => new BakedCompositeTexture
                        { TextureSubId = (f + 3 * k) % Positions })
                    .ToArray())
        ];
}

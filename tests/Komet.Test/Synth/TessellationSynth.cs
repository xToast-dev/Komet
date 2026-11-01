using Komet.Tessellation;
using Komet.Test.Tessellation;

namespace Komet.Test.Synth;

// Chunk tessellation as the tessellation thread runs it: ChunkTesselator.NowProcessChunk on the centre of a 3x3x3 neighbourhood
// (ChunkRig with the game's block tesselators, as OwnTessellationGoldenTests prepares them, smooth shadows on), its parts disposed
// afterwards as the upload does. Worlds: a surface chunk (topsoil, layers, JSON blocks, crosses, leaves, caves below), an
// underground chunk (stone, granite and ores with cave pockets) and solid rock (no face at all).
[Category(Synth.Category)]
[NonParallelizable]
public sealed class TessellationSynth
{
    private const int Soil = ChunkRig.Count, Grass = Soil + 1, SnowCube = Soil + 2, Layer = Soil + 3, Json = Soil + 4,
        JsonSnow = Soil + 5, Leaves = Soil + 6, Cross = Soil + 7, Turned = Soil + 8, Ores = Soil + 9, Extra = 14;

    private const int Positions = 8, Slots = 40;
    private static readonly int[] OneAtlas = [0];
    private static readonly MethodInfo DisposePart = AccessTools.Method(typeof(TesselatedChunkPart), "Dispose");

    [Test]
    public void FaceLightSurface() => Tessellate("FaceLight", "surface", Surface);

    [Test]
    public void FaceLightUnderground() => Tessellate("FaceLight", "underground", Underground);

    [Test]
    public void OccludedChunksRock() => Tessellate("OccludedChunks", "rock", static (_, _, _, _) => ChunkRig.Stone);

    [Test]
    public void OccludedChunksUnderground() => Tessellate("OccludedChunks", "underground", Underground);

    // The neighbourhood packed again after every pass, as SystemCompressChunks packs chunks idle for 8 s: the engine's Unpack()
    // drops the packed bytes and marks the chunk changed, so every pack compresses anew. The centre is unpacked read-only first,
    // as the shipped TesselateChunk does.
    [Test]
    public void NeighbourReadsSurface() => Tessellate("NeighbourReads", "surface packed", Surface, true);

    [Test]
    public void NeighbourReadsUnderground() => Tessellate("NeighbourReads", "underground packed", Underground, true);

    // Komet's cube, topsoil, cross and JSON tesselators against the game's in the same slots: swapped in while the feature is
    // installed (as ChunkTesselator.Start swaps them in game), the game's put back for the off arm
    [Test]
    public void OwnTessellationSurface() => Tessellate("OwnTessellation", "surface", Surface);

    [Test]
    public void OwnTessellationUnderground() => Tessellate("OwnTessellation", "underground", Underground);

    private static void Tessellate(string feature, string scenario, System.Func<Random, int, int, int, int> pick,
        bool packed = false)
    {
        using var rig = World(pick);
        var own = feature == "OwnTessellation" ? OwnSlots(rig.Tesselator) : null;
        var chunks = new List<ClientChunk>();
        for (var c = 0; c < 27; c++) chunks.Add(rig.At(c % 3, c / 9, c / 3 % 3)!);
        TessParallelTests.Digest? last = null;
        _ = Synth.Run(feature, scenario, ops =>
        {
            own?.Invoke();
            for (var i = 0; i < ops; i++)
            {
                if (packed) _ = chunks[13].Unpack_ReadOnly();
                var tess = rig.Tess(1, 1, 1);
                _ = rig.Tesselator.NowProcessChunk(1, 1, 1, tess, false);
                if (i == ops - 1) last = TessParallelTests.Digest.Of(tess);
                foreach (var parts in (TesselatedChunkPart[]?[])[ChunkRig.CenterParts(tess), ChunkRig.EdgeParts(tess)])
                    foreach (var part in parts ?? [])
                        _ = DisposePart.Invoke(part, null);
                if (!packed) continue;
                foreach (var chunk in chunks)
                {
                    chunk.Pack();
                    _ = chunk.TryCommitPackAndFree(0);
                }
            }
        }, 4, () => last);
    }

    // Komet's tesselators in the slots while OwnTessellation is installed, the game's put back once it is not
    private static Action OwnSlots(ChunkTesselator tesselator)
    {
        var slots = (IBlockTesselator?[])AccessTools.Field(typeof(ChunkTesselator), "blockTesselators").GetValue(tesselator)!;
        var engine = (IBlockTesselator?[])slots.Clone();
        var swapped = false;
        return () =>
        {
            if (OwnTessellation.Installed == swapped) return;
            if (swapped) engine.CopyTo(slots, 0);
            else _ = OwnTessellation.Swap(tesselator);
            swapped = !swapped;
        };
    }

    private static ChunkRig World(System.Func<Random, int, int, int, int> pick)
    {
        var rig = new ChunkRig(Extra);
        var r = new Random(20261101);
        for (var c = 0; c < 27; c++)
        {
            var (cx, cy, cz) = (c % 3, c / 9, c / 3 % 3);
            rig.Put(cx, cy, cz, (x, y, z) => pick(r, cx * 32 + x, cy * 32 + y, cz * 32 + z)).Lighting
                .FillWithSunlight((ushort)((c * 7 + 5) % 25));
        }

        rig.AddRegion(1, 1, 3);
        return Prepare(rig, Shapes(new Random(7), rig.Blocks));
    }

    // Ground at y 40..47 (the centre chunk's rows 8..15): topsoil, a layer, snow, leaves, crosses above, mixed blocks and caves below
    private static int Surface(Random r, int x, int y, int z)
    {
        var ground = 40 + TessMix.Hash(x, 0, z) % 8;
        if (y > ground + 4) return ChunkRig.Air;
        var tree = TessMix.Hash(x / 5, 2, z / 5) % 4 == 0 && TessMix.Hash(x, 3, z) % 3 != 0;
        if (y > ground + 1) return tree ? Leaves : ChunkRig.Air;
        if (y == ground + 1)
            return (TessMix.Hash(x, 1, z) % 9) switch
            {
                0 => SnowCube, 1 => Layer, 2 => JsonSnow, 3 => Leaves, 4 or 5 => Cross, 6 => Turned, _ => ChunkRig.Air
            };
        if (y == ground) return r.Next(4) == 0 ? Soil : Grass;
        if (y > ground - 4) return Soil;
        return Rock(r, x, y, z);
    }


    // Deep rock: stone and granite in veins, ores, cave pockets, now and then a JSON block (a crystal, a rock heap) on a cave floor
    private static int Underground(Random r, int x, int y, int z)
    {
        if (TessMix.Hash(x / 6, y / 4, z / 6) % 5 != 0 || TessMix.Hash(x, y, z) % 4 == 0) return Rock(r, x, y, z);
        return TessMix.Hash(x, y, z) % 23 == 0 ? Json : ChunkRig.Air;
    }

    private static int Rock(Random r, int x, int y, int z)
    {
        var h = TessMix.Hash(x, y + 7, z);
        if (h % 40 == 0) return Ores + h / 40 % (Extra - 9);
        if (h % 97 == 0) return ChunkRig.Air;
        return (x / 4 + y / 3 + z / 5 + r.Next(2)) % 3 == 0 ? ChunkRig.Granite : ChunkRig.Stone;
    }

    // OwnTessellationGoldenTests' preparation: the game's tesselators in their slots, 7 texture ids a block, an atlas of 8 positions
    private static ChunkRig Prepare(ChunkRig rig, ShapeTesselatorManager shapes)
    {
        var (game, blocks) = (rig.Game, rig.Blocks);
        foreach (var id in (int[])[Soil, Grass])
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].Textures) = (EnumDrawType.TopSoil,
                EnumChunkRenderPass.TopSoil,
                new Dictionary<string, CompositeTexture> { ["snowed"] = new() { Baked = new() { TextureSubId = 7 } } });
        (blocks[ChunkRig.Glass].DrawType, blocks[SnowCube].BlockMaterial) = (EnumDrawType.Transparent, EnumBlockMaterial.Snow);
        (blocks[Layer].DrawType, blocks[Layer].SideOpaque, blocks[Layer].SideAo) =
            (EnumDrawType.BlockLayer_3, new SmallBoolArray(1 << 5), new SmallBoolArray(0));
        foreach (var id in (int[])[Cross, Turned])
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].AllSidesOpaque) =
                (EnumDrawType.Cross, EnumChunkRenderPass.OpaqueNoCull, false);
        (blocks[Turned].RandomizeRotations, blocks[Turned].RandomizeAxes, blocks[Turned].DoNotRenderAtLod2) =
            (true, EnumRandomizeAxes.XYZ, true);
        (blocks[Leaves].DrawType, blocks[Leaves].AllSidesOpaque, blocks[Leaves].BlockMaterial) =
            (EnumDrawType.JSON, false, EnumBlockMaterial.Leaves);
        foreach (var id in (int[])[ChunkRig.Granite, Grass, Layer, Cross])
            (blocks[id].HasAlternates, blocks[id].FastTextureVariants) = (true, Variants());
        (blocks[ChunkRig.Merge].RandomDrawOffset, blocks[Grass].ShapeUsesColormap) = (1, true);
        var (json, snow) = (blocks[Json], blocks[JsonSnow]);
        (json.DrawType, json.ShapeUsesColormap, json.Frostable, json.ExtraColorBits, json.RandomizeAxes) =
            (EnumDrawType.JSON, true, true, 5, EnumRandomizeAxes.XYZ);
        (snow.DrawType, snow.RenderPass, snow.RandomDrawOffset, snow.FaceCullMode, snow.DoNotRenderAtLod2) =
            (EnumDrawType.JSONAndSnowLayer, EnumChunkRenderPass.OpaqueNoCull, 1, EnumFaceCullMode.CollapseMaterial, true);
        (snow.AllSidesOpaque, snow.Lod0Shape) = (false, new CompositeShape());
        foreach (var b in blocks)
            (b.VertexFlags, b.EmitSideAo) = (new VertexFlags(b.BlockId), b.AllSidesOpaque ? (byte)63 : (byte)0);
        (blocks[Json].VertexFlags.WindMode, blocks[JsonSnow].VertexFlags.WindMode) =
            (EnumWindBitMode.NormalWind, EnumWindBitMode.Leaves);
        (blocks[Leaves].VertexFlags.WindMode, blocks[Cross].VertexFlags.WindMode, blocks[Turned].VertexFlags.WindMode) =
            (EnumWindBitMode.Leaves, EnumWindBitMode.NormalWind, EnumWindBitMode.WeakWind);
        game.FastBlockTextureSubidsByBlockAndFace =
            [.. blocks.Select(b => Enumerable.Range(b.BlockId, 7).Select(f => f % 7).ToArray())];
        ChunkRig.Set(game.BlockAtlasManager, "TextureAtlasPositionsByTextureSubId", Enumerable.Range(0, Positions)
            .Select(i => new TextureAtlasPosition { x1 = i / 8f, x2 = (i + 1) / 8f, y1 = i / 16f, y2 = 1 - i / 16f })
            .ToArray());
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        ChunkRig.Set(platform, "uptimeStopWatch", Stopwatch.StartNew());
        (game.Platform, rig.Map.MapChunkSize) = (platform, ChunkRig.Size);
        var tesselators = new IBlockTesselator[Slots];
        for (var layer = 1; layer <= 7; layer++) tesselators[layer] = new CubeTesselator(0.125f * layer);
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
        render.SetValue(api, RuntimeHelpers.GetUninitializedObject(render.FieldType));
        game.api = api;
        ChunkRig.Set(rig.Tesselator, "AoAndSmoothShadows", true);
        ChunkRig.Set(rig.Tesselator, "regionSize", 16 * ChunkRig.Size);
        _ = AccessTools.Method(typeof(ChunkTesselator), "UpdateForAtlasses").Invoke(rig.Tesselator, [OneAtlas]);
        return rig;
    }

    private static ShapeTesselatorManager Shapes(Random r, Block[] blocks)
    {
        var shapes = (ShapeTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ShapeTesselatorManager));
        (shapes.blockModelDatas, shapes.altblockModelDatasLod0, shapes.altblockModelDatasLod1, shapes.altblockModelDatasLod2) =
            (new MeshData[blocks.Length], new MeshData[blocks.Length][], new MeshData[blocks.Length][],
                new MeshData[blocks.Length][]);
        (shapes.blockModelDatas[Json], shapes.blockModelDatas[JsonSnow]) = (Model(r, 14, true), Model(r, 10, false));
        shapes.altblockModelDatasLod1[Json] = [Model(r, 14, true), Model(r, 9, true), Model(r, 20, true)];
        (blocks[Json].Lod2Mesh, blocks[JsonSnow].Lod0Mesh) = (Model(r, 6, true), Model(r, 8, false));
        shapes.blockModelDatas[Leaves] = Model(r, 12, true);
        shapes.altblockModelDatasLod1[Leaves] = [Model(r, 12, true), Model(r, 7, false)];
        return shapes;
    }

    private static MeshData Model(Random r, int faces, bool passes)
    {
        float[] steps = [0f, 0.25f, 0.5f, 1f, 1.25f];
        var mesh = new MeshData(4 * faces, 6 * faces);
        for (var l = 0; l < faces; l++)
        {
            var facing = l % 7 - 1;
            var axis = facing < 0 ? 1 : Komet.Tessellation.JsonMesh.FaceCoord[facing];
            var plane = facing is 1 or 2 or 4 ? 1f : 0f;
            if (l % 3 == 0) plane = 0.5f;
            for (var k = 0; k < 4; k++)
            {
                var p = Enumerable.Range(0, 3).Select(c => c == axis ? plane : steps[r.Next(steps.Length)]).ToArray();
                mesh.AddVertexWithFlags(p[0], p[1], p[2], r.NextSingle(), r.NextSingle(), 0, (r.Next(4) << 25) | r.Next(256));
            }

            mesh.AddIndices(4 * l, 4 * l + 1, 4 * l + 2, 4 * l, 4 * l + 2, 4 * l + 3);
            mesh.AddXyzFace((byte)(facing + 1));
            mesh.AddTextureId(0);
            mesh.AddColorMapIndex((byte)r.Next(4), (byte)r.Next(4), r.Next(2) == 0);
            if (passes) mesh.AddRenderPass((l % 5) switch { 0 => 1024, 4 => 4, _ => 0 });
        }

        return mesh;
    }

    private static BakedCompositeTexture[]?[] Variants() =>
    [
        .. Enumerable.Range(0, 6).Select(f => f == 5
            ? null
            : Enumerable.Range(0, 2 + f % 2).Select(k => new BakedCompositeTexture { TextureSubId = (f + 3 * k) % Positions })
                .ToArray())
    ];
}

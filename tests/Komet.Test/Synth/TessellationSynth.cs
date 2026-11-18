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
    private const int Soil = ChunkRig.Count,
        Grass = Soil + 1,
        SnowCube = Soil + 2,
        Layer = Soil + 3,
        Json = Soil + 4,
        JsonSnow = Soil + 5,
        Leaves = Soil + 6,
        Cross = Soil + 7,
        Turned = Soil + 8,
        Ores = Soil + 9,
        Extra = 14;

    private static readonly TessBlocks Ids = new(Soil, Grass, SnowCube, Layer, Json, JsonSnow, Leaves, Cross, Turned);
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
        for (var c = 0; c < 27; c++)
        {
            chunks.Add(rig.At(c % 3, c / 9, c / 3 % 3)!);
        }

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
                {
                    foreach (var part in parts ?? [])
                    {
                        _ = DisposePart.Invoke(part, null);
                    }
                }

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
        var slots = (IBlockTesselator?[])AccessTools.Field(typeof(ChunkTesselator), "blockTesselators")
            .GetValue(tesselator)!;
        var engine = (IBlockTesselator?[])slots.Clone();
        var swapped = false;
        return () =>
        {
            if (OwnTessellation.Installed == swapped) return;

            if (swapped)
            {
                engine.CopyTo(slots, 0);
            }
            else
            {
                _ = OwnTessellation.Swap(tesselator);
            }

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
        return TessWorld.Prepare(rig, Ids, true, TessWorld.Shapes(new Random(7), rig.Blocks, Ids));
    }

    // Ground at y 40..47 (the centre chunk's rows 8..15): topsoil, a layer, snow, leaves, crosses above, mixed blocks and caves below
    private static int Surface(Random r, int x, int y, int z)
    {
        var ground = 40 + TessMix.Hash(x, 0, z) % 8;
        if (y > ground + 4) return ChunkRig.Air;

        var tree = TessMix.Hash(x / 5, 2, z / 5) % 4 == 0 && TessMix.Hash(x, 3, z) % 3 != 0;
        if (y > ground + 1) return tree ? Leaves : ChunkRig.Air;

        if (y == ground + 1)
        {
            return (TessMix.Hash(x, 1, z) % 9) switch
            {
                0 => SnowCube, 1 => Layer, 2 => JsonSnow, 3 => Leaves, 4 or 5 => Cross, 6 => Turned, _ => ChunkRig.Air
            };
        }

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
}

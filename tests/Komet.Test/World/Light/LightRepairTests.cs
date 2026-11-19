using Komet.World;

namespace Komet.Test.World;

// Golden test against the engine. The reference lights LightWorld with PlaceBlockLight for every source, then every column's sunlight
// by the sequence FullRelight runs for a whole column (FullRelight itself cannot serve: it places the sources again at twice their
// chunk's offset, pinned below). Block light the engine blends from overlapping sources in the order they were placed (a hue, and at
// the edge of a reach a step, can differ with another order), so the whole light is compared with the intact world after the same
// sources were placed again in the same order.
[NonParallelizable]
public sealed class LightRepairTests
{
    private const int Seed = 5;

    // Corner with lava, the glass wall's column, the pond with its light under water, the light cluster on the chunk corner
    private static readonly (int X, int Z)[] Broken = [(0, 3), (1, 1), (3, 0), (0, 0)];
    private static readonly BlockPos Low = new(0, 0, 0);
    private static readonly BlockPos High = new(LightWorld.SizeX - 1, LightWorld.SizeY - 1, LightWorld.SizeZ - 1);

    // Lit on first use: the quick set runs no test that reads it
    private static readonly Lazy<int[]?[]> Snapshot = new(Take);
    private static readonly Lazy<int[][]> Reference = new(() => Lit().Light());

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Waiting")]
    private static extern ref Queue<(int Dimension, int X, int Z)> Waiting(
        [UnsafeAccessorType("Komet.World.LightRepair, Komet.World")]
        object? repair);

    [TestCaseSource(nameof(Broken))]
    [Category("Slow")]
    public void ARepairedColumnGetsTheLightTheEngineGivesTheIntactWorld((int X, int Z) column)
    {
        var world = Lit();
        var chunks = Column(world, column.X, column.Z);
        var rain = Rain(world, column.X, column.Z);
        var lost = Lose(chunks, rain);
        var broken = LightRepair.Broken(chunks, rain);
        _ = LightRepair.Sunlight(world.Illuminator, chunks, column.X, column.Z, 0);
        _ = LightRepair.BlockLight(world.Illuminator, world, column.X, column.Z, broken, 0);
        var intact = Lit();
        _ = LightRepair.BlockLight(intact.Illuminator, intact, column.X, column.Z, broken, 0);
        var (found, expected) = (world.Light(), intact.Light());
        Assert.Multiple(() =>
        {
            Assert.That(broken, Has.Length.EqualTo(lost).And.Length.GreaterThan(0));
            Assert.That(Differences(Sun(found), Sun(Reference.Value)), Is.Empty,
                "sunlight that is not the reference's");
            Assert.That(Differences(found, expected), Is.Empty, "light that is not the intact world's");
        });
    }

    // Over a world loaded whole the relight is the engine's but for its sources: the same sunlight, every source where it is and
    // none added, and the light of the intact world after every source was placed again in the order the relight places them.
    // Why the reference is not FullRelight's: it places every light source again at its chunk's offset plus the position within
    // the world, a source in a chunk off the origin twice as far out - a light source where there is none, or none at all
    [Test]
    [Category("Slow")]
    public void TheRelightGivesTheEnginesSunlightAndPlacesEverySourceWhereItIs()
    {
        var world = Lit();
        var (before, order) = (Sources(world), Placements(world));
        var relit = LightRepair.FullRelight(world.Illuminator, Low, High);
        var engine = Lit();
        engine.Illuminator.FullRelight(Low, High);
        var intact = Lit();
        foreach (var (hsv, x, y, z) in order)
        {
            _ = intact.Illuminator.PlaceBlockLight(hsv, x, y, z);
        }

        var found = world.Light();
        Assert.Multiple(() =>
        {
            Assert.That(relit, Is.True);
            Assert.That(Sources(world), Is.EqualTo(before), "a light source added or moved");
            Assert.That(Differences(Sun(found), Sun(Reference.Value)), Is.Empty,
                "sunlight that is not the reference's");
            Assert.That(Differences(Sun(found), Sun(engine.Light())), Is.Empty, "sunlight that is not the engine's");
            Assert.That(Sources(engine).Except(before).ToList(), Is.Not.Empty.And.Contains("2,2,2:13312"),
                "the engine's FullRelight: a source of chunk 1,1,1 placed in chunk 2,2,2");
            Assert.That(Differences(found, intact.Light()), Is.Empty, "light that is not the intact world's");
        });
    }

    // The chunks of a column that reach the sky lose their light, as a save left by a relight; how many
    private static int Lose(IWorldChunk[] chunks, ushort[] rain)
    {
        var lost = 0;
        for (var y = 0; y < chunks.Length; y++)
        {
            // wholly below the rain height: its light may be missing rightly
            if (rain.Min() + 1 > y * 32 + 31) continue;

            chunks[y].Lighting.ClearLight();
            lost++;
        }

        return lost;
    }

    private static List<string> Differences(int[][] found, int[][] expected)
    {
        var differences = new List<string>();
        for (var c = 0; c < found.Length; c++)
        {
            for (var i = 0; i < found[c].Length && differences.Count < 12; i++)
            {
                if (found[c][i] != expected[c][i]) differences.Add($"{c}:{i} {expected[c][i]} -> {found[c][i]}");
            }
        }

        return differences;
    }

    // The sunlight of every cell: the low five bits
    private static int[][] Sun(int[][] light) =>
        [.. light.Select(chunk => chunk.Select(value => value & 0x1F).ToArray())];

    // LightWorld with its light sources placed, then the sunlight of every column, in the order FullRelight takes them. Lit once:
    // the sunlight takes seconds, so every other world is a new one given that world's light value for value
    private static LightWorld Lit()
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), Seed) { Recording = false };
        var lit = Snapshot.Value;
        for (var c = 0; c < lit.Length; c++)
        {
            if (lit[c] is not { } light) continue;

            var cells = Cells(world, c);
            for (var i = 0; i < light.Length; i++)
            {
                cells.SetLight(i, (uint)light[i]);
            }
        }

        return world;
    }

    private static int[]?[] Take()
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), Seed) { Recording = false };
        world.LightUp();
        for (var x = 0; x < LightWorld.ChunksX; x++)
        {
            for (var z = 0; z < LightWorld.ChunksZ; z++)
            {
                _ = LightRepair.Sunlight(world.Illuminator, Column(world, x, z), x, z, 0);
            }
        }

        var light = world.Light();
        return [.. light.Select((values, c) => Cells(world, c).lightLayer is null ? null : values)];
    }

    // The chunk's ChunkData by the index Light() lists it under
    private static ChunkData Cells(LightWorld world, int c) =>
        (ChunkData)world.GetChunk(c % LightWorld.ChunksX, c / (LightWorld.ChunksX * LightWorld.ChunksZ),
            c / LightWorld.ChunksX % LightWorld.ChunksZ).Lighting;

    // Every light source as FullRelight places it again: chunk by chunk, x before y before z, each where it is
    private static List<(byte[] Hsv, int X, int Y, int Z)> Placements(LightWorld world)
    {
        var placements = new List<(byte[] Hsv, int X, int Y, int Z)>();
        for (var i = 0; i < LightWorld.ChunksX * LightWorld.ChunksY * LightWorld.ChunksZ; i++)
        {
            var (x, y, z) = (i / (LightWorld.ChunksY * LightWorld.ChunksZ), i / LightWorld.ChunksZ % LightWorld.ChunksY,
                i % LightWorld.ChunksZ);
            var chunk = world.GetChunk(x, y, z);
            foreach (var at in chunk.LightPositions)
            {
                var pos = new BlockPos(x * 32 + at % 32, y * 32 + at / 1024, z * 32 + at / 32 % 32);
                placements.Add((world.Blocks[chunk.Data[at]].GetLightHsv(null!, pos), pos.X, pos.Y, pos.Z));
            }
        }

        return placements;
    }

    private static List<string> Sources(LightWorld world)
    {
        var sources = new List<string>();
        for (var i = 0; i < LightWorld.ChunksX * LightWorld.ChunksY * LightWorld.ChunksZ; i++)
        {
            var (x, y, z) = (i % LightWorld.ChunksX, i / LightWorld.ChunksX % LightWorld.ChunksY,
                i / (LightWorld.ChunksX * LightWorld.ChunksY));
            sources.AddRange(world.GetChunk(x, y, z).LightPositions.Select(p => $"{x},{y},{z}:{p}"));
        }

        return sources;
    }

    // Typed as the server's load hands its column over: an array of a chunk class, taken as IWorldChunk[] by covariance
    private static IWorldChunk[] Column(LightWorld world, int x, int z)
    {
        WorldChunk[] column =
            [.. Enumerable.Range(0, LightWorld.ChunksY).Select(y => (WorldChunk)world.GetChunk(x, y, z))];
        return [.. column];
    }

    private static ushort[] Rain(LightWorld world, int cx, int cz)
    {
        var rain = new ushort[32 * 32];
        for (var x = 0; x < 32; x++)
        {
            for (var z = 0; z < 32; z++)
            {
                for (var y = LightWorld.SizeY - 1; y >= 0; y--)
                {
                    var (wx, wz) = (cx * 32 + x, cz * 32 + z);
                    if (world.BlockAt(wx, y, wz) == LightWorld.Air && world.Fluid(wx, y, wz) == 0) continue;

                    rain[z * 32 + x] = (ushort)y;
                    break;
                }
            }
        }

        return rain;
    }

    // A chunk as the server keeps it packed: no ChunkData, only the compressed arrays
    private sealed class PackedChunk : WorldChunk
    {
        public PackedChunk(byte[]? light, byte[]? lightSat) =>
            (lightCompressed, lightSatCompressed) = (light, lightSat);

        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];
    }
}

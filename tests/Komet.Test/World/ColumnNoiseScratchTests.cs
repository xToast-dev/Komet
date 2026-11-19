using System.Collections.Concurrent;
using Komet.Core;
using Komet.World;
using Vintagestory.ServerMods;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.Test.World;

// Golden tests against the engine's own terrain noise. Random noises of 1 to 14 octaves with amplitudes and thresholds that let anything
// from none to all octaves contribute, evaluated as GenTerra evaluates them - the bounds, then NoiseSign upwards through the column, whose
// cache of the previous evaluation makes the order part of the result - and Noise. Every value must be bit for bit what the shipped
// constructor gives: from the thread's arrays inside Column, from new ones outside it, on many threads at once, and in GenTerra's own
// OnChunkColumnGen, which runs the rewritten Parallel.For body over real landforms and records every block it writes.
public sealed class ColumnNoiseScratchTests
{
    private const int Inputs = 600,
        Heights = 254,
        Short = 48, Passes = 3,
        Threads = 8; // every machine runs the Parallel.For body concurrently

    private static readonly FieldInfo Entries = AccessTools.Field(typeof(ColumnNoise), "orderedOctaveEntries");
    private static readonly FieldInfo Past = AccessTools.Field(typeof(ColumnNoise), "pastEvaluations");
    private static int _seen;

    [TearDown]
    public void Reset()
    {
        ColumnNoiseScratch.Enabled = true;
        Counting.Hud = false;
    }

    private static TestHarmony Patched(ILogger? logger = null)
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials is found by name, so it has to be loaded first
        var harmony = new TestHarmony("komet-test-columnnoise");
        ColumnNoiseScratch.Install(harmony, logger);
        Assert.That(ColumnNoiseScratch.Rewritten, Is.True,
            "ForColumn, the ColumnNoise constructor or GenTerra's column body changed shape");
        return harmony;
    }

    // GenTerra has 9 octaves at the default world height and one more per 128 blocks above it; a threshold at or above an amplitude
    // silences that octave for the column, and the thresholders NoiseSign gets lie mostly within the column's bound, sometimes beyond
    private static Input Make(Random r)
    {
        var octaves = r.Next(1, 15);
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(octaves, 0.0003 * (0.2 + r.NextDouble() * 5),
            0.5 + r.NextDouble() / 2, r.NextInt64());
        var amplitudes = new double[octaves];
        var thresholds = new double[octaves];
        var bound = 0.0;
        for (var i = 0; i < octaves; i++)
        {
            amplitudes[i] = r.Next(6) == 0 ? 0 : r.NextDouble() * 2.4 - 1.2;
            thresholds[i] = r.Next(3) == 0 ? 0 : r.NextDouble() * 0.9;
            bound += Math.Max(0, Math.Abs(amplitudes[i]) - thresholds[i]) * 1.2;
        }

        var thresholders = Enumerable.Range(0, 64).Select(_ =>
                r.Next(8) == 0 ? (r.NextDouble() - 0.5) * 8 * (bound + 1) : (r.NextDouble() - 0.5) * 2 * bound)
            .ToArray();
        var reach = r.Next(4) == 0 ? 2e7 : 2e5;
        return new Input(noise, 0.25 * (0.5 + r.NextDouble()), amplitudes, thresholds, (r.NextDouble() - 0.5) * reach,
            (r.NextDouble() - 0.5) * reach, thresholders);
    }

    private static Input Contributing(Random r) =>
        Enumerable.Range(0, 64).Select(_ => Make(r)).First(input => EntriesOf(input.Engine()).Length > 0);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static long[] Trace(ColumnNoise column, Input input, int heights = Heights)
    {
        var trace = new List<long>(heights + 32)
            { Bits(column.BoundMin), Bits(column.BoundMax), Bits(column.UncurvedBound) };
        for (var y = 1; y <= heights; y++)
        {
            trace.Add(Bits(column.NoiseSign(y, input.Thresholders[y % input.Thresholders.Length])));
        }

        for (var y = 0; y < heights; y += 17)
        {
            trace.Add(Bits(column.Noise(y)));
        }

        return [.. trace];
    }

    private static Array EntriesOf(ColumnNoise column) => (Array)Entries.GetValue(column)!;

    private static Array PastOf(ColumnNoise column) => (Array)Past.GetValue(column)!;

    [Test]
    public void ColumnsAreBitIdenticalToTheEngine()
    {
        var r = new Random(11);
        var inputs = Enumerable.Range(0, Inputs).Select(_ => Make(r)).ToList();
        var engine = inputs.Select(input => Trace(input.Engine(), input)).ToList();
        var contributing = inputs.Select(input => EntriesOf(input.Engine()).Length).ToList();
        using var harmony = Patched();
        Counting.Hud = true;
        var start = ColumnNoiseScratch.Saved;
        var scratch = inputs.Select(input => Trace(input.Scratch(), input)).ToList();
        var saved = ColumnNoiseScratch.Saved - start;
        var plain = inputs.Select(input => Trace(input.Engine(), input)).ToList();
        ColumnNoiseScratch.Enabled = false;
        var off = inputs.Select(input => Trace(input.Scratch(), input)).ToList();
        ColumnNoiseScratch.Enabled = true;
        var lengths = inputs.Select(input => EntriesOf(input.Scratch()).Length).ToList();
        Assert.Multiple(() =>
        {
            for (var i = 0; i < Inputs; i++)
            {
                Assert.That(scratch[i], Is.EqualTo(engine[i]), $"column {i} from the thread's arrays");
                Assert.That(plain[i], Is.EqualTo(engine[i]), $"column {i} built outside Column");
                Assert.That(off[i], Is.EqualTo(engine[i]), $"column {i} with the rewrite switched off");
            }

            Assert.That(lengths, Is.EqualTo(contributing), "every array exactly as long as the engine's");
            Assert.That(contributing.Distinct().Count(), Is.GreaterThanOrEqualTo(12),
                "the contributing octaves have to vary");
            Assert.That(contributing, Has.Some.EqualTo(0), "a column no octave contributes to");
            Assert.That(saved, Is.GreaterThan(Inputs * 100L), "the columns were built from the thread's arrays");
        });
    }

    // Many threads at once, as Parallel.For runs GenTerra's body, the rewrite on and off in turn. Each column's arrays are registered
    // while the column is evaluated: a second column holding them before the first is done would be arrays shared while both are live.
    [Test]
    public void ParallelColumnsMatchTheEngine()
    {
        var r = new Random(13);
        var inputs = Enumerable.Range(0, 4096).Select(_ => Make(r)).ToArray();
        var engine = inputs.Select(input => Trace(input.Engine(), input, Short)).ToArray();
        using var harmony = Patched();
        var live = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        var shared = 0;
        var threads = new ConcurrentDictionary<int, bool>();
        Counting.Hud = true;
        var start = ColumnNoiseScratch.Saved;
        for (var round = 0; round < 2 * Passes; round++)
        {
            ColumnNoiseScratch.Enabled = round % 2 == 0;
            var results = new long[inputs.Length][];
            _ = Parallel.For(0, inputs.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, i =>
                {
                    threads[Environment.CurrentManagedThreadId] = true;
                    var column = inputs[i].Scratch();
                    var entries = EntriesOf(column);
                    var mine = entries.Length > 0 && live.TryAdd(entries, i);
                    if (entries.Length > 0 && !mine) _ = Interlocked.Increment(ref shared);

                    results[i] = Trace(column, inputs[i], Short);
                    if (mine) _ = live.TryRemove(entries, out _);
                });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(shared, Is.Zero, "two live columns shared their arrays");
                for (var i = 0; i < inputs.Length; i++)
                {
                    Assert.That(results[i], Is.EqualTo(engine[i]), $"round {round}, column {i}");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(threads, Has.Count.GreaterThan(1), "the loop has to run on several threads");
            Assert.That(ColumnNoiseScratch.Saved - start, Is.GreaterThan(Passes * inputs.Length * 100L),
                "the rounds with the rewrite on reused");
        });
    }

    private static long PerChunkColumn()
    {
        var world = World(5);
        _ = Generate(world, 5, 5, false); // the region's landform map and the threads' temporary arrays
        var start = GC.GetTotalAllocatedBytes(true);
        for (var z = 0; z < 4; z++)
        {
            _ = Generate(world, 5, 6 + z, false);
        }

        return (GC.GetTotalAllocatedBytes(true) - start) / 4;
    }

    // GenTerra's own chunk column generation, as the server calls it for a new chunk column: initWorldGen against a stand-in server with
    // the game's landforms.json, then OnChunkColumnGen for chunk columns across a region, with and without border smoothing. Several
    // generate at once, each on its own GenTerra with its own Parallel.For: the pool threads take bodies of one chunk column and then of
    // another, so a thread's arrays pass between generations. Every block and fluid written, both height maps and YMax must be what the
    // shipped body produces, with the rewrite on and off.
    [Test]
    [Category("Slow")]
    public void GenTerraGeneratesTheSameTerrain()
    {
        GameInstall.RequireAssets();
        (int Seed, int X, int Z, bool Smooth)[] jobs =
        [
            (1, 3, 4, false), (1, 9, 12, true), (2, 0, 0, false), (2, 15, 15, true), (3, 6, 1, false), (3, 7, 1, true),
            (4, 12, 8, false), (4, 2, 11, true)
        ];
        var engine = jobs.Select(j => Fresh(j.Seed, j.X, j.Z, j.Smooth)).ToList();
        using var harmony = Patched();
        Counting.Hud = true;
        var saved = new long[2];
        for (var round = 0; round < 2; round++)
        {
            ColumnNoiseScratch.Enabled = round == 0;
            var start = ColumnNoiseScratch.Saved;
            // one after another: LoadLandforms fills a static
            var worlds = jobs.Select(j => World(j.Seed)).ToArray();
            var results = new Generated[jobs.Length];
            _ = Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                i => results[i] = Generate(worlds[i], jobs[i].X, jobs[i].Z, jobs[i].Smooth));
            saved[round] = ColumnNoiseScratch.Saved - start;
            using (Assert.EnterMultipleScope())
            {
                for (var i = 0; i < jobs.Length; i++)
                {
                    Assert.That(results[i].Writes, Is.EqualTo(engine[i].Writes),
                        $"blocks of chunk column {i}, round {round}");
                    Assert.That(results[i].HeightMaps, Is.EqualTo(engine[i].HeightMaps),
                        $"height maps of chunk column {i}, round {round}");
                }
            }
        }

        ColumnNoiseScratch.Enabled = true;
        Assert.Multiple(() =>
        {
            Assert.That(engine.Select(c => c.Writes.Count(w => w.Op == Write.Block)), Is.All.GreaterThan(1000),
                "every chunk column has terrain");
            Assert.That(engine.SelectMany(c => c.Writes).Count(w => w.Op == Write.Fluid), Is.GreaterThan(1000),
                "some columns lie under water");
            Assert.That(engine.Select(c => c.HeightMaps[^1]).Distinct().Count(), Is.GreaterThan(2),
                "the chunk columns differ in height");
            Assert.That(saved[0], Is.GreaterThan(jobs.Length * 1024L * 500),
                "every block column was built from the thread's arrays");
            Assert.That(saved[1], Is.Zero, "switched off");
        });
    }

    private static MethodBase Seam(int seam) =>
        seam switch
        {
            0 => ColumnNoiseScratch.ForColumn()!,
            1 => ColumnNoiseScratch.Constructor()!,
            2 => ColumnNoiseScratch.ColumnBody()!,
            _ => AccessTools.Method(typeof(ColumnNoise), nameof(ColumnNoise.NoiseSign))
        };

    private static void SeenByAnotherMod() => _ = Interlocked.Increment(ref _seen);

    // One found at Install - here on NoiseSign, which the body calls with its column - leaves every method as shipped, and says so
    [Test]
    public void AnotherModsPatchAtInstallLeavesTheEngineAlone()
    {
        _ = typeof(GenTerra).Assembly;
        using var other = new TestHarmony("komet-test-another-mod");
        using var harmony = new TestHarmony("komet-test-columnnoise");
        var logger = new CapturingLogger();
        _ = other.Patch(Seam(3), new HarmonyMethod(typeof(ColumnNoiseScratchTests), nameof(SeenByAnotherMod)));
        ColumnNoiseScratch.Install(harmony, logger);
        var input = Contributing(new Random(18));
        var (first, second) = (input.Scratch(), input.Scratch());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ColumnNoiseScratch.Rewritten, Is.False);
            for (var i = 0; i < 3; i++)
            {
                Assert.That(Harmony.GetPatchInfo(Seam(i))?.Owners ?? [], Does.Not.Contain(harmony.Id), $"seam {i}");
            }

            Assert.That(EntriesOf(second), Is.Not.SameAs(EntriesOf(first)), "every column owns new arrays");
            Assert.That(string.Join('\n', logger.Lines), Is.EqualTo(
                "Notification Komet ColumnNoiseScratch stands down: another mod patches ColumnNoise.NoiseSign; terrain columns allocate as shipped"));
        }
    }

    private static bool IsMine(CodeInstruction code) =>
        code.operand is MethodInfo { DeclaringType: var t } && t == typeof(ColumnNoiseScratch);

    private static GenTerraRegion World(int seed) => GenTerraWorld.World(seed, Threads);

    private static System.Func<object?[]?, object?> Fixed(object value) => GenTerraWorld.Fixed(value);

    private static Generated Fresh(int seed, int chunkX, int chunkZ, bool smooth) =>
        Generate(World(seed), chunkX, chunkZ, smooth);

    // The chunk column request: eight chunks whose blocks record every write in order, one map chunk with the two height maps, and
    // for border smoothing the neighbours' terrain heights on some sides
    private static Generated Generate(GenTerraRegion region, int chunkX, int chunkZ, bool smooth)
    {
        var writes = new List<(Write Op, int Chunk, int Index, int Value)>();
        var (rain, terrain) = (new ushort[1024], new ushort[1024]);
        var yMax = new int[1];
        var map = Answers.Of<IMapChunk>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_MapRegion"] = Fixed(region.Maps), ["get_RainHeightMap"] = Fixed(rain),
            ["get_WorldGenTerrainHeightMap"] = Fixed(terrain),
            ["set_YMax"] = args => yMax[0] = (ushort)args![0]!
        });
        IChunkBlocks Blocks(int chunk) => Answers.Of<IChunkBlocks>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["SetBlockBulk"] = args => Log(writes, Write.Bulk, chunk, (int)args![0]!,
                (int)args[3]! ^ ((int)args[1]! << 16) ^ ((int)args[2]! << 24)),
            ["set_Item"] = args => Log(writes, Write.Block, chunk, (int)args![0]!, (int)args[1]!),
            ["SetFluid"] = args => Log(writes, Write.Fluid, chunk, (int)args![0]!, (int)args[1]!)
        });

        var r = new Random(chunkX * 31 + chunkZ);
        var neighbours = new ushort[8][];
        for (var i = 0; i < neighbours.Length; i++)
        {
            if (smooth && r.Next(3) > 0)
            {
                neighbours[i] = [.. Enumerable.Range(0, 1024).Select(_ => (ushort)r.Next(80, 170))];
            }
        }

        GenTerraWorld.Generate(region, chunkX, chunkZ, map, Blocks, neighbours, smooth);
        return new Generated(writes, [.. rain.Select(h => (int)h), .. terrain.Select(h => (int)h), yMax[0]]);
    }

    private static object? Log(List<(Write Op, int Chunk, int Index, int Value)> writes, Write op, int chunk, int index,
        int value)
    {
        writes.Add((op, chunk, index, value)); // after the Parallel.For, on the generating thread only
        return null;
    }

    private sealed record Input(
        NewNormalizedSimplexFractalNoise Noise,
        double YFrequency,
        double[] Amplitudes,
        double[] Thresholds,
        double X,
        double Z,
        double[] Thresholders)
    {
        public ColumnNoise Engine() => Noise.ForColumn(YFrequency, Amplitudes, Thresholds, X, Z);

        public ColumnNoise Scratch() => ColumnNoiseScratch.Column(Noise, YFrequency, Amplitudes, Thresholds, X, Z);
    }

    private static class Keeper
    {
        public static object? Kept = string.Empty;

        public static object?[] Many = [null];
    }

    private enum Write
    {
        Bulk,
        Block,
        Fluid
    }

    private sealed record Generated(List<(Write Op, int Chunk, int Index, int Value)> Writes, int[] HeightMaps);

}

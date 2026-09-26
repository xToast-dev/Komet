using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Komet.Test.Tessellation;

// The buckets a pass lands in, the cumulative totals the benchmark takes differences of, and the patches on the engine's real
// TesselateChunk
public sealed class TessAccountingTests
{
    [SetUp]
    public void Fresh()
    {
        TessAccounting.Clear();
        Counting.Hud = true;
    }

    [TearDown]
    public void Reset()
    {
        Counting.Hud = false;
        TessAccounting.Clear();
    }

    // Buckets by number, the enum is internal to Komet: 0 full, 1 edge, 2 priority full, 3 priority edge, 4 skipped, 5 requeued
    [TestCase(false, false, false, false, 4)]
    [TestCase(false, false, true, true, 4)]
    [TestCase(false, true, false, false, 5)]
    [TestCase(true, true, false, true, 5)]
    [TestCase(true, false, false, false, 0)]
    [TestCase(true, false, false, true, 1)]
    [TestCase(true, false, true, false, 2)]
    [TestCase(true, false, true, true, 3)]
    public void APassLandsInItsBucket(bool processed, bool requeue, bool priority, bool edge, int expected)
    {
        Assert.That(TessAccounting.Classify(processed, requeue, priority, edge), Is.EqualTo((TessBucket)expected));
    }

    [Test]
    public void OnlyFullPassesWithoutVerticesCountAsZero()
    {
        TessAccounting.Record(TessBucket.Full, 100, true);
        TessAccounting.Record(TessBucket.PriorityFull, 50, true);
        TessAccounting.Record(TessBucket.Edge, 30, true); // an edge pass without vertices is a hidden edge, not rock
        TessAccounting.Record(TessBucket.Skipped, 5, true);
        TessAccounting.Record(TessBucket.Full, 200, false);
        var totals = TessAccounting.Totals();
        Assert.Multiple(() =>
        {
            Assert.That(totals.Passes, Is.EqualTo(4), "a skipped call is no pass");
            Assert.That(totals.Ticks, Is.EqualTo(380));
            Assert.That(totals.Edge, Is.EqualTo(1));
            Assert.That(TessAccounting.ZeroMs, Is.EqualTo(150 * 1000.0 / Stopwatch.Frequency).Within(1e-9));
            Assert.That(TessAccounting.ZeroPasses, Is.EqualTo(2));
            Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(1));
        });
    }

    // The totals only grow: the benchmark and the HUD take differences of them
    [Test]
    public void ASegmentIsTheDifferenceOfTwoTotals()
    {
        TessAccounting.Record(TessBucket.Full, 1000, false);
        var start = TessAccounting.Totals();
        TessAccounting.Record(TessBucket.Edge, 3000, false);
        var segment = TessAccounting.Totals().Since(start);
        Assert.Multiple(() =>
        {
            Assert.That((segment.Passes, segment.Ticks, segment.Edge), Is.EqualTo((1L, 3000L, 1L)));
            Assert.That(segment.Ms, Is.EqualTo(3000 * 1000.0 / Stopwatch.Frequency).Within(1e-9));
            Assert.That(TessAccounting.Totals().Passes, Is.EqualTo(2));
        });
    }

    // The patched engine method (Install checks the parameter names Harmony hands over), on a chunk the map does not have and on an
    // Empty one: the engine returns before any lock, and that is a skipped call
    [Test]
    public void ThePatchesCountTheEnginesCalls()
    {
        using var rig = new ChunkRig();
        var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
        ChunkRig.Set(manager, "game", rig.Game);
        using var harmony = new TestHarmony("komet-test-tessaccounting");
        TessAccounting.Install(harmony);
        Assert.That(TessAccounting.Installed, Is.True);
        Assert.That(manager.TesselateChunk(3, 3, 3, false, false, out var requeue), Is.Zero);
        var empty = rig.Put(2, 2, 2, (_, _, _) => ChunkRig.Air, empty: true);
        Assert.That(manager.TesselateChunk(2, 2, 2, true, true, out requeue), Is.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(requeue, Is.False);
            Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(2));
            Assert.That(TessAccounting.Totals().Passes, Is.Zero);
            Assert.That(ChunkRig.Get(empty, "quantityDrawn"), Is.EqualTo(1), "the engine's own path ran");
        });
        Counting.Hud = false;
        _ = manager.TesselateChunk(3, 3, 3, false, false, out _);
        Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(2), "nothing while nobody looks");
    }
}

// What TessWorkers rely on: every TessSafety patch finds its sites in the engine as installed, and two tesselators on two threads
// decode their chunks as each would alone - the palette table the engine keeps in one static field is one per thread now
public sealed class TessSafetyTests
{
    private const int Rounds = 300;

    [Test]
    public void EveryPatchFindsItsSites()
    {
        var harmony = new Harmony("komet-test-tesssafety-sites");
        try
        {
            TessSafety.Install(harmony);
            TessSchedule.Install(harmony);
            TessWorkers.Install(harmony);
            Assert.Multiple(() =>
            {
                Assert.That(TessSafety.Installed, Is.True, "an engine method no longer looks as TessSafety expects");
                Assert.That(TessWorkers.Installed, Is.True,
                    "TesselateChunk no longer reads game.TerrainChunkTesselator once");
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TessSafety.Clear();
            TessSchedule.Clear();
            TessWorkers.Stop();
        }
    }

    // A failed install leaves nothing behind: with one transpiler finding another count of sites, every patch made so far goes again
    [Test]
    public void AnIncompleteInstallUnpatchesEverything()
    {
        var harmony = new Harmony("komet-test-tesssafety-partial");
        var blocker = new Harmony("komet-test-tesssafety-blocker");
        var cross = AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross));
        try
        {
            _ = blocker.Patch(cross, transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad)));
            TessSafety.Install(harmony);
            var decoder = AccessTools.DeclaredMethod(typeof(BlockChunkDataLayer), "getBlockOne");
            Assert.Multiple(() =>
            {
                Assert.That(TessSafety.Installed, Is.False);
                Assert.That(Harmony.GetPatchInfo(decoder)?.Owners ?? [], Does.Not.Contain(harmony.Id),
                    "the decoders read the engine's table");
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            blocker.UnpatchAll(blocker.Id);
            TessSafety.Clear();
        }
    }

    // Another mod's transpiler arriving later makes Harmony run TessSafety's again: a count that no longer fits keeps the workers off
    [Test]
    public void ALaterForeignTranspilerBreaksTheWorkers()
    {
        var harmony = new Harmony("komet-test-tesssafety-late");
        var blocker = new Harmony("komet-test-tesssafety-late-blocker");
        try
        {
            TessSafety.Install(harmony);
            Assert.That((TessSafety.Installed, TessSafety.Broken), Is.EqualTo((true, false)));
            _ = blocker.Patch(AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross)),
                transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad))
                { priority = Priority.First });
            Assert.That(TessSafety.Broken, Is.True);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            blocker.UnpatchAll(blocker.Id);
            TessSafety.Clear();
        }
    }

    // Another mod's transpiler that reads CrossTesselator.startRot once more
    private static IEnumerable<CodeInstruction> OneMoreLoad(IEnumerable<CodeInstruction> instructions)
    {
        var start = AccessTools.DeclaredField(typeof(CrossTesselator), "startRot");
        return new[] { new CodeInstruction(OpCodes.Ldsfld, start), new CodeInstruction(OpCodes.Pop) }
            .Concat(instructions);
    }

    // Two chunks whose palettes hold stone and granite in opposite order: with the engine's one static table, a thread decoding its
    // chunk while the other built the table for its own reads the other palette, and stone becomes granite
    [Test]
    public void TwoThreadsDecodeTheirOwnPalettes()
    {
        var harmony = new Harmony("komet-test-tesssafety-palette");
        using var a = new ChunkRig();
        using var b = new ChunkRig();
        try
        {
            TessSafety.Install(harmony);
            Assert.That(TessSafety.Installed, Is.True);
            var chunkA = a.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Stone : ChunkRig.Granite);
            var chunkB = b.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Granite : ChunkRig.Stone);
            var expectedA = Extended(a, chunkA);
            var expectedB = Extended(b, chunkB);
            Assert.That(expectedA, Is.Not.EqualTo(expectedB),
                "the two chunks must differ for the test to mean anything");
            var mismatches = 0;
            Parallel.Invoke(() => Interlocked.Add(ref mismatches, Repeat(a, chunkA, expectedA)),
                () => Interlocked.Add(ref mismatches, Repeat(b, chunkB, expectedB)));
            Assert.That(mismatches, Is.Zero);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TessSafety.Clear();
        }
    }

    private static int Repeat(ChunkRig rig, ClientChunk chunk, string[] expected)
    {
        var wrong = 0;
        for (var i = 0; i < Rounds; i++)
            if (!Extended(rig, chunk).SequenceEqual(expected))
                wrong++;
        return wrong;
    }

    // The centre of the extended block array after the engine's BuildExtendedChunkData, as block codes
    private static string[] Extended(ChunkRig rig, ClientChunk chunk)
    {
        BuildExtended(rig.Tesselator, chunk, 1, 1, 1, false, false);
        var cells = new string[32 * 32 * 32];
        for (var i = 0; i < cells.Length; i++)
        {
            int x = i % 32, z = i / 32 % 32, y = i / 1024;
            cells[i] = rig.BlocksExt[((y + 1) * 34 + z + 1) * 34 + x + 1]?.Code?.Path ?? "null";
        }

        return cells;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildExtendedChunkData")]
    private static extern void BuildExtended(ChunkTesselator tesselator, ClientChunk chunk, int x, int y, int z,
        bool atMapEdge, bool skipChunkCenter);
}

// The same chunks tessellated by the engine's thread alone and with four of Komet's worker threads beside it, each with a tesselator of
// its own, through the patched engine tick (TessSchedule) and the engine's ChunkTesselatorManager.TesselateChunk with TessSafety,
// TessWorkers' transpiler and the fast paths installed: every chunk's meshes (vertices, uv, light, flags, indices, custom ints, bounds)
// must come out the same. The world mixes palettes of 1 to 6 bits (every block decoder), opaque and partly opaque cubes and randomly
// rotated crosses (CrossTesselator's startRot/endRot), with ambient occlusion on (FaceLight).
public sealed class TessParallelTests
{
    private const int Threads = 4, Extra = 40, Chunks = ChunkRig.ChunksX * ChunkRig.ChunksY * ChunkRig.ChunksZ,
        MaxTicks = 100000;

    private const int Cross = ChunkRig.Glass;
    private static readonly int[] OneAtlas = [0]; // texture id 0

    [Test]
    public void ManyThreadsTessellateAsOne()
    {
        using var rig = new ChunkRig(Extra);
        Fill(rig);
        var manager = Manager(rig);
        var harmony = new Harmony("komet-test-tessparallel");
        var (background, jobs) = (WorkerPool.Background, TessWorkers.Jobs);
        TessWorkers.Jobs = Threads; // all four at once: without a backlog the default lets two
        try
        {
            Install(harmony);
            _ = Mesher(rig, rig.Tesselator);
            var alone = Run(rig, manager, []);
            ChunkTesselator[] workers = [.. Enumerable.Range(0, Threads).Select(_ => Mesher(rig, rig.NewTesselator()))];
            var together = Run(rig, manager, workers);
            Assert.Multiple(() =>
            {
                Assert.That(alone, Has.Count.EqualTo(Chunks), "every chunk tessellated");
                Assert.That(alone.Values.Count(d => d.Vertices > 0), Is.GreaterThan(Chunks / 4),
                    "the world draws something");
                Assert.That(together.Keys, Is.EquivalentTo(alone.Keys));
                foreach (var (key, digest) in alone) Assert.That(together[key], Is.EqualTo(digest), $"chunk {key}");
            });
        }
        finally
        {
            _ = WorkerPool.Stop();
            TessSafety.Guard(false);
            harmony.UnpatchAll(harmony.Id);
            TessSafety.Clear();
            TessSchedule.Clear();
            TessWorkers.Stop();
            (WorkerPool.Background, TessWorkers.Jobs) = (background, jobs);
        }
    }

    private static void Install(Harmony harmony)
    {
        TessSafety.Install(harmony);
        TessSchedule.Install(harmony);
        TessWorkers.Install(harmony);
        ExtendedRows.Install(harmony);
        VisibleFaces.Install(harmony);
        FaceLight.Install(harmony);
        OccludedChunks.Install(harmony);
        Assert.That((TessSafety.Installed, TessSchedule.Installed, TessWorkers.Installed, ExtendedRows.Rewritten,
            VisibleFaces.Installed, FaceLight.Installed), Is.EqualTo((true, true, true, true, true, true)));
    }

    // Every chunk marked dirty, then the engine's tick on this thread (the "tesselateterrain" thread) until every chunk's mesh is queued
    // for upload; with workers, each pool thread runs passes with the tesselator provided for it
    private static Dictionary<string, Digest> Run(ChunkRig rig, ChunkTesselatorManager manager,
        ChunkTesselator[] workers)
    {
        var dirty = (UniqueQueue<long>)ChunkRig.Get(rig.Game, "dirtyChunks");
        for (var i = 0; i < Chunks; i++)
            dirty.Enqueue(ChunkRig.Key(i % ChunkRig.ChunksX, i / ChunkRig.ChunksX % ChunkRig.ChunksY, i / 16));
        TessWorkers.Steer(rig.Game, manager, true); // binds the world, which drops any instance an earlier world had
        for (var i = 0; i < workers.Length; i++) TessWorkers.Provide(i, workers[i]);
        WorkerPool.Resize(null, workers.Length);
        var (counting, before) = (Counting.Bench, WorkerPool.BackgroundTicks);
        Counting.Bench = true; // the pool's time on background jobs: the workers must have run passes
        var (gate, queue) = (ChunkRig.Get(manager, "tessChunksQueueLock"),
            (SortableQueue<TesselatedChunk>)ChunkRig.Get(manager, "tessChunksQueue"));
        var done = 0;
        for (var tick = 0; tick < MaxTicks && done < Chunks; tick++)
        {
            manager.OnSeperateThreadGameTick(0);
            lock (gate)
            {
                done = queue.Count;
            }
        }

        Counting.Bench = counting;
        Assert.That(WorkerPool.Stop(), Is.True);
        Assert.That(WorkerPool.BackgroundTicks > before, Is.EqualTo(workers.Length > 0),
            "passes on the worker threads");
        return Collect(manager);
    }

    private static Dictionary<string, Digest> Collect(ChunkTesselatorManager manager)
    {
        var queue = (SortableQueue<TesselatedChunk>)ChunkRig.Get(manager, "tessChunksQueue");
        var result = new Dictionary<string, Digest>();
        while (queue.Count > 0)
        {
            var tess = queue.Dequeue();
            var key = string.Join(',', ChunkRig.Get(tess, "positionX"), ChunkRig.Get(tess, "positionYAndDimension"),
                ChunkRig.Get(tess, "positionZ"));
            result.Add(key, Digest.Of(tess));
        }

        return result;
    }

    // The engine's own pool setup (UpdateForAtlasses, one atlas) and block tesselators for cubes and crosses, AO on
    private static ChunkTesselator Mesher(ChunkRig rig, ChunkTesselator tesselator)
    {
        var tesselators = new IBlockTesselator[40];
        tesselators[(int)EnumDrawType.Cube] = new CubeTesselator(1f);
        tesselators[(int)EnumDrawType.Cross] = new CrossTesselator();
        ChunkRig.Set(tesselator, "blockTesselators", tesselators);
        ChunkRig.Set(tesselator, "AoAndSmoothShadows", true);
        _ = AccessTools.Method(typeof(ChunkTesselator), "UpdateForAtlasses").Invoke(tesselator, [OneAtlas]);
        Assert.That(rig.Game.FastBlockTextureSubidsByBlockAndFace, Is.Not.Null);
        return tesselator;
    }

    // The manager's upload queue collects the results; the rest is the state the tick and TesselateChunk read
    private static ChunkTesselatorManager Manager(ChunkRig rig)
    {
        var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
        var game = rig.Game;
        ChunkRig.Set(manager, "game", game);
        ChunkRig.Set(manager, "chunksize", ChunkRig.Size);
        ChunkRig.Set(manager, "tessChunksQueueLock", new object());
        ChunkRig.Set(manager, "tessChunksQueue", new SortableQueue<TesselatedChunk>());
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        ChunkRig.Set(platform, "uptimeStopWatch", Stopwatch.StartNew());
        (game.Platform, game.TerrainChunkTesselator, game.ShouldTesselateTerrain) = (platform, rig.Tesselator, true);
        game.frustumCuller = (FrustumCulling)RuntimeHelpers.GetUninitializedObject(typeof(FrustumCulling));
        foreach (var name in new[] { "dirtyChunksPriority", "dirtyChunks", "dirtyChunksLast" })
        {
            ChunkRig.Set(game, name, new UniqueQueue<long>());
            ChunkRig.Set(game, name + "Lock", new object());
        }

        game.FastBlockTextureSubidsByBlockAndFace = [.. rig.Blocks.Select(_ => new int[6])];
        var positions = new[] { new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 } };
        ChunkRig.Set(game.BlockAtlasManager, "TextureAtlasPositionsByTextureSubId", positions);
        rig.Map.MapChunkSize = ChunkRig.Size;
        return manager;
    }

    // Rock with caves and ores below, a surface of hills with crosses on it, sky with a few floating crosses above
    private static void Fill(ChunkRig rig)
    {
        foreach (var block in rig.Blocks)
            (block.VertexFlags, block.EmitSideAo) = (new VertexFlags(0), block.AllSidesOpaque ? (byte)63 : (byte)0);
        (rig.Blocks[Cross].DrawType, rig.Blocks[Cross].RandomizeRotations, rig.Blocks[Cross].EmitSideAo) =
            (EnumDrawType.Cross, true, 0);
        for (var i = 0; i < Chunks; i++)
        {
            int cx = i % ChunkRig.ChunksX, cy = i / ChunkRig.ChunksX % ChunkRig.ChunksY, cz = i / 16;
            var ores = cx == 1 && cz == 2 ? Extra : 3; // this column's palettes need six bits
            _ = rig.Put(cx, cy, cz, (x, y, z) => Block(cx * 32 + x, cy * 32 + y, cz * 32 + z, ores));
        }
    }

    private static int Block(int x, int y, int z, int ores)
    {
        var h = TessMix.Hash(x, 0, z);
        var ground = 70 + (int)(8 * Math.Sin(x / 9.0) + 6 * Math.Cos(z / 7.0));
        if (y > ground) return (y == ground + 1 && h % 5 == 0) || (h % 997 == 0 && y > 110) ? Cross : ChunkRig.Air;
        var r = TessMix.Hash(x, y, z);
        if (y < ground - 3 && r % 11 == 0) return ChunkRig.Air; // caves
        if (r % 13 == 0) return ChunkRig.Count + r / 13 % ores; // ores
        var pick = r / 16 % 17;
        if (pick == 0) return ChunkRig.Slab;
        if (pick == 1) return ChunkRig.Merge;
        if (pick == 2) return Cross;
        return pick < 9 ? ChunkRig.Granite : ChunkRig.Stone;
    }

    // A chunk's meshes, part by part in the engine's order, hashed; and its vertex count
    private sealed record Digest(int Vertices, string Hash)
    {
        public static Digest Of(TesselatedChunk tess)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var vertices = 0;
            foreach (var parts in new[] { ChunkRig.CenterParts(tess), ChunkRig.EdgeParts(tess) })
            {
                Add(hash, parts?.Length ?? -1);
                foreach (var part in parts ?? [])
                {
                    Add(hash, (int)ChunkRig.Get(part, "atlasNumber"),
                        (int)(EnumChunkRenderPass)ChunkRig.Get(part, "pass"));
                    foreach (var lod in new[]
                                 { "modelDataLod0", "modelDataLod1", "modelDataNotLod2Far", "modelDataLod2Far" })
                        vertices += Mesh(hash, (MeshData?)AccessTools.Field(part.GetType(), lod).GetValue(part));
                }
            }

            var sphere = ChunkRig.Bounds(tess);
            Add(hash, BitConverter.SingleToInt32Bits(sphere.x), BitConverter.SingleToInt32Bits(sphere.y),
                BitConverter.SingleToInt32Bits(sphere.z),
                BitConverter.SingleToInt32Bits(sphere.radius), BitConverter.SingleToInt32Bits(sphere.radiusY),
                BitConverter.SingleToInt32Bits(sphere.radiusZ));
            return new Digest(vertices, Convert.ToHexString(hash.GetHashAndReset()));
        }

        private static int Mesh(IncrementalHash hash, MeshData? mesh)
        {
            if (mesh is null)
            {
                Add(hash, -1);
                return 0;
            }

            int v = mesh.VerticesCount, n = mesh.IndicesCount, c = mesh.CustomInts?.Count ?? -1;
            Add(hash, v, n, c);
            hash.AppendData(MemoryMarshal.AsBytes(mesh.xyz.AsSpan(0, 3 * v)));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Uv.AsSpan(0, 2 * v)));
            hash.AppendData(mesh.Rgba.AsSpan(0, 4 * v));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Flags.AsSpan(0, v)));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan(0, n)));
            if (c > 0) hash.AppendData(MemoryMarshal.AsBytes(mesh.CustomInts!.Values.AsSpan(0, c)));
            return v;
        }

        private static void Add(IncrementalHash hash, params int[] values)
        {
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }

        public override string ToString()
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Vertices} vertices, {Hash}");
        }
    }
}

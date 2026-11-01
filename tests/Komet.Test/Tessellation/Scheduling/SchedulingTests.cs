using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Komet.Core;
using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// The same chunks tessellated by the engine's thread alone and with four of Komet's worker threads beside it, each with a tesselator of
// its own, through the patched engine tick (TessSchedule) and the engine's ChunkTesselatorManager.TesselateChunk with TessSafety,
// TessWorkers' transpiler, the fast paths and Komet's own cube and cross tesselators (OwnTessellation) installed: every chunk's meshes (vertices, uv, light, flags, indices, custom ints, bounds)
// must come out the same. The world mixes palettes of 1 to 6 bits (every block decoder), opaque and partly opaque cubes and randomly
// rotated crosses (CrossTesselator's startRot/endRot), with ambient occlusion on (FaceLight).
public sealed class TessParallelTests
{
    private const int Threads = 4, Extra = 40, Chunks = ChunkRig.ChunksX * ChunkRig.ChunksY * ChunkRig.ChunksZ,
        MaxTicks = 100000;

    private const int Cross = ChunkRig.Glass;
    private static readonly int[] OneAtlas = [0];

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Made")]
    private static extern ref ChunkTesselator?[] Made(
        [UnsafeAccessorType("Komet.Tessellation.TessWorkers, Komet.Tessellation")] object? workers);

    [Test]
    [Category("Slow")]
    public void ManyThreadsTessellateAsOne()
    {
        using var rig = new ChunkRig(Extra);
        Fill(rig);
        var manager = Manager(rig);
        var harmony = new Harmony("komet-test-tessparallel");
        var (background, priority) = (WorkerPool.Background, TessWorkers.Priority);
        TessWorkers.Priority = 100; // every pool thread at once: without a backlog the default lets fewer
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
            OwnTessellation.Installed = false;
            (WorkerPool.Background, TessWorkers.Priority) = (background, priority);
        }
    }

    private static void Install(Harmony harmony)
    {
        TessSafety.Install(harmony);
        TessSchedule.Install(harmony);
        TessWorkers.Install(harmony);
        FaceLight.Install(harmony);
        OccludedChunks.Install(harmony);
        OwnTessellation.Install(harmony);
        Assert.That((TessSafety.Installed, TessSchedule.Installed, TessWorkers.Installed, FaceLight.Installed,
            OwnTessellation.Installed), Is.EqualTo((true, true, true, true, true)));
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
        for (var i = 0; i < workers.Length; i++) Volatile.Write(ref Made(null)[i], workers[i]); // as Make would leave them
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

    private static ChunkTesselator Mesher(ChunkRig rig, ChunkTesselator tesselator)
    {
        var tesselators = new IBlockTesselator[40];
        tesselators[(int)EnumDrawType.Cube] = new CubeTesselator(1f);
        tesselators[(int)EnumDrawType.Cross] = new CrossTesselator();
        ChunkRig.Set(tesselator, "blockTesselators", tesselators);
        Assert.That(OwnTessellation.Swap(tesselator), Is.EqualTo(2), "Komet's cube and cross tesselators in the slots");
        ChunkRig.Set(tesselator, "AoAndSmoothShadows", true);
        _ = AccessTools.Method(typeof(ChunkTesselator), "UpdateForAtlasses").Invoke(tesselator, [OneAtlas]);
        Assert.That(rig.Game.FastBlockTextureSubidsByBlockAndFace, Is.Not.Null);
        return tesselator;
    }

    private static ChunkTesselatorManager Manager(ChunkRig rig)
    {
        var (manager, game) = (TessManager.Ticking(rig), rig.Game);
        ChunkRig.Set(manager, "chunksize", ChunkRig.Size);
        ChunkRig.Set(manager, "tessChunksQueueLock", new object());
        ChunkRig.Set(manager, "tessChunksQueue", new SortableQueue<TesselatedChunk>());
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        ChunkRig.Set(platform, "uptimeStopWatch", Stopwatch.StartNew());
        game.Platform = platform;
        game.FastBlockTextureSubidsByBlockAndFace = [.. rig.Blocks.Select(_ => new int[6])];
        var positions = new[] { new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 } };
        ChunkRig.Set(game.BlockAtlasManager, "TextureAtlasPositionsByTextureSubId", positions);
        rig.Map.MapChunkSize = ChunkRig.Size;
        return manager;
    }

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

    internal sealed record Digest(int Vertices, string Hash)
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

            int v = mesh.VerticesCount, n = mesh.IndicesCount, c = mesh.CustomInts?.Count ?? -1,
                s = mesh.CustomShorts?.Count ?? -1, f = mesh.CustomFloats?.Count ?? -1;
            Add(hash, v, n, c, s, f);
            hash.AppendData(MemoryMarshal.AsBytes(mesh.xyz.AsSpan(0, 3 * v)));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Uv.AsSpan(0, 2 * v)));
            hash.AppendData(mesh.Rgba.AsSpan(0, 4 * v));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Flags.AsSpan(0, v)));
            hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan(0, n)));
            if (c > 0) hash.AppendData(MemoryMarshal.AsBytes(mesh.CustomInts!.Values.AsSpan(0, c)));
            if (s > 0) hash.AppendData(MemoryMarshal.AsBytes(mesh.CustomShorts!.Values.AsSpan(0, s))); // TopSoil's overlay
            if (f > 0) hash.AppendData(MemoryMarshal.AsBytes(mesh.CustomFloats!.Values.AsSpan(0, f))); // Liquid's
            return v;
        }

        private static void Add(IncrementalHash hash, params int[] values) =>
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Vertices} vertices, {Hash}");
    }
}

public sealed class TessGovernorTests
{
    [TestCase(4, 8, 0.1, 500, 4, 5, TestName = "calm, a backlog, all busy: one more")]
    [TestCase(8, 8, 0.1, 500, 8, 8, TestName = "never past the ceiling")]
    [TestCase(4, 8, 0.1, 500, 2, 4, TestName = "threads idle: no more")]
    [TestCase(4, 8, 0.1, 10, 4, 4, TestName = "a small backlog: no more")]
    [TestCase(4, 8, 0.5, 500, 4, 4, TestName = "between calm and pressed: holds")]
    [TestCase(4, 8, 1.5, 500, 4, 3, TestName = "the main thread waits: one less")]
    [TestCase(8, 8, 6.0, 500, 8, 4, TestName = "it waits long: half")]
    [TestCase(1, 8, 9.0, 500, 1, 1, TestName = "at least one while there is work")]
    [TestCase(3, 8, 0.1, 0, 0, 2, TestName = "no backlog: eases back")]
    [TestCase(6, 2, 0.1, 500, 6, 2, TestName = "a lowered ceiling takes effect at once")]
    [TestCase(3, 0, 0.1, 500, 3, 0, TestName = "a ceiling of none: none")]
    public void TheLimitFollowsTheMainThread(int limit, int ceiling, double waitMs, int backlog, int busy, int next) =>
        Assert.That(TessGovernor.Decide(limit, ceiling, waitMs, (backlog, busy)), Is.EqualTo(next));

    // The GC's pauses as a share of the window: from 6 % one thread less, below 3 % growth as before, between them it holds
    [TestCase(4, 0.08, 3, TestName = "the GC pauses often: one less")]
    [TestCase(1, 0.20, 1, TestName = "at least one however long it pauses")]
    [TestCase(4, 0.04, 4, TestName = "between calm and pressed: holds")]
    [TestCase(4, 0.01, 5, TestName = "pauses rare: grows as before")]
    public void TheLimitFollowsTheGarbageCollector(int limit, double gcShare, int next) =>
        Assert.That(TessGovernor.Decide(limit, 8, 0.1, (500, limit), 1, gcShare), Is.EqualTo(next));

    [Test]
    public void UnmeasuredTheCeilingStands()
    {
        TessGovernor.Reset();
        Assert.That(TessGovernor.Limit(6), Is.EqualTo(6));
    }
}

public sealed class JitWarmTests
{
    // Leaving a world unpatches, but Harmony keeps an empty record: the next world must warm again
    [Test]
    public void WarmsAgainInTheNextWorld()
    {
        var method = JitWarm.Methods()[0];
        using (var first = new TestHarmony("komet-test-jitwarm")) JitWarm.Install(first, null);
        using var second = new TestHarmony("komet-test-jitwarm");
        JitWarm.Install(second, null);
        Assert.That(Harmony.GetPatchInfo(method)?.Owners, Does.Contain(second.Id));
    }
}

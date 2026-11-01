using Komet.Core;

namespace Komet.Test.Synth;

// Chunk lookups and reads: ChunkLookup, DirectChunks (its packed single block reads), LayerDelegates, ChunkThreadClosure
[Category(Synth.Category)]
public sealed class ChunkReadsSynth
{
    private const int Cells = 32 * 32 * 32;

    private static readonly MethodInfo Loaded = AccessTools.Method(typeof(Komet.Chunks.ChunkLookup), "Loaded");

    // The main thread's lookups around the player: the 27 chunks of a 3x3x3 block, all loaded, and the shell of a 4x4x4 one
    // around it (37 not loaded), by chunk coordinate, block position and index, as renderers and block accessors ask
    [Test]
    public void ChunkLookup()
    {
        var mainThread = RuntimeEnv.MainThreadId;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        var rig = new ChunkRig();
        try
        {
            var chunk = ClientChunk.CreateNew(rig.Pool);
            var chunks = (Dictionary<long, ClientChunk>)ChunkRig.Get(rig.Map, "chunks");
            var loaded = new List<(int X, int Y, int Z)>();
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    for (var z = 0; z < 3; z++)
                    {
                        chunks[ChunkRig.Key(x, y, z)] = chunk;
                        loaded.Add((x, y, z));
                    }

            var map = rig.Map;
            var (found, pos) = (0, new BlockPos(0));
            var result = Synth.Run("ChunkLookup", "main-thread-64", ops =>
            {
                found = 0;
                for (var i = 0; i < ops; i++)
                {
                    int x = i & 3, y = (i >> 2) & 3, z = (i >> 4) & 3;
                    if (SynthCalls.GetChunk(map, x, y, z) is not null) found++;
                    if (SynthCalls.GetChunk(map, pos.Set(x * 32 + 5, y * 32 + 7, z * 32 + 9)) is not null) found++;
                    if (SynthCalls.GetChunk(map, ChunkRig.Key(z, y, x)) is not null) found++;
                }
            }, 6400, () => found, () =>
            {
                // the mirror as loadChunkMT's finalizer leaves it (a no-op for the engine's lookups)
                foreach (var (x, y, z) in loaded) _ = Loaded.Invoke(null, [map, new Packet_ServerChunk { X = x, Y = y, Z = z }, chunk]);
            });
            using (new FeatureRig("ChunkLookup"))
            {
                // the on arm answers from the mirror
                foreach (var (x, y, z) in loaded) _ = Loaded.Invoke(null, [map, new Packet_ServerChunk { X = x, Y = y, Z = z }, chunk]);
                Counting.Hud = true;
                var hits = Komet.Chunks.ChunkLookup.Hits;
                _ = SynthCalls.GetChunk(map, 3, 3, 3);
                Counting.Hud = false;
                Assert.That(Komet.Chunks.ChunkLookup.Hits - hits, Is.EqualTo(1));
            }

            Assert.That(result.Feature, Is.EqualTo("ChunkLookup"));
        }
        finally
        {
            rig.Dispose();
            Komet.Chunks.ChunkLookup.Clear();
            RuntimeEnv.MainThreadId = mainThread;
        }
    }

    // Block accessor reads of packed chunks: each op reads 64 cells of the next of 8 packed chunks. The engine unpacks the chunk on
    // the first read (and SystemCompressChunks packs it again later: done in the op, so each visit finds it packed again); the views
    // read it packed.
    [Test]
    public void DirectChunks()
    {
        var rig = new ChunkRig();
        try
        {
            var r = new Random(5);
            var chunks = Enumerable.Range(0, 8).Select(_ => Filled(rig.Pool, r)).ToArray();
            foreach (var chunk in chunks) Pack(chunk);
            var cells = Enumerable.Range(0, 64).Select(_ => r.Next(Cells)).ToArray();
            long sum = 0;
            _ = Synth.Run("DirectChunks", "packed-reads", ops =>
            {
                sum = 0;
                for (var i = 0; i < ops; i++)
                {
                    var chunk = chunks[i & 7];
                    if (!chunk.IsPacked()) Pack(chunk);
                    foreach (var cell in cells) sum += SynthCalls.ReadBlock(chunk, cell);
                }
            }, 64, () => sum);
            using (new FeatureRig("DirectChunks"))
            {
                Pack(chunks[0]);
                _ = SynthCalls.ReadBlock(chunks[0], 0);
                Assert.That(chunks[0].IsPacked(), Is.True, "the on arm reads through a view");
            }
        }
        finally
        {
            rig.Dispose();
        }
    }

    // The same reads once the chunks have settled: the engine reads its unpacked chunks in place (the first read of a
    // measurement unpacks them), the views read the packed chunks through the 8 views they keep
    [Test]
    public void DirectChunksSettled()
    {
        var rig = new ChunkRig();
        try
        {
            var r = new Random(5);
            var chunks = Enumerable.Range(0, 8).Select(_ => Filled(rig.Pool, r)).ToArray();
            var cells = Enumerable.Range(0, 64).Select(_ => r.Next(Cells)).ToArray();
            long sum = 0;
            var result = Synth.Run("DirectChunks", "settled-reads", ops =>
            {
                sum = 0;
                for (var i = 0; i < ops; i++)
                {
                    var chunk = chunks[i & 7];
                    foreach (var cell in cells) sum += SynthCalls.ReadBlock(chunk, cell);
                }
            }, 640, () => sum, () =>
            {
                foreach (var chunk in chunks) Pack(chunk);
            });
            Assert.That(result.Feature, Is.EqualTo("DirectChunks"));
        }
        finally
        {
            rig.Dispose();
        }
    }

    // The chunk thread's per-tick pass over waiting requests: a column whose map chunk is a worldgen pass behind another thread
    // returns at once (the common case while the world generates), after the closure the engine allocates first thing
    [Test]
    public void ChunkThreadClosure()
    {
        var target = Komet.Chunks.ChunkThreadClosure.Target()!;
        var supplyType = target.DeclaringType!;
        var requestType = AccessTools.TypeByName("Vintagestory.Server.ChunkColumnLoadRequest");
        var server = (Vintagestory.Server.ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Server.ServerMain));
        var map = (Vintagestory.Server.ServerWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Server.ServerWorldMap));
        ChunkRig.Set(map, "mapsize", new Vec3i(1024 * 32, 256, 1024 * 32));
        server.WorldMap = map;
        var mapChunks = new System.Collections.Concurrent.ConcurrentDictionary<long, Vintagestory.Server.ServerMapChunk>();
        ChunkRig.Set(server, "loadedMapChunks", mapChunks);
        var thread = RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("Vintagestory.Server.ChunkServerThread"));
        ChunkRig.Set(thread, "additionalWorldGenThreadsCount", 2);
        var supply = RuntimeHelpers.GetUninitializedObject(supplyType);
        ChunkRig.Set(supply, "server", server);
        ChunkRig.Set(supply, "chunkthread", thread);
        var requests = new object[64];
        for (var i = 0; i < requests.Length; i++)
        {
            var mapChunk = (Vintagestory.Server.ServerMapChunk)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Server.ServerMapChunk));
            ChunkRig.Set(mapChunk, "currentpass", 1);
            mapChunks[map.MapChunkIndex2D(i, 7)] = mapChunk;
            var request = RuntimeHelpers.GetUninitializedObject(requestType);
            ChunkRig.Set(request, "chunkX", i);
            ChunkRig.Set(request, "chunkZ", 7);
            ChunkRig.Set(request, "Chunks", Array.CreateInstance(typeof(Vintagestory.Server.ServerChunk), 8));
            requests[i] = request;
        }

        var call = Caller(target);
        var went = 0;
        _ = Synth.Run("ChunkThreadClosure", "waiting-requests", ops =>
        {
            went = 0;
            for (var i = 0; i < ops; i++)
                if (call(supply, requests[i & 63], 2)) went++;
        }, 6400, () => went);
        using (new FeatureRig("ChunkThreadClosure")) Assert.That(Komet.Chunks.ChunkThreadClosure.Rewritten, Is.True);
    }

    // supply.loadOrGenerateChunkColumn_OnChunkThread(request, stage), through the method's entry (patched when patched)
    private static System.Func<object, object, int, bool> Caller(MethodInfo target)
    {
        var method = new DynamicMethod("CallChunkThread", typeof(bool), [typeof(object), typeof(object), typeof(int)],
            typeof(ChunkReadsSynth).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, target.DeclaringType!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, target.GetParameters()[0].ParameterType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<System.Func<object, object, int, bool>>();
    }

    // A chunk of 30 random block kinds (a 5-bit palette), as terrain often is
    private static ClientChunk Filled(ClientChunkDataPool pool, Random r)
    {
        var chunk = ClientChunk.CreateNew(pool);
        var data = (ChunkData)chunk.Data;
        for (var i = 0; i < Cells; i++) data[i] = r.Next(1, 30);
        return chunk;
    }

    private static void Pack(ClientChunk chunk)
    {
        chunk.lastReadOrWrite = Environment.TickCount - 60_000;
        chunk.TryPackAndCommit(0);
    }
}

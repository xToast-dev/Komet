using Vintagestory.Server;
using Vintagestory.GameContent;

namespace Komet.Test.Synth;

// Komet.World's light, snow and map features on the engine's own chunk storage
[Category(Synth.Category)]
[NonParallelizable]
public sealed class WorldChunkSynth
{
    private static readonly int[] Colors = [0x203040, 0x608020, 0x909090];

    // Block light placed and removed, blocks put into its reach and taken: LightWorld's script on its 128 x 96 x 128 world, a step per op
    [Test]
    public void LightScratch()
    {
        LightWorld? world = null;
        IEnumerator<int>? steps = null;
        using (new FeatureRig("LightScratch")) Assert.That(global::Komet.World.LightScratch.Rewritten, Is.True);
        _ = Synth.Run("LightScratch", "script", ops =>
        {
            for (var i = 0; i < ops; i++) _ = steps!.MoveNext();
        }, 200, () => world!.Hash(), () =>
        {
            world = new LightWorld(LightWorld.MakeBlocks(), 1) { Recording = false };
            world.LightUp();
            steps?.Dispose();
            steps = world.Script(8, 1 << 30).GetEnumerator();
        });
        steps?.Dispose();
    }

    // Worldgen's sunlight: a chunk column of LightWorld's hills, caves and trees lit from dark and flooded sideways (Sunlight,
    // SunlightFlood, SpreadSunLightInColumn), a column per op
    [Test]
    public void WorldGenScratch()
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), 2);
        var columns = Enumerable.Range(0, LightWorld.ChunksX * LightWorld.ChunksZ).Select(c =>
            Enumerable.Range(0, LightWorld.ChunksY)
                .Select(y => world.GetChunk(c % LightWorld.ChunksX, y, c / LightWorld.ChunksX)).ToArray()).ToArray();
        var top = LightWorld.ChunksY - 1;
        using (new FeatureRig("WorldGenScratch"))
            Assert.That(global::Komet.World.WorldGenScratch.Rewritten & 1, Is.EqualTo(1), "SpreadSunLightInColumn");
        _ = Synth.Run("WorldGenScratch", "sunColumn", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var c = i % columns.Length;
                var chunks = columns[c];
                foreach (var chunk in chunks) chunk.Lighting.ClearAllSunlight();
                world.Illuminator.Sunlight(chunks, c % LightWorld.ChunksX, top, c / LightWorld.ChunksX, 0);
                world.Illuminator.SunlightFlood(chunks, c % LightWorld.ChunksX, top, c / LightWorld.ChunksX);
            }
        }, 16, () => world.Hash());
    }

    // Particles on the render thread: 300 of them in a 12-block swarm, each reading its block's light, a read per op
    [Test]
    public void ParticleLight()
    {
        var main = RuntimeEnv.MainThreadId;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        using var rig = new ChunkRig();
        try
        {
            var map = rig.Map;
            map.SunLightLevels = [.. Enumerable.Range(0, 32).Select(l => l / 31f)];
            map.BlockLightLevels = [.. Enumerable.Range(0, 32).Select(l => l / 31f)];
            map.hueLevels = [.. Enumerable.Range(0, 64).Select(h => (byte)(h * 4))];
            map.satLevels = [.. Enumerable.Range(0, 8).Select(s => (byte)(s * 32))];
            map.SunBrightness = 24;
            for (var x = 0; x < 2; x++)
            {
                for (var z = 0; z < 2; z++)
                    _ = rig.Put(1 + x, 1, 1 + z, (_, y, _) => y < 8 ? ChunkRig.Stone : ChunkRig.Air);
            }

            var accessor = new BlockAccessorReadLockfree(map, rig.Game);
            var random = new Random(4);
            var swarm = Enumerable.Range(0, 300).Select(_ => (X: 58 + random.Next(12), Y: 44 + random.Next(12), Z: 58 + random.Next(12)))
                .ToArray();
            long sum = 0;
            using (new FeatureRig("ParticleLight")) Assert.That(global::Komet.World.ParticleLight.Installed, Is.True);
            _ = Synth.Run("ParticleLight", "swarm300", ops =>
            {
                for (var i = 0; i < ops; i++)
                {
                    var (x, y, z) = swarm[i % swarm.Length];
                    sum += accessor.GetLightRGBsAsInt(x, y, z);
                }
            }, 3000, () => sum, () =>
            {
                sum = 0;
                global::Komet.World.ParticleLight.Forget();
            });
        }
        finally
        {
            RuntimeEnv.MainThreadId = main;
        }
    }

    // The server's snow scan over a packed chunk column (eight chunks, the surface in the fourth): the engine unpacks what it reads
    // and the chunk packer packs it again; a column per op
    [Test]
    public void SnowPeek()
    {
        const int height = 8;
        var pool = new ChunkDataPool(32, null!);
        var blocks = Enumerable.Range(0, 6).Select(id => new Block
        {
            BlockId = id, Code = new AssetLocation("game", "snowsynth" + id),
            BlockMaterial = id == 0 ? EnumBlockMaterial.Air : EnumBlockMaterial.Soil, Replaceable = id == 0 ? 9999 : 100
        }).ToArray();
        var random = new Random(5);
        var surface = Enumerable.Range(0, 1024).Select(_ => (ushort)(100 + random.Next(16))).ToArray();
        var columns = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, height).Select(cy =>
        {
            var chunk = new PackChunk(pool);
            for (var i = 0; i < 32 * 32 * 32; i++)
            {
                var (x, y, z) = (i % 32, cy * 32 + i / 1024, i / 32 % 32);
                var h = surface[z * 32 + x];
                if (y <= h) chunk.Cells.SetBlockUnsafe(i, y == h ? 2 + (x + z) % 3 : 1);
            }

            chunk.TryPackAndCommit(0);
            return chunk;
        }).ToArray()).ToArray();
        Assert.That(columns.SelectMany(c => c).All(c => c.IsPacked()), Is.True, "packed");
        // the engine's own server objects: a stand-in's every call would allocate more than the scan itself
        var server = (ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));
        server.Blocks = blocks;
        AccessTools.Field(typeof(ServerMain), "seaLevel").SetValue(server, 110);
        server.rand = new ThreadLocal<Random>(() => new Random(6));
        var worldMap = (ServerWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldMap));
        AccessTools.Field(typeof(ServerWorldMap), "mapsize").SetValue(worldMap, new Vec3i(1 << 20, height * 32, 1 << 20));
        worldMap.RelaxedBlockAccess = new BlockAccessorRelaxed(worldMap, server, false, false);
        server.WorldMap = worldMap;
        var sapi = (ServerCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ServerCoreAPI));
        AccessTools.Field(typeof(ServerCoreAPI), "server").SetValue(sapi, server);
        var weather = (WeatherSystemServer)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSystemServer));
        weather.GeneralConfig = new WeatherSystemConfig { SnowLayerBlocks = new() { [blocks[5]] = 0 } };
        var snow = (WeatherSimulationSnowAccum)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSimulationSnowAccum));
        AccessTools.Field(typeof(WeatherSimulationSnowAccum), "sapi").SetValue(snow, sapi);
        AccessTools.Field(typeof(WeatherSimulationSnowAccum), "ws").SetValue(snow, weather);
        AccessTools.Field(typeof(WeatherSimulationSnowAccum), "regionsize").SetValue(snow, 512);
        _ = AccessTools.Method(typeof(WeatherSimulationSnowAccum), "initRandomShuffles").Invoke(snow, []);
        var snapshot = new SnowAccumSnapshot { SnowAccumulationByRegionCorner = new FloatDataMap3D(2, 2, 2) };
        var map = (ServerMapChunk)RuntimeHelpers.GetUninitializedObject(typeof(ServerMapChunk));
        map.RainHeightMap = surface;
        var pos = new Vec2i(16000, 16000);
        var set = 0;
        using (new FeatureRig("SnowPeek")) Assert.That(global::Komet.World.SnowPeek.Rewritten, Is.True);
        _ = Synth.Run("SnowPeek", "packedColumn", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var column = columns[i % columns.Length];
                set += snow.UpdateSnowLayer(snapshot, true, map, pos, column)?.SetBlocks.Count ?? -1;
                foreach (var chunk in column)
                    if (!chunk.IsPacked())
                        chunk.TryPackAndCommit(0);
            }
        }, 8, () => set, () => set = 0);
        Assert.That(columns.SelectMany(c => c).All(c => c.IsPacked()), Is.True);
    }

    // The client's map thread draws a map piece per chunk column it receives: GenerateChunkImage over a 4 chunk high column whose
    // surface lies in its second chunk, a piece per op. MapPixels' two holders (the save and the tile upload) hand each piece back
    // as MapSaveScratch and MapTileUpload would, in both arms.
    [Test]
    public void MapPixels()
    {
        using var rig = new ChunkRig();
        var random = new Random(7);
        var heights = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, 1024).Select(_ => (ushort)(36 + random.Next(20))).ToArray())
            .ToArray();
        var mapChunks = heights.Select(h =>
        {
            var mapChunk = (ClientMapChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientMapChunk));
            AccessTools.Field(typeof(ClientMapChunk), "rainheightmap").SetValue(mapChunk, h); // read per pixel: the engine's own
            return (IMapChunk)mapChunk;
        }).ToArray();
        for (var c = 0; c < 4; c++)
        {
            var h = heights[c];
            for (var cy = 0; cy < ChunkRig.ChunksY; cy++)
            {
                var y0 = cy * 32;
                _ = rig.Put(c, cy, 1, (x, y, z) => Surface(y0 + y, h[z * 32 + x]));
            }
        }

        var accessor = Answers.Of<IBlockAccessor>(new()
        {
            ["GetChunk"] = args => args!.Length == 3 ? rig.At((int)args[0]!, (int)args[1]!, (int)args[2]!) : null,
            ["GetMapChunk"] = args => (int)args![1]! == 1 && (int)args[0]! is >= 0 and < 4 ? mapChunks[(int)args[0]!] : null,
            ["get_MapSizeY"] = _ => ChunkRig.ChunksY * 32
        });
        var world = Answers.Of<IClientWorldAccessor>(new() { ["get_BlockAccessor"] = _ => accessor });
        var capi = Answers.Of<ICoreClientAPI>(new() { ["get_World"] = _ => world });
        var layer = (ChunkMapLayer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkMapLayer));
        AccessTools.Field(typeof(ChunkMapLayer), "capi").SetValue(layer, capi);
        var core = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
        AccessTools.Field(typeof(ClientCoreAPI), "game").SetValue(core, rig.Game); // api.World.Blocks per pixel: the game's own list
        AccessTools.Field(typeof(ChunkMapLayer), "api").SetValue(layer, core);
        AccessTools.Field(typeof(ChunkMapLayer), "chunksTmp").SetValue(layer, new IWorldChunk[ChunkRig.ChunksY]);
        AccessTools.Field(typeof(ChunkMapLayer), "colors").SetValue(layer, Colors);
        layer.block2Color = [.. Enumerable.Range(0, rig.Blocks.Length).Select(id => (byte)(id % 3))];
        long sum = 0;
        using (new FeatureRig("MapPixels")) Assert.That(global::Komet.World.MapPixels.Rewritten, Is.True);
        _ = Synth.Run("MapPixels", "piece", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var c = i & 3;
                var pixels = layer.GenerateChunkImage(new FastVec2i(c, 1), mapChunks[c]);
                sum += pixels[i & 1023];
                global::Komet.World.MapPixels.Release(pixels); // saved
                global::Komet.World.MapPixels.Release(pixels); // uploaded
            }
        }, 200, () => sum, () => sum = 0);
    }

    // Snow settling on grass, leaves and crops changing: block changes that alter no light, each queued as a lighting task the client's
    // relight thread processes (ProcessLightingTask), a task per op. The engine's task marks the chunk dirty once more, which the
    // tesselator then rebuilds; that rebuild is the cost the feature avoids and lies outside this scenario.
    [Test]
    public void RelightRedraws()
    {
        using var rig = new ChunkRig();
        for (var x = 0; x < ChunkRig.ChunksX; x++)
        {
            for (var z = 0; z < ChunkRig.ChunksZ; z++)
                _ = rig.Put(x, 1, z, (_, y, _) => y < 10 ? ChunkRig.Stone : ChunkRig.Air);
        }

        var game = rig.Game;
        game.WorldMap = rig.Map;
        var queue = new UniqueQueue<long>();
        AccessTools.Field(typeof(ClientMain), "dirtyChunks").SetValue(game, queue);
        AccessTools.Field(typeof(ClientMain), "dirtyChunksLock").SetValue(game, new object());
        AccessTools.Field(typeof(ClientMain), "dirtyChunksPriorityLock").SetValue(game, new object());
        AccessTools.Field(typeof(ClientMain), "dirtyChunksPriority").SetValue(game, new UniqueQueue<long>());
        var relight = (ClientSystemRelight)RuntimeHelpers.GetUninitializedObject(typeof(ClientSystemRelight));
        AccessTools.Field(typeof(ClientSystem), "game").SetValue(relight, game);
        var illuminator = new ChunkIlluminator(rig.Map, null!, 32);
        illuminator.InitForWorld(rig.Blocks, 24, ChunkRig.ChunksX * 32, ChunkRig.ChunksY * 32, ChunkRig.ChunksZ * 32);
        AccessTools.Field(typeof(ClientSystemRelight), "chunkIlluminator").SetValue(relight, illuminator);
        var process = AccessTools.Method(typeof(ClientSystemRelight), "ProcessLightingTask")
            .CreateDelegate<Action<EntityPos?, UpdateLightingTask>>(relight);
        var random = new Random(8);
        var tasks = Enumerable.Range(0, 256).Select(_ => new UpdateLightingTask
        {
            pos = new BlockPos(random.Next(128), 32 + 9, random.Next(128), 0), oldBlockId = ChunkRig.Stone, newBlockId = ChunkRig.Granite
        }).ToArray();
        var dirty = 0;
        using (new FeatureRig("RelightRedraws")) Assert.That(global::Komet.World.RelightRedraws.Installed, Is.True);
        _ = Synth.Run("RelightRedraws", "lightlessChange", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                process(null, tasks[i & 255]);
                if ((i & 63) != 63) continue;
                dirty += queue.Count;
                queue.Clear(); // the tesselator took them
            }
        }, 2048, null, () =>
        {
            dirty = 0;
            queue.Clear();
        });
        TestContext.Out.WriteLine($"chunks marked dirty: {dirty}");
    }

    private static int Surface(int y, int height)
    {
        if (y < height) return ChunkRig.Stone;
        return y == height ? ChunkRig.Granite : ChunkRig.Air;
    }

    // A chunk on the engine's own storage that the test can fill and pack
    private sealed class PackChunk : WorldChunk
    {
        public PackChunk(ChunkDataPool pool)
        {
            datapool = pool;
            chunkdata = ChunkData.CreateNew(32, pool);
            MaybeBlocks = chunkdata;
            PotentialBlockOrLightingChanges = true; // as after an unpack: packing compresses
        }

        public ChunkData Cells => chunkdata;
        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];
    }
}

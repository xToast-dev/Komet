using HarmonyLib;
using Komet.Api;
using Komet.Core;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.World;

// A chunk without light is drawn black and treated as a cave. The engine lights only generated columns, and FullRelight
// clears its area but relights only whole columns, so the rest are saved unlit (bench world: 2 800 of 5 200). Broken:
// no light yet partly above the rain height. Server main thread: sunlight before the column enters the map, block light
// after; dimension 0.
internal static partial class LightRepair
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x5C83B386ED98073EUL;

    private const string Supply = "Vintagestory.Server.ServerSystemSupplyChunks", Load = "mainThreadLoadChunkColumn";
    private const string Unload = "Vintagestory.Server.ServerSystemUnloadChunks";
    private const int Size = 32, MaxChunks = 64, Neighbourhood = 1, MaxSources = 1 << 16;

    private static ILogger? _logger;
    private static bool _shaped, _failed;

    private static ChunkColumnLoadRequest? _lit;
    private static int[] _broken = [];
    private static byte _flooded;
    private static long _columns, _chunks; // repaired, for the log at each power of ten

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => _shaped;
    internal static bool Failed => _failed;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "server")]
    private static extern ref ServerMain Server(ServerSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Chunks")]
    private static extern ref ServerChunk[] Column(ChunkColumnLoadRequest request);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "MapChunk")]
    private static extern ref ServerMapChunk MapChunk(ChunkColumnLoadRequest request);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimension")]
    private static extern ref int Dimension(ChunkColumnLoadRequest request);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockTypes")]
    private static extern ref IList<Block> BlockTypes(ChunkIlluminator illuminator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "readBlockAccess")]
    private static extern ref IBlockAccessor ReadAccess(ChunkIlluminator illuminator);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, _failed, _foreign, _logger, _lit, _columns, _chunks) = (false, false, false, logger, null, 0, 0);
        (_sweep, _cursor, _scratch) = ([], 0, []);
        Waiting.Clear();
        var seams = Seams();
        if (!NotNull(harmony) || seams[0] is not MethodInfo load ||
            seams[1] is not MethodInfo relight || seams[6] is not MethodInfo tick) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(LightRepair), logger);
        if (!_shaped) return;
        _ = harmony.Patch(load, new HarmonyMethod(Before), new HarmonyMethod(After));
        _ = harmony.Patch(relight, new HarmonyMethod(Relight));
        _ = harmony.Patch(tick, postfix: new HarmonyMethod(Tick));
        Recheck();
    }

    // ChunkData.DecompressFrom is in the shape: Missing relies on it reading the empty arrays as no light
    internal static MethodBase?[] Seams()
    {
        var (illuminator, column) = (typeof(ChunkIlluminator), typeof(IWorldChunk[]));
        var (bytes, i, at) = (typeof(byte[]), typeof(int), typeof(BlockPos));
        var (supply, unload) = (AccessTools.TypeByName(Supply), AccessTools.TypeByName(Unload));
        MethodBase?[] seams =
        [
            supply is null ? null : AccessTools.DeclaredMethod(supply, Load, [typeof(ChunkColumnLoadRequest)]),
            AccessTools.DeclaredMethod(illuminator, nameof(ChunkIlluminator.FullRelight), [at, at]),
            AccessTools.DeclaredMethod(illuminator, nameof(ChunkIlluminator.Sunlight), [column, i, i, i, i]),
            AccessTools.DeclaredMethod(illuminator, nameof(ChunkIlluminator.SunlightFlood), [column, i, i, i]),
            AccessTools.DeclaredMethod(illuminator, nameof(ChunkIlluminator.SunLightFloodNeighbourChunks),
                [column, i, i, i, i]),
            AccessTools.DeclaredMethod(typeof(ChunkData), "DecompressFrom", [bytes, bytes, bytes, bytes, i]),
            unload is null
                ? null
                : AccessTools.DeclaredMethod(unload, nameof(ServerSystem.OnServerTick), [typeof(float)])
        ];
        return seams;
    }

    internal static void Before(ServerSystem __instance, ChunkColumnLoadRequest chunkRequest)
    {
        _lit = null;
        if (!Enabled || _failed || !NotNull(chunkRequest) || Dimension(chunkRequest) != 0)
            return;
        try
        {
            var (server, chunks) = (Server(__instance), Column(chunkRequest));
            if (server?.WorldMap?.chunkIlluminatorMainThread is not { } illuminator || chunks is not { Length: > 0 })
                return;
            var broken = Broken(chunks, ((IMapChunk?)MapChunk(chunkRequest))?.RainHeightMap);
            if (broken.Length == 0) return;
            _flooded = Sunlight(illuminator, chunks, chunkRequest.ChunkX, chunkRequest.ChunkZ, 0);
            (_lit, _broken) = (chunkRequest, broken);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    internal static void After(ServerSystem __instance, ChunkColumnLoadRequest chunkRequest)
    {
        if (_lit is null || !ReferenceEquals(_lit, chunkRequest)) return;
        _lit = null;
        try
        {
            if (Server(__instance)?.WorldMap is not { chunkIlluminatorMainThread: { } illuminator } map) return;
            var (x, z) = (chunkRequest.ChunkX, chunkRequest.ChunkZ);
            var touched = BlockLight(illuminator, map, x, z, _broken, 0);
            Report(map, x, z, 0, map.ChunkMapSizeY, _flooded, touched, (cx, cy, cz) => Changed(map, cx, cy, cz));
            Count(_broken.Length);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    private static void Fail(Exception e)
    {
        _failed = true;
        _logger?.Error(
            "Komet: lighting chunk columns loaded without light failed, they stay as loaded from now on: {0}", e);
    }

    private static void Count(int chunks)
    {
        (_columns, _chunks) = (_columns + 1, _chunks + chunks);
        var power = 1L;
        for (var i = 0; i < 18 && power < _columns; i++) power *= 10;
        if (power == _columns)
            _logger?.Notification("Komet: lit {0} chunk columns that had lost their light ({1} chunks)", _columns,
                _chunks);
    }

    private static void Changed(ServerWorldMap map, int x, int y, int z)
    {
        if (map.GetChunk(x, y, z) is not { } chunk) return;
        chunk.MarkModified();
        map.MarkChunkDirty(x, y, z);
    }

    // Chunks without light reaching above the rain height; none when a chunk is missing; no rain map: all reach the sky.
    // The shared empty array when none is broken, as for nearly every column the sweep looks at.
    internal static int[] Broken(IWorldChunk[] chunks, ushort[]? rain)
    {
        if (chunks.Length > MaxChunks) return [];
        Span<int> missing = stackalloc int[MaxChunks];
        var count = 0;
        for (var y = 0; y < Math.Min(chunks.Length, MaxChunks); y++)
        {
            if (chunks[y] is not { } chunk) return [];
            if (Missing(chunk)) missing[count++] = y;
        }

        if (count == 0) return [];
        // the lowest cell above a rain height is lowest + 1, the top cell of chunk y is y * Size + Size - 1
        var lowest = rain is { Length: Size * Size } ? rain.Min() : 0;
        var kept = 0;
        for (var i = 0; i < Math.Min(count, MaxChunks); i++)
            if (lowest + 1 < (missing[i] + 1) * Size) missing[kept++] = missing[i];
        return missing[..kept].ToArray();
    }

    // No light layer, or, packed, the arrays DecompressFrom turns into none
    internal static bool Missing(IWorldChunk chunk)
    {
        if (chunk is not WorldChunk { } stored || !stored.IsPacked()) return chunk.Lighting?.IsNull ?? true;
        return stored.lightCompressed is not { Length: > 0 } || stored.lightSatCompressed is not { Length: > 0 };
    }

    // FullRelight's sunlight for one column, returning the faces reached; copied, as a span over a ServerChunk[] throws
    internal static byte Sunlight(ChunkIlluminator illuminator, IWorldChunk[] chunks, int x, int z, int dimension)
    {
        if (!Assert(chunks.Length is > 0 and <= MaxChunks)) return 0;
        IWorldChunk[] column = [.. chunks];
        var top = column.Length - 1;
        foreach (var chunk in column.Bounded(MaxChunks)) chunk.Unpack();
        illuminator.Sunlight(column, x, top, z, dimension);
        illuminator.SunlightFlood(column, x, top, z);
        return illuminator.SunLightFloodNeighbourChunks(column, x, top, z, dimension);
    }

    // Replaces every source near a repaired chunk whose light can reach it; returns the chunks changed
    internal static HashSet<long> BlockLight(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z,
        int[] broken, int dimension)
    {
        var touched = new HashSet<long>();
        var (blocks, access) = (BlockTypes(illuminator), ReadAccess(illuminator));
        var placed = new HashSet<long>();
        foreach (var y in broken.Bounded(MaxChunks))
        {
            for (var i = 0; i < 27; i++)
            {
                var (sx, sy, sz) =
                    (x + i % 3 - Neighbourhood, y + i / 3 % 3 - Neighbourhood, z + i / 9 - Neighbourhood);
                var sourceY = sy + dimension * 1024;
                if (sy < 0 || chunks.GetChunk(sx, sourceY, sz) is not { LightPositions.Count: > 0 } source) continue;
                source.Unpack();
                foreach (var at in source.LightPositions.ToArray().Bounded(MaxSources))
                {
                    var pos = At(sx, sy, sz, at, dimension);
                    var hsv = blocks[source.Data[at]].GetLightHsv(access, pos);
                    // once per source that reaches this chunk
                    if (hsv is not { Length: 3 } || Reach(pos, x, y, z) >= hsv[2] ||
                        !placed.Add(chunks.ChunkIndex3D(sx, sourceY, sz) * Size * Size * Size + at)) continue;
                    touched.UnionWith(illuminator.PlaceBlockLight(hsv, pos.X, pos.InternalY, pos.Z));
                }
            }
        }

        return touched;
    }

    // Manhattan distance from a source to the chunk's nearest cell; light loses one per step
    internal static int Reach(BlockPos source, int x, int y, int z)
    {
        var (dx, dy, dz) = (Gap(source.X, x * Size), Gap(source.Y, y * Size), Gap(source.Z, z * Size));
        return dx + dy + dz;
    }

    private static int Gap(int at, int low) => Math.Max(0, Math.Max(low - at, at - (low + Size - 1)));
}

// FullRelight skips columns not loaded whole and places sources at twice the chunk offset. The prefix runs the same
// relight without those faults, on the server's own illuminator; partial columns wait for the sweep. A foreign
// FullRelight patch leaves it to the engine.
internal static partial class LightRepair
{
    private const int MaxSpan = 1024, MaxWaiting = 4096;

    private static readonly Queue<(int Dimension, int X, int Z)> Waiting = [];
    private static bool _foreign;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkProvider")]
    private static extern ref IChunkProvider Provider(ChunkIlluminator illuminator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkSize")]
    private static extern ref int ChunkSize(ChunkIlluminator illuminator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizex")]
    private static extern ref int MapX(ChunkIlluminator illuminator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizey")]
    private static extern ref int MapY(ChunkIlluminator illuminator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizez")]
    private static extern ref int MapZ(ChunkIlluminator illuminator);

    // Asked again on LevelFinalize (RunGame in the server child): another mod's FullRelight stays the engine's
    internal static void Recheck()
    {
        var relight = Seams() is { Length: > 1 } seams ? seams[1] : null;
        _foreign = EngineShape.Report(_logger, nameof(LightRepair), _foreign,
            _shaped && EngineShape.Foreign([relight], PatchKinds.Replacing, null, typeof(LightRepair)));
    }

    internal static bool Relight(ChunkIlluminator __instance, BlockPos minPos, BlockPos maxPos)
    {
        if (!Enabled || _failed || _foreign || !NotNull(minPos) || !NotNull(maxPos) ||
            Provider(__instance) is not ServerWorldMap) return true;
        try
        {
            return !FullRelight(__instance, minPos, maxPos);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
            return true;
        }
    }

    // The engine's FullRelight without the two faults; false, unchanged, for an unsupported world
    internal static bool FullRelight(ChunkIlluminator illuminator, BlockPos minPos, BlockPos maxPos)
    {
        var (size, provider) = (ChunkSize(illuminator), Provider(illuminator));
        if (!Assert(size == Size) || !NotNull(provider)) return false;
        var (dimension, height) = (minPos.dimension, MapY(illuminator) / Size);
        if (!Assert(height is > 0 and <= MaxChunks)) return false;
        var (mapX, mapY, mapZ) = (MapX(illuminator), MapY(illuminator), MapZ(illuminator));
        var from = (X: Edge(minPos.X, maxPos.X, mapX, false), Y: Edge(minPos.Y, maxPos.Y, mapY, false),
            Z: Edge(minPos.Z, maxPos.Z, mapZ, false));
        var to = (X: Edge(minPos.X, maxPos.X, mapX, true), Y: Edge(minPos.Y, maxPos.Y, mapY, true),
            Z: Edge(minPos.Z, maxPos.Z, mapZ, true));
        var loaded = new Dictionary<Vec3i, IWorldChunk>();
        for (var i = 0; i < Math.Min(to.X - from.X + 1, MaxSpan); i++)
        {
            for (var j = 0; j < Math.Min(to.Y - from.Y + 1, MaxSpan); j++)
            {
                for (var k = 0; k < Math.Min(to.Z - from.Z + 1, MaxSpan); k++)
                {
                    var at = new Vec3i(from.X + i, from.Y + j, from.Z + k);
                    if (provider.GetChunk(at.X, at.Y + dimension * 1024, at.Z) is not { } chunk) continue;
                    chunk.Unpack();
                    loaded[at] = chunk;
                }
            }
        }

        var whole = Whole(provider, from, to, dimension, height, loaded);
        foreach (var (key, chunk) in loaded.Bounded(MaxSources))
            if (whole.ContainsKey((key.X, key.Z)))
                chunk.Lighting.ClearLight();
        foreach (var ((x, z), column) in whole.Bounded(MaxSources))
        {
            illuminator.Sunlight(column, x, height - 1, z, dimension);
            illuminator.SunlightFlood(column, x, height - 1, z);
            _ = illuminator.SunLightFloodNeighbourChunks(column, x, height - 1, z, dimension);
        }

        Sources(illuminator, loaded, dimension);
        return true;
    }

    // Whole columns of the area in the engine's order; a partial one holding an area chunk waits
    private static Dictionary<(int X, int Z), IWorldChunk[]> Whole(IChunkProvider provider, (int X, int Y, int Z) from,
        (int X, int Y, int Z) to, int dimension, int height, Dictionary<Vec3i, IWorldChunk> loaded)
    {
        var whole = new Dictionary<(int X, int Z), IWorldChunk[]>();
        for (var i = 0; i < Math.Min(to.X - from.X + 1, MaxSpan); i++)
        {
            for (var k = 0; k < Math.Min(to.Z - from.Z + 1, MaxSpan); k++)
            {
                var (x, z) = (from.X + i, from.Z + k);
                var column = new IWorldChunk[height];
                if (Column(provider, x, z, dimension, column) == height) whole[(x, z)] = column;
                else if (loaded.Keys.Any(key => key.X == x && key.Z == z) && Waiting.Count < MaxWaiting &&
                         !Waiting.Contains((dimension, x, z)))
                    Waiting.Enqueue((dimension, x, z));
            }
        }

        return whole;
    }

    // Every light source of the area's chunks placed again, in the engine's order, where it is
    private static void Sources(ChunkIlluminator illuminator, Dictionary<Vec3i, IWorldChunk> loaded, int dimension)
    {
        var (blocks, access) = (BlockTypes(illuminator), ReadAccess(illuminator));
        if (!NotNull(blocks)) return;
        var sources = new Dictionary<BlockPos, Block>();
        foreach (var (key, chunk) in loaded.Bounded(MaxSources))
        {
            foreach (var at in chunk.LightPositions.Bounded(MaxSources))
                sources[At(key.X, key.Y, key.Z, at, dimension)] = blocks[chunk.Data[at]];
        }

        foreach (var (pos, block) in sources.Bounded(MaxSources))
            _ = illuminator.PlaceBlockLight(block.GetLightHsv(access, pos), pos.X, pos.InternalY, pos.Z);
    }

    // A chunk index at the area's low or high end, as the engine clamps it: one chunk around, within the map
    private static int Edge(int a, int b, int map, bool high)
    {
        if (!Assert(map > 0)) return 0;
        return GameMath.Clamp(high ? Math.Max(a, b) + Size : Math.Min(a, b) - Size, 0, map - 1) / Size;
    }

    // The block of a chunk's light source: the chunk's offset and the position within it, once
    internal static BlockPos At(int x, int y, int z, int at, int dimension)
    {
        _ = Index(at, Size * Size * Size); // a light position lies within its chunk
        return new BlockPos(x * Size + at % Size, y * Size + at / (Size * Size), z * Size + at / Size % Size,
            dimension);
    }
}

// Catches what the load misses (broken while loaded, loaded before install, left by a relight); every 200 ms,
// MaxRepairs per tick.
internal static partial class LightRepair
{
    private const int ColumnsPerTick = 256, MaxRepairs = 2;

    private static long[] _sweep = [];
    private static int _cursor;
    private static IWorldChunk[] _scratch = [];

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RunPhase")]
    private static extern ref EnumServerRunPhase RunPhase(ServerMain server);

    internal static void Tick(ServerSystem __instance)
    {
        if (!Enabled || _failed) return;
        try
        {
            var server = Server(__instance);
            if (server?.WorldMap is { } map && RunPhase(server) == EnumServerRunPhase.RunGame) Sweep(server, map);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    private static void Sweep(ServerMain server, ServerWorldMap map)
    {
        var illuminator = map.chunkIlluminatorMainThread;
        if (!NotNull(illuminator)) return;
        Action<int, int, int> changed = (x, y, z) => Changed(map, x, y, z);
        try
        {
            var repairs = RelightWaiting(illuminator, map, MaxRepairs, changed);
            if (_cursor >= _sweep.Length) (_sweep, _cursor) = (server.LoadedMapChunkIndices, 0);
            for (var i = 0; i < Math.Min(_sweep.Length - _cursor, ColumnsPerTick) && repairs < MaxRepairs; i++)
            {
                var index = _sweep[_cursor++];
                var (x, z) = ((int)(index % map.ChunkMapSizeX), (int)(index / map.ChunkMapSizeX));
                if (Repair(illuminator, map, x, z, map.GetMapChunk(x, z)?.RainHeightMap, changed) > 0) repairs++;
            }
        }
        finally
        {
            Array.Clear(_scratch); // no chunk held past the tick, also when a repair threw and Fail stopped the sweep
        }
    }

    // A partly loaded column waits, an unloaded one is dropped; unpacked first, as a packed one has no Lighting
    internal static int RelightWaiting(ChunkIlluminator illuminator, IChunkProvider chunks, int max,
        Action<int, int, int> changed)
    {
        var (done, count) = (0, Waiting.Count);
        for (var i = 0; i < Math.Min(count, ColumnsPerTick) && done < max; i++)
        {
            var (dimension, x, z) = Waiting.Dequeue();
            var column = Scratch(illuminator);
            var loaded = Column(chunks, x, z, dimension, column);
            if (loaded > 0 && loaded < column.Length) Waiting.Enqueue((dimension, x, z));
            if (loaded < column.Length) continue;
            foreach (var chunk in column.Bounded(MaxChunks))
            {
                chunk.Unpack();
                chunk.Lighting.ClearLight();
            }

            Relit(illuminator, chunks, x, z, dimension, column, [.. Enumerable.Range(0, column.Length)], changed);
            done++;
        }

        return done;
    }

    // The column's broken chunks relit, read once into the scratch column
    internal static int Repair(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z, ushort[]? rain,
        Action<int, int, int> changed)
    {
        var column = Scratch(illuminator);
        if (Column(chunks, x, z, 0, column) < column.Length) return 0;
        var broken = Broken(column, rain);
        if (broken.Length == 0) return 0;
        Relit(illuminator, chunks, x, z, 0, column, broken, changed);
        return broken.Length;
    }

    private static IWorldChunk[] Scratch(ChunkIlluminator illuminator)
    {
        var height = Math.Min(MapY(illuminator) / Size, MaxChunks);
        if (_scratch.Length != height) _scratch = new IWorldChunk[height];
        return _scratch;
    }

    private static int Column(IChunkProvider chunks, int x, int z, int dimension, IWorldChunk[] column)
    {
        if (column.Length == 0) return -1;
        var loaded = 0;
        for (var y = 0; y < Math.Min(column.Length, MaxChunks); y++)
        {
            column[y] = chunks.GetChunk(x, y + dimension * 1024, z);
            if (column[y] is not null) loaded++;
        }

        return loaded;
    }

    private static void Relit(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z, int dimension,
        IWorldChunk[] column, int[] lit, Action<int, int, int> changed)
    {
        var flooded = Sunlight(illuminator, column, x, z, dimension);
        var touched = BlockLight(illuminator, chunks, x, z, lit, dimension);
        Report(chunks, x, z, dimension, column.Length, flooded, touched, changed);
        Count(lit.Length);
    }

    private static void Report(IChunkProvider chunks, int x, int z, int dimension, int height, byte flooded,
        HashSet<long> touched, Action<int, int, int> changed)
    {
        var offset = dimension * 1024;
        for (var y = 0; y < Math.Min(height, MaxChunks); y++) changed(x, y + offset, z);
        foreach (var face in BlockFacing.HORIZONTALS.Bounded(4))
        {
            if ((flooded & face.Flag) == 0) continue;
            for (var y = 0; y < Math.Min(height, MaxChunks); y++)
                changed(x + face.Normali.X, y + offset, z + face.Normali.Z);
        }

        foreach (var index in touched.Bounded(MaxSources))
        {
            if (chunks is not WorldMap map) break;
            var pos = map.ChunkPosFromChunkIndex3D(index);
            changed(pos.X, pos.InternalY, pos.Z);
        }
    }
}

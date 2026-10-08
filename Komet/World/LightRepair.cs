using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace Komet.World;

// A chunk without a light layer reads light 0 everywhere: the client draws it black and the server treats it as a cave. The engine
// computes light when it generates a column, never when it loads one, and ChunkIlluminator.FullRelight (WorldEdit, /debug relight,
// the timeswitch, /wgen) clears the light of every loaded chunk in its area but relights only the columns loaded whole, so the others
// are saved without light. The bench world loaded 2 800 of 5 200 columns with such chunks. A chunk has lost its light when it has none
// (no layer, or the empty arrays CompressInto writes for none) although a cell of it lies above its column's rain height, which the
// sun reaches in any lit world; one wholly below may lack light rightly.
//
// All on the server's main thread. Sunlight goes in before the column enters the map (mainThreadLoadChunkColumn), so nothing can send
// it unlit; block light is placed again after, now that the column is in the map. A chunk that kept its light keeps it: Sunlight only
// rewrites cells down to where the light is absorbed, the floods only raise, block light is recomputed from the sources. The load and
// the sweep take dimension 0; a relight keeps the dimension it was asked for.
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

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => _shaped;
    internal static bool Failed => _failed;

    public static long Columns { get; private set; }
    public static long Chunks { get; private set; }

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
        (_shaped, _failed, _foreign, _logger, _lit, Columns, Chunks) = (false, false, false, logger, null, 0, 0);
        (_sweep, _cursor) = ([], 0);
        Waiting.Clear();
        var seams = Seams();
        if (!NotNull(harmony) || !Assert(seams.Length == 7) || seams[0] is not MethodInfo load ||
            seams[1] is not MethodInfo relight || seams[6] is not MethodInfo tick) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(LightRepair), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(Before), new HarmonyMethod(After)));
        _ = NotNull(harmony.Patch(relight, new HarmonyMethod(Relight)));
        _ = NotNull(harmony.Patch(tick, postfix: new HarmonyMethod(Tick)));
        Recheck();
    }

    // ChunkData.DecompressFrom is part of the shape because Missing relies on it reading the empty arrays as no light
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
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static void Before(ServerSystem __instance, ChunkColumnLoadRequest chunkRequest)
    {
        _lit = null;
        if (!Enabled || _failed || !NotNull(__instance) || !NotNull(chunkRequest) || Dimension(chunkRequest) != 0)
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

    // A failed repair leaves the chunks as the engine loaded them: no light where the sun has not been put yet
    private static void Fail(Exception e)
    {
        _failed = true;
        _logger?.Error(
            "Komet: lighting chunk columns loaded without light failed, they stay as loaded from now on: {0}", e);
    }

    private static void Count(int chunks)
    {
        (Columns, Chunks) = (Columns + 1, Chunks + chunks);
        var power = 1L;
        for (var i = 0; i < 18 && power < Columns; i++) power *= 10;
        if (Assert(Columns > 0) && power == Columns)
            _logger?.Notification("Komet: lit {0} chunk columns that had lost their light ({1} chunks)", Columns,
                Chunks);
    }

    private static void Changed(ServerWorldMap map, int x, int y, int z)
    {
        if (map.GetChunk(x, y, z) is not { } chunk) return;
        chunk.MarkModified();
        map.MarkChunkDirty(x, y, z);
    }

    // The chunks of the column that have no light although a cell of them lies above the column's rain height; none when a chunk
    // is missing. Without a rain map every chunk counts as reaching the sky.
    internal static int[] Broken(IWorldChunk[] chunks, ushort[]? rain)
    {
        if (!NotNull(chunks) || chunks.Length > MaxChunks) return [];
        // the lowest cell above a rain height is lowest + 1, the top cell of chunk y is y * Size + Size - 1
        var lowest = rain is { Length: Size * Size } ? rain.Min() : 0;
        var broken = new List<int>();
        for (var y = 0; y < Math.Min(chunks.Length, MaxChunks); y++)
        {
            if (chunks[y] is not { } chunk) return [];
            if (lowest + 1 < (y + 1) * Size && Missing(chunk)) broken.Add(y);
        }

        return Assert(broken.Count <= chunks.Length) ? [.. broken] : [];
    }

    // No light layer, or, packed, the arrays DecompressFrom turns into none
    internal static bool Missing(IWorldChunk chunk)
    {
        if (!NotNull(chunk)) return false;
        if (chunk is not WorldChunk { } stored || !stored.IsPacked()) return chunk.Lighting?.IsNull ?? true;
        return stored.lightCompressed is not { Length: > 0 } || stored.lightSatCompressed is not { Length: > 0 };
    }

    // FullRelight's sunlight for one whole column: the neighbour faces the flood reached. The load hands over a ServerChunk[], which
    // an IWorldChunk[] parameter takes by array covariance, and a span over it (Bounded) would throw: the column is copied first.
    internal static byte Sunlight(ChunkIlluminator illuminator, IWorldChunk[] chunks, int x, int z, int dimension)
    {
        if (!NotNull(illuminator) || !NotNull(chunks) || !Assert(chunks.Length is > 0 and <= MaxChunks)) return 0;
        IWorldChunk[] column = [.. chunks];
        var top = column.Length - 1;
        foreach (var chunk in column.Bounded(MaxChunks)) chunk.Unpack();
        illuminator.Sunlight(column, x, top, z, dimension);
        illuminator.SunlightFlood(column, x, top, z);
        return illuminator.SunLightFloodNeighbourChunks(column, x, top, z, dimension);
    }

    // Every light source in the chunks around a repaired one whose light can reach it, placed again as FullRelight places them; the
    // chunks the placements changed
    internal static HashSet<long> BlockLight(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z,
        int[] broken, int dimension)
    {
        var touched = new HashSet<long>();
        if (!NotNull(illuminator) || !NotNull(chunks) || !NotNull(broken)) return touched;
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
                    // once per source, and only one that reaches this chunk: another repaired chunk may be the one it reaches
                    if (hsv is not { Length: 3 } || Reach(pos, x, y, z) >= hsv[2] ||
                        !placed.Add(chunks.ChunkIndex3D(sx, sourceY, sz) * Size * Size * Size + at)) continue;
                    touched.UnionWith(illuminator.PlaceBlockLight(hsv, pos.X, pos.InternalY, pos.Z));
                }
            }
        }

        return touched;
    }

    // How many steps a source's light needs to reach the chunk (Manhattan distance to its nearest cell); light loses one per step
    internal static int Reach(BlockPos source, int x, int y, int z)
    {
        if (!NotNull(source)) return int.MaxValue;
        var (dx, dy, dz) = (Gap(source.X, x * Size), Gap(source.Y, y * Size), Gap(source.Z, z * Size));
        return Assert(dx >= 0 && dy >= 0 && dz >= 0) ? dx + dy + dz : int.MaxValue;
    }

    private static int Gap(int at, int low) => Math.Max(0, Math.Max(low - at, at - (low + Size - 1)));
}

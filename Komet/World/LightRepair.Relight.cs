using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.World;

// ChunkIlluminator.FullRelight relights an area and a chunk around it: every loaded chunk there loses its light, every column loaded
// whole gets its sunlight, and last every light source of those chunks is placed again. It has two faults. A chunk of a column not
// loaded whole keeps no light: the column is skipped. And each source is placed at its chunk's offset plus its position in the world,
// which already holds that offset - twice as far out, where it lights nothing (off the map in any real world) or plants a phantom
// source into whatever chunk is there, while the real one stays dark. The prefix runs the same relight in the same order without them:
// the light of a column not loaded whole is left as it was, and the column waits (Waiting) until the sweep finds it loaded whole and
// relights it; every source is placed where it is. Only the server's own illuminator is taken, whose relights run on its main thread
// as the sweep does; another one, or another mod's patch on FullRelight, which the prefix would bypass, leaves it to the engine.
internal static partial class LightRepair
{
    private const int MaxSpan = 1024, MaxWaiting = 4096;

    // Columns a relight could not take because they were loaded in part, each once, in the order they came: dimension and chunk
    // position; server main thread
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

    internal static int WaitingColumns => Waiting.Count;

    // KometModSystem asks again on LevelFinalize, when every other mod has patched: another mod's FullRelight stays the engine's
    internal static void Recheck()
    {
        var relight = Seams() is { Length: > 1 } seams ? seams[1] : null;
        _foreign = EngineShape.Report(_logger, nameof(LightRepair), _foreign,
            _shaped && EngineShape.Foreign([relight], EngineShape.Kinds.Replacing, null, typeof(LightRepair)));
    }

    // Prefix on ChunkIlluminator.FullRelight: the relight without its two faults; a failure hands it to the engine's
    internal static bool Relight(ChunkIlluminator __instance, BlockPos minPos, BlockPos maxPos)
    {
        if (!Enabled || _failed || _foreign || !NotNull(__instance) || !NotNull(minPos) || !NotNull(maxPos) ||
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

    // The engine's FullRelight step by step - its area, its order, its calls - but for the two faults; false, having changed
    // nothing, for a world it is not made for
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

    // The columns of the area loaded whole, in the engine's order; each column that is not, and holds a chunk the area took, waits
    private static Dictionary<(int X, int Z), IWorldChunk[]> Whole(IChunkProvider provider, (int X, int Y, int Z) from,
        (int X, int Y, int Z) to, int dimension, int height, Dictionary<Vec3i, IWorldChunk> loaded)
    {
        var whole = new Dictionary<(int X, int Z), IWorldChunk[]>();
        if (!Assert(height is > 0 and <= MaxChunks)) return whole;
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

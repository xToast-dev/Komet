using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.World;

// The integrated server's snow layer scanner reads two blocks a position over each loaded column's surface after every
// in-game hour of weather, and each read of a packed chunk unpacked it for good: at view distance 1536 some 9000 columns,
// 13.6 MB/s of chunk data (Int32[]) that lived until the chunk was packed again, long enough to be promoted (the largest
// allocator in steady flight, 28 %). A packed chunk is read without unpacking instead: its blocks and fluids are
// decompressed (the engine's DecompressCombined) into the bit planes of one of the thread's slots, reused from chunk to
// chunk, and a block id is read as ChunkDataLayer.GetFromBitsN reads it (palette[bits at the index]). The positions of a
// column alternate between two or three chunks, so four slots. The chunk stays packed and untouched (its last read time
// too); an unpacked one is read as the engine reads it. Every 3 s the scanner queues all loaded columns from
// AllLoadedMapchunks, a new dictionary of some 9000 entries grown by doubling (large object heap); it gets one kept per
// thread, refilled.
internal static class SnowPeek
{
    private const string Snow = "Vintagestory.GameContent.WeatherSimulationSnowAccum", Compression = "Vintagestory.Common.Compression";
    private const int MaxInstructions = 4096, Reads = 4, Side = 32, Cells = Side * Side * Side, Slots = 4, MaxPlanes = 15;
    private const int MaxColumns = 1 << 20;

    private delegate int[]? Combined(byte[] compressed, ref int[][] planes, ref int count, System.Func<int[]> newArray);

    private static readonly System.Func<int[]> NewPlane = static () => new int[Cells / Side];
    private static Combined? _combined;

    [ThreadStatic] private static Slot[]? _slots;
    [ThreadStatic] private static int _next;
    [ThreadStatic] private static Dictionary<long, IMapChunk>? _columns;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }
    public static bool Listed { get; private set; } // OnServerTick3s reads the kept columns

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "server")]
    private static extern ref ServerMain? Server(ServerAPIComponentBase api);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loadedMapChunks")]
    private static extern ref ConcurrentDictionary<long, ServerMapChunk>? MapChunks(ServerMain server);

    // UpdateSnowLayer and OnServerTick3s, which the transpilers rewrite
    internal static MethodBase?[] Replaced =>
        AccessTools.TypeByName(Snow) is { } snow
            ? [AccessTools.DeclaredMethod(snow, "UpdateSnowLayer"), AccessTools.DeclaredMethod(snow, "OnServerTick3s")]
            : [];

    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        (Rewritten, Listed) = (false, false);
        var snow = AccessTools.TypeByName(Snow);
        var update = snow is null ? null : AccessTools.DeclaredMethod(snow, "UpdateSnowLayer");
        var combined = AccessTools.TypeByName(Compression) is { } type ? AccessTools.DeclaredMethod(type, "DecompressCombined") : null;
        _combined = combined is null ? null : (Combined?)Delegate.CreateDelegate(typeof(Combined), combined, false);
        if (!NotNull(harmony) || update is null || _combined is null) return;
        if (!Fields())
        {
            logger?.Warning("Komet SnowPeek: the server's chunk or column fields are not the ones it reads; the snow scan unpacks " +
                            "chunks as in the engine");
            return;
        }

        _ = harmony.Patch(update, postfix: new HarmonyMethod(AccessTools.Method(typeof(SnowPeek), nameof(Done))),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(SnowPeek), nameof(Rewrite))));
        if (AccessTools.DeclaredMethod(snow, "OnServerTick3s") is { } tick)
            _ = harmony.Patch(tick, transpiler: new HarmonyMethod(AccessTools.Method(typeof(SnowPeek), nameof(List))));
    }

    // The private members the accessors read, each with the type they expect: a missing one throws at its first use
    private static bool Fields() =>
        ChunkInternals.Present() &&
        AccessTools.DeclaredField(typeof(ServerAPIComponentBase), "server")?.FieldType == typeof(ServerMain) &&
        AccessTools.DeclaredField(typeof(ServerMain), "loadedMapChunks")?.FieldType ==
        typeof(ConcurrentDictionary<long, ServerMapChunk>);

    // OnServerTick3s's one AllLoadedMapchunks becomes Columns
    internal static List<CodeInstruction> List(IEnumerable<CodeInstruction> instructions)
    {
        var (all, columns) = (AccessTools.PropertyGetter(typeof(IWorldManagerAPI), nameof(IWorldManagerAPI.AllLoadedMapchunks)),
            AccessTools.Method(typeof(SnowPeek), nameof(Columns)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(all) || !NotNull(columns)) return code;
        Listed = Il.SubstituteSingle(code, c => c.operand is MethodInfo m && m == all, columns);
        return code;
    }

    // AllLoadedMapchunks into the thread's kept dictionary (the scanner only enumerates it, at once)
    internal static Dictionary<long, IMapChunk> Columns(IWorldManagerAPI world)
    {
        if (!Enabled || world is not ServerAPIComponentBase api || Server(api) is not { } server || MapChunks(server) is not { } loaded)
            return world.AllLoadedMapchunks;
        var columns = _columns ??= [];
        columns.Clear();
        foreach (var (key, value) in loaded.Bounded(MaxColumns)) columns[key] = value;
        return columns;
    }

    // The four block reads of the column (two by position, two by coordinates and layer) become Komet's, any other
    // count leaves the engine's IL as it is
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (byPos, byXyz) = (AccessTools.Method(typeof(IWorldChunk), nameof(IWorldChunk.GetLocalBlockAtBlockPos),
                [typeof(IWorldAccessor), typeof(BlockPos)]),
            AccessTools.Method(typeof(IWorldChunk), nameof(IWorldChunk.GetLocalBlockAtBlockPos),
                [typeof(IWorldAccessor), typeof(int), typeof(int), typeof(int), typeof(int)]));
        var (atPos, atXyz) = (AccessTools.Method(typeof(SnowPeek), nameof(ReadAt)), AccessTools.Method(typeof(SnowPeek), nameof(Read)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(byPos) || !NotNull(byXyz) ||
            !NotNull(atPos) || !NotNull(atXyz)) return code;
        if (Il.Count(code, c => c.operand is MethodInfo m && (m == byPos || m == byXyz)) != Reads) return code;
        var done = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].operand is MethodInfo m && (m == byPos || m == byXyz) && Il.Substitute(code, i, m == byPos ? atPos : atXyz))
                done++;
        Rewritten = Assert(done == Reads);
        return code;
    }

    internal static Block ReadAt(IWorldChunk chunk, IWorldAccessor world, BlockPos pos) =>
        NotNull(pos) ? Read(chunk, world, pos.X, pos.Y, pos.Z, 0) : chunk.GetLocalBlockAtBlockPos(world, pos);

    internal static Block Read(IWorldChunk chunk, IWorldAccessor world, int x, int y, int z, int layer)
    {
        if (!Enabled || chunk is not WorldChunk own || !NotNull(world) || _combined is not { } combined)
            return chunk.GetLocalBlockAtBlockPos(world, x, y, z, layer);
        var index = ((y % Side) * Side + z % Side) * Side + x % Side;
        lock (ChunkInternals.PackUnpackLock(own))
        {
            if (own.Disposed) return world.Blocks[0];
            if (ChunkInternals.Data(own) is { } live) return world.Blocks[live.GetBlockId(index, layer)];
            // nothing packed to read, a format before 2 (its fluids still in the block layer), another layer
            if (own.blocksCompressed is not { } blocks || ChunkInternals.Version(own) < 2 || layer is not (0 or 2) || !Index(index, Cells))
                return chunk.GetLocalBlockAtBlockPos(world, x, y, z, layer);
            var slot = Decoded(own, blocks, combined);
            var fluid = Id(slot.Fluids, slot.FluidPalette, slot.FluidBits, index);
            if (layer == 2) return world.Blocks[fluid];
            var solid = Id(slot.Blocks, slot.BlockPalette, slot.BlockBits, index);
            return world.Blocks[solid != 0 ? solid : fluid]; // ChunkData.GetBlockId, layer 0
        }
    }

    // The slot holding this chunk, else the next one decoded anew
    private static Slot Decoded(WorldChunk chunk, byte[] blocks, Combined combined)
    {
        var slots = _slots ??= [new(), new(), new(), new()];
        foreach (var held in slots.Bounded(Slots))
            if (ReferenceEquals(held.Key, blocks)) return held;
        var slot = slots[_next];
        _next = (_next + 1) % Slots;
        (slot.BlockPalette, slot.BlockBits) = Decode(blocks, ref slot.Blocks, combined);
        (slot.FluidPalette, slot.FluidBits) = chunk.fluidsCompressed is { } fluids ? Decode(fluids, ref slot.Fluids, combined) : (null, 0);
        slot.Key = blocks;
        return slot;
    }

    // As ChunkDataLayer.Decompress: the palette, and the planes its length needs
    private static (int[]? Palette, int Bits) Decode(byte[] compressed, ref int[][] planes, Combined combined)
    {
        var count = 0;
        var palette = combined(compressed, ref planes, ref count, NewPlane);
        if (palette is null || !Assert(palette.Length > 0)) return (null, 0);
        var bits = System.Numerics.BitOperations.Log2((uint)palette.Length); // the engine's halving count
        return (palette, Math.Min(bits, MaxPlanes));
    }

    // As GetFromBitsN: the palette entry the index's bits name; bitsize 0 reads 0
    private static int Id(int[][] planes, int[]? palette, int bits, int index)
    {
        if (palette is null || bits == 0 || !Assert(bits <= planes.Length)) return 0;
        var (word, at) = ((index & 0x7FFF) / Side, 0);
        for (var i = 0; i < Math.Min(bits, MaxPlanes); i++) at |= ((planes[i][word] >> index) & 1) << i;
        return Index(at, palette.Length) ? palette[at] : 0;
    }

    // A packed chunk's block as Read finds it (tests); -1 before Install
    internal static int PeekId(byte[] blocks, byte[]? fluids, int index, int layer)
    {
        if (_combined is not { } combined || !NotNull(blocks) || !Index(index, Cells)) return -1;
        var slot = new Slot();
        (slot.BlockPalette, slot.BlockBits) = Decode(blocks, ref slot.Blocks, combined);
        (slot.FluidPalette, slot.FluidBits) = fluids is null ? (null, 0) : Decode(fluids, ref slot.Fluids, combined);
        var fluid = Id(slot.Fluids, slot.FluidPalette, slot.FluidBits, index);
        if (layer == 2) return fluid;
        var solid = Id(slot.Blocks, slot.BlockPalette, slot.BlockBits, index);
        return solid != 0 ? solid : fluid;
    }

    // Postfix on UpdateSnowLayer: the column is read; the slots keep their planes, their chunks may change before the next
    internal static void Done()
    {
        if (_slots is not { } slots) return;
        foreach (var slot in slots.Bounded(Slots)) slot.Key = null;
    }

    // A packed chunk's blocks and fluids, decoded; Key is its blocks array by reference (a repack makes new ones)
    private sealed class Slot
    {
        public byte[]? Key;
        public int[][] Blocks = new int[MaxPlanes][], Fluids = new int[MaxPlanes][];
        public int[]? BlockPalette, FluidPalette;
        public int BlockBits, FluidBits;
    }
}

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Chunks;

// The server's random block ticks (ServerSystemBlockSimulation, every 300 ms) mark every chunk within BlockTickChunkRange
// (5: 11x11x11 chunks) of a player fresh and unpack each to draw 16 random cells: the chunks around a player never pack,
// rock and air below and above the surface included. Only blocks that override ShouldReceiveServerGameTicks (soil,
// leaves, crops, water, ice, ...) or are their own snow-covered variant (Block's melt check) can take such a tick; the
// rest answer false and the tick does nothing. A chunk whose block and fluid palettes hold none of them is skipped
// (packed: the palette alone is read from the packed bytes, verdict kept per byte array), and the tick no longer marks
// chunks fresh: a chunk that is ticked is unpacked by the tick itself, which keeps it fresh as before, the others pack
// after their time. Off with PackedChunks too, and while any mod patches Block.ShouldReceiveServerGameTicks itself.
internal static class PackedTicks
{
    private const string Simulation = "Vintagestory.Server.ServerSystemBlockSimulation"; // internal
    private const int MaxInstructions = 4096, MaxPalette = 1 << 16, MaxBlocks = 1 << 20, RecheckMs = 5000;

    // Verdict boxes per packed array: whether its palette holds a block that can take a tick
    private static readonly object Calm = new(), Busy = new();
    private static readonly ConditionalWeakTable<byte[], object> Verdicts = [];
    private static readonly Dictionary<Type, bool> Overrides = [];

    // Built and read on the server's block simulation thread only
    private static bool[]? _tickable;
    private static IList<Block>? _blocks;
    private static int _built, _checkedAt;
    private static bool _checked, _foreign;

    private static readonly MethodBase? Base = AccessTools.DeclaredMethod(typeof(Block),
        nameof(Block.ShouldReceiveServerGameTicks),
        [typeof(IWorldAccessor), typeof(BlockPos), typeof(Random), typeof(object).MakeByRefType()]);

    private static readonly ICompression? Zstd =
        AccessTools.DeclaredField(typeof(Compression), "Compression1")?.GetValue(null) as ICompression;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static bool Rewritten { get; private set; }

    // Chunk ticks skipped, on the block simulation thread
    private static long Skipped { get; set; }

    private static bool Active => Installed && Enabled && ChunkViews.Enabled && ChunkViews.Usable;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "server")]
    private static extern ref ServerMain? ServerOf(ServerSystem system);

    // tickChunk (skipped by the prefix) and OnSeparateThreadTick (its MarkFresh rewritten)
    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Simulation);
        return type is null
            ? [null, null]
            :
            [
                AccessTools.DeclaredMethod(type, "tickChunk", [typeof(long), typeof(ServerChunk)]),
                AccessTools.DeclaredMethod(type, "OnSeparateThreadTick", [])
            ];
    }

    public static void Install(Harmony harmony)
    {
        Stop();
        var seams = Seams();
        if (seams[0] is not MethodInfo tick || seams[1] is not MethodInfo loop || Base is null ||
            Zstd is null || !typeof(ServerSystem).IsAssignableFrom(tick.DeclaringType))
        {
            return;
        }

        _ = harmony.Patch(tick, new HarmonyMethod(typeof(PackedTicks), nameof(Ticking)));
        _ = harmony.Patch(loop, transpiler: new HarmonyMethod(typeof(PackedTicks), nameof(FreshIl)));
        Installed = true;
    }

    public static void Stop()
    {
        (Installed, _tickable, _blocks, _built, _checked, _foreign, Skipped) = (false, null, null, 0, false, false, 0);
        Verdicts.Clear();
        Overrides.Clear();
    }

    // The loop's one ServerChunk.MarkFresh becomes Fresh; the map chunk's stays
    internal static List<CodeInstruction> FreshIl(IEnumerable<CodeInstruction> instructions)
    {
        var mark = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.MarkFresh), []);
        var fresh = AccessTools.Method(typeof(PackedTicks), nameof(Fresh));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(mark)) return code;

        Rewritten = Il.SubstituteSingle(code, c => c.Calls(mark), fresh);
        return code;
    }

    // A chunk in tick range: fresh only while off (the tick's own unpack keeps the chunks it ticks fresh)
    internal static void Fresh(WorldChunk chunk)
    {
        if (!Active || _foreign) chunk.MarkFresh();
    }

    // tickChunk prefix: false (skipped) for a chunk none of whose blocks can take a random tick
    private static bool Ticking(ServerSystem __instance, ServerChunk? chunk)
    {
        if (!Active || chunk is null || ServerOf(__instance)?.WorldMap?.Blocks is not { } blocks || !Ready(blocks) ||
            !Quiet(chunk))
        {
            return true;
        }

        Skipped++;
        return false;
    }

    // Whether the chunk holds no block that can take a tick; false when unsure
    internal static bool Quiet(WorldChunk chunk)
    {
        if (ChunkViews.Packed(chunk, out var blocks, out var fluids)) return !Ticks(blocks) && !Ticks(fluids);

        if (ChunkViews.Live(chunk) is not { } data)
        {
            return false; // disposed, or packed in an older format
        }

        return !Ticks(data.blocksLayer) && !Ticks(data.fluidsLayer);
    }

    // The table for this block list, rebuilt when it grows; nothing while a mod patches the base method
    internal static bool Ready(IList<Block> blocks)
    {
        var now = Environment.TickCount;
        if (!_checked || now - _checkedAt > RecheckMs)
        {
            (_checked, _checkedAt) = (true, now);
            _foreign = Base is null || EngineShape.Patched(Base);
        }

        if (_foreign) return false;

        if (_tickable is not null && ReferenceEquals(blocks, _blocks) && blocks.Count == _built) return true;

        var count = blocks.Count;
        if (!Assert(count <= MaxBlocks)) return false;

        var table = new bool[count];
        for (var i = 0; i < Math.Min(count, MaxBlocks); i++)
        {
            table[i] = Takes(blocks[i]);
        }

        (_tickable, _blocks, _built) = (table, blocks, count);
        Verdicts.Clear();
        return true;
    }

    // A block that can take a random tick: null (unknown), an override anywhere below Block, or its own snow cover
    private static bool Takes(Block? block)
    {
        if (block is null) return true;

        if (ReferenceEquals(block, block.snowCovered1) || ReferenceEquals(block, block.snowCovered2) ||
            ReferenceEquals(block, block.snowCovered3))
        {
            return true;
        }

        var type = block.GetType();
        if (!Overrides.TryGetValue(type, out var own))
        {
            var method = type.GetMethod(nameof(Block.ShouldReceiveServerGameTicks),
                [typeof(IWorldAccessor), typeof(BlockPos), typeof(Random), typeof(object).MakeByRefType()]);
            Overrides[type] = own = method is null || method.DeclaringType != typeof(Block);
        }

        return own;
    }

    private static bool Ticks(int id) => _tickable is not { } table || !Index(id, table.Length) || table[id];

    private static bool Ticks(ChunkDataLayer? layer)
    {
        if (layer?.palette is not { } palette)
        {
            return false; // no layer: air
        }

        var count = Math.Min(layer.paletteCount, palette.Length);
        for (var i = 0; i < Math.Min(count, MaxPalette); i++)
        {
            if (Ticks(palette[i])) return true;
        }

        return count > MaxPalette;
    }

    // A packed layer: its palette as Compression.DecompressCombined reads it (length, then raw or zstd ints)
    private static bool Ticks(byte[]? packed)
    {
        if (packed is null) return false;

        if (Verdicts.TryGetValue(packed, out var known)) return ReferenceEquals(known, Busy);

        var busy = Palette(packed);
        Verdicts.AddOrUpdate(packed, busy ? Busy : Calm);
        return busy;
    }

    private static bool Palette(byte[] packed)
    {
        if (packed.Length < 4) return true;

        var length = BinaryPrimitives.ReadInt32LittleEndian(packed);
        if (length == 0)
        {
            return false; // the empty layer
        }

        ReadOnlySpan<byte> bytes;
        if (length < 0)
        {
            if (length == int.MinValue || -length > packed.Length - 4) return true;

            bytes = packed.AsSpan(4, -length);
        }
        else
        {
            if (length > packed.Length - 4 || Zstd is not { } zstd) return true;

            var size = zstd.DecompressAndSize(packed, 4, length, out var buffer);
            if (size < 0 || size > buffer.Length) return true;

            bytes = buffer.AsSpan(0, size);
        }

        var ids = MemoryMarshal.Cast<byte, int>(bytes); // native order, as the engine's ByteToInt copies them
        for (var i = 0; i < Math.Min(ids.Length, MaxPalette); i++)
        {
            if (Ticks(ids[i])) return true;
        }

        return ids.Length > MaxPalette;
    }
}

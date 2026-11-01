using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Common;

namespace Komet.Chunks;

// Single block and light reads (WorldChunk.UnpackAndReadBlock, Unpack_AndReadLight: every block accessor, the map,
// rooms, an arriving chunk's block entities, the server's block ticks) unpack a packed chunk for one cell, and the chunk
// then holds its planes for its time. A packed chunk is read through the thread's ChunkViews instead and stays packed.
// A thread decoding more than MaxDecodes views within WindowMs reads across more chunks than its views hold (worldgen
// over many columns): for the rest of that window it unpacks as the engine does, which keeps the chunk for the next
// reads. Switched with PackedChunks (ChunkViews.Enabled); an unpacked chunk is always read where it is.
internal static class PackedReads
{
    private const int WindowMs = 250, MaxDecodes = 128, Cells = 32 * 32 * 32;

    [ThreadStatic] private static int _windowStart, _windowDecoded;

    public static bool Installed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadLight")]
    private static extern ushort ReadLight(ChunkData data, int index);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var block = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.UnpackAndReadBlock), [typeof(int), typeof(int)]);
        var light = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.Unpack_AndReadLight), [typeof(int)]);
        if (!NotNull(harmony) || block is null || light is null ||
            AccessTools.DeclaredMethod(typeof(ChunkData), "ReadLight", [typeof(int)])?.ReturnType != typeof(ushort)) return;
        _ = harmony.Patch(block, new HarmonyMethod(typeof(PackedReads), nameof(Block)));
        _ = harmony.Patch(light, new HarmonyMethod(typeof(PackedReads), nameof(Light)));
        Installed = true;
    }

    // UnpackAndReadBlock prefix: false with the view's block id
    private static bool Block(WorldChunk __instance, int index, int layer, ref int __result)
    {
        if (View(__instance) is not { } view || !Index(index, Cells)) return true;
        __result = view.GetBlockId(index, layer);
        return false;
    }

    // Unpack_AndReadLight prefix: false with the view's light
    private static bool Light(WorldChunk __instance, int index, ref ushort __result)
    {
        if (View(__instance) is not { } view || !Index(index, Cells)) return true;
        __result = ReadLight(view, index);
        return false;
    }

    // The packed chunk's view, unless off, unpacked or this thread is past its decodes for the window
    private static ChunkData? View(WorldChunk chunk)
    {
        if (!ChunkViews.Enabled || !chunk.IsPacked()) return null;
        var now = Environment.TickCount;
        if (now - _windowStart > WindowMs) (_windowStart, _windowDecoded) = (now, ChunkViews.ThreadDecoded);
        return ChunkViews.ThreadDecoded - _windowDecoded > MaxDecodes ? null : ChunkViews.Open(chunk);
    }
}

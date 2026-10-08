namespace Komet.Vulkan;

// Freed from the recording thread too, hence the lock. Exportable allocations are counted apart: RADV names each in every
// submission of the device (a few hundred cost a submission a third of a millisecond), the others cost it nothing.
internal static class VkMemory
{
    private const int MaxLive = 1 << 16;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, (string Owner, ulong Bytes, bool Exported)> Live = [];
    private static readonly Dictionary<string, ulong> ByOwner = new(StringComparer.Ordinal);

    public static ulong Bytes { get; private set; }
    public static long Made { get; private set; }

    // Live exportable allocations, and their bytes
    public static int Exported { get; private set; }
    public static ulong ExportedBytes { get; private set; }

    // The shared arena's: bytes and ranges carved, empty blocks freed
    private static (ulong Live, int Ranges, long Freed) _arena;

    public static void Carved(ulong live, int ranges, long freed)
    {
        _ = Assert(ranges >= 0) && Assert(freed >= 0);
        lock (Gate) _arena = (live, ranges, freed);
    }

    public static void Allocated(ulong memory, ulong bytes, string file, bool exported = false)
    {
        if (!Assert(memory != 0) || !NotNull(file)) return;
        var owner = Path.GetFileNameWithoutExtension(file);
        lock (Gate)
        {
            if (Live.Count >= MaxLive) return; // past this the report is wrong anyway: the guard has long fired
            Live[memory] = (owner, bytes, exported);
            ByOwner[owner] = ByOwner.GetValueOrDefault(owner) + bytes;
            (Bytes, Made) = (Bytes + bytes, Made + 1);
            if (exported) (Exported, ExportedBytes) = (Exported + 1, ExportedBytes + bytes);
        }
    }

    public static void Freed(ulong memory)
    {
        if (!Assert(memory != 0)) return;
        lock (Gate)
        {
            if (!Live.Remove(memory, out var was)) return;
            ByOwner[was.Owner] = ByOwner.GetValueOrDefault(was.Owner) - was.Bytes;
            Bytes -= was.Bytes;
            if (was.Exported) (Exported, ExportedBytes) = (Exported - 1, ExportedBytes - was.Bytes);
            _ = Assert(Bytes < ulong.MaxValue / 2) && Assert(Exported >= 0);
        }
    }

    public static string Report()
    {
        lock (Gate)
        {
            var owners = ByOwner.Where(o => o.Value > 0).OrderByDescending(o => o.Value).Take(8)
                .Select(o => $"{o.Key} {o.Value >> 20} MB");
            _ = Assert(Live.Count <= MaxLive);
            return $"{Bytes >> 20} MB in {Live.Count} allocations ({Made} made, {Exported} exportable of " +
                   $"{ExportedBytes >> 20} MB): {string.Join(", ", owners)}; arena {_arena.Live >> 20} MB carved in " +
                   $"{_arena.Ranges} ranges, {_arena.Freed} empty blocks freed; " +
                   $"process {Environment.WorkingSet >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB ({Survived()})";
        }
    }

    // What the last full collection (blocking or background) left alive: the managed figure counts the garbage made since as well,
    // this one grows only with what something keeps
    internal static string Survived()
    {
        var (full, background) = (GC.GetGCMemoryInfo(GCKind.FullBlocking), GC.GetGCMemoryInfo(GCKind.Background));
        var last = full.Index >= background.Index ? full : background;
        if (last.Index == 0 || !Assert(last.Generation <= 2)) return "no full collection yet";
        var live = 0L;
        foreach (var generation in last.GenerationInfo.Bounded(8))
            live += generation.SizeAfterBytes - generation.FragmentationAfterBytes;
        _ = Assert(live >= 0);
        return $"{live >> 20} MB alive after full collection {GC.CollectionCount(2)}";
    }
}

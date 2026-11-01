namespace Komet.Vulkan;

// Vulkan memory made and freed, by owner (the file that made it), for the report. Freed from the recording thread too,
// hence the lock.
internal static class VkMemory
{
    private const int MaxLive = 1 << 16;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, (string Owner, ulong Bytes)> Live = [];
    private static readonly Dictionary<string, ulong> ByOwner = new(StringComparer.Ordinal);

    public static ulong Bytes { get; private set; }
    public static long Made { get; private set; }

    public static void Allocated(ulong memory, ulong bytes, string file)
    {
        if (!Assert(memory != 0)) return;
        var owner = Path.GetFileNameWithoutExtension(file);
        lock (Gate)
        {
            if (Live.Count >= MaxLive) return; // past this the report is wrong anyway: the guard has long fired
            Forgotten(memory); // a handle a closed device leaked, its value given again by the next device: counted once
            Live[memory] = (owner, bytes);
            ByOwner[owner] = ByOwner.GetValueOrDefault(owner) + bytes;
            (Bytes, Made) = (Bytes + bytes, Made + 1);
        }
    }

    public static void Freed(ulong memory)
    {
        if (!Assert(memory != 0)) return;
        lock (Gate) Forgotten(memory);
    }

    // Under Gate
    private static void Forgotten(ulong memory)
    {
        if (!Live.Remove(memory, out var was)) return;
        ByOwner[was.Owner] = ByOwner.GetValueOrDefault(was.Owner) - was.Bytes;
        Bytes -= was.Bytes;
    }

    public static string Report()
    {
        lock (Gate)
        {
            var owners = ByOwner.Where(o => o.Value > 0).OrderByDescending(o => o.Value).Take(8)
                .Select(o => $"{o.Key} {o.Value >> 20} MB");
            return $"{Bytes >> 20} MB in {Live.Count} allocations ({Made} made): {string.Join(", ", owners)}; " +
                   $"process {Environment.WorkingSet >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB ({Survived()})";
        }
    }

    // Alive after the last full collection: unlike the managed figure, excludes garbage made since
    internal static string Survived()
    {
        var (full, background) = (GC.GetGCMemoryInfo(GCKind.FullBlocking), GC.GetGCMemoryInfo(GCKind.Background));
        var last = full.Index >= background.Index ? full : background;
        if (last.Index == 0 || !Assert(last.Generation <= 2)) return "no full collection yet";
        var live = 0L;
        foreach (var generation in last.GenerationInfo.Bounded(8))
            live += generation.SizeAfterBytes - generation.FragmentationAfterBytes;
        return $"{live >> 20} MB alive after full collection {GC.CollectionCount(2)}";
    }
}

// What an in-flight frame may read (image, buffer, heap range), kept with its last frame and freed once that is done,
// no device wait. Full (max): Add says so, the caller frees it itself.
internal sealed class Retired<T>(int max, Action<T> free)
{
    private const int MaxItems = 1 << 16;

    private readonly List<(T Item, long Frame)> _items = [];

    public int Count => _items.Count;

    public bool Add(T item, long frame)
    {
        if (_items.Count >= Math.Min(max, MaxItems)) return false;
        _items.Add((item, frame));
        return Assert(frame >= -VulkanFrame.Slots);
    }

    // Those whose frame is done go; the others keep their order
    public void Collect(long done)
    {
        if (_items.Count == 0 || !Assert(done >= -VulkanFrame.Slots)) return;
        var kept = 0;
        for (var i = 0; i < Math.Min(_items.Count, MaxItems); i++)
        {
            if (_items[i].Frame <= done) free(_items[i].Item);
            else _items[kept++] = _items[i];
        }

        _items.RemoveRange(kept, _items.Count - kept);
    }

    // With the device idle: every one goes (free: false, gone with what holds them, as a heap's blocks)
    public void Clear(bool freed = true)
    {
        if (freed)
            foreach (var (item, _) in _items.Bounded(MaxItems))
                free(item);
        _items.Clear();
    }
}

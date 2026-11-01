using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Core;

// A packed client chunk read without unpacking it. Unpacking gives the chunk new bit planes (the engine's pool is empty
// while chunks stream in), keeps them for the chunk's time (seconds) and packs it again: while a world loads, the
// tessellator's 27 chunks a pass and the map's columns were most of the live chunk data and of what outlived each
// collection. A view decodes the packed bytes into one of the reading thread's own chunk datas instead, their planes
// taken from and given back to the thread's own pool; the chunk keeps its bytes and stays packed. A view stands for its
// chunk while the chunk holds the same packed arrays (never changed in place: a write unpacks, a pack makes new ones), so
// a thread keeps its last MaxViews and reuses them across passes. An unpacked chunk is read where it is (Open gives null), and
// only chunks in the current packed format are viewed (client and the built-in server's: a pool of the same kind).
internal static class ChunkViews
{
    private const int Format = 2, Size = 32, MaxViews = 64, MaxSpares = 1024, ClientSlot = 0, ServerSlot = 1, MaxThreads = 256;

    // This thread's client and server caches, also listed in Threads: Release drops every thread's, not only its own
    [ThreadStatic] private static Cache?[]? _caches;

    // Every thread that viewed a chunk with its caches. Pool workers outlive worlds (parked, not ended): a worker that
    // reads no chunk after a world left would hold the old game, its last 64 chunks and their planes until its next read.
    // A reader keeps the cache it took for the read it is in; its next read makes a new one
    private static readonly List<(Thread Thread, Cache?[] Caches)> Threads = [];

    // Counted per thread only: views serve every block accessor read of a packed chunk on every thread, where a shared
    // interlocked count would bounce one cache line between them
    [ThreadStatic] private static int _threadDecoded;

    // At world leave: every thread's caches, whose old game, last chunks and planes would otherwise live until that
    // thread's first read in the next world
    internal static void Release()
    {
        if (_caches is { } own) Array.Clear(own); // the calling thread's even when Threads was full
        lock (Threads)
        {
            for (var i = 0; i < Math.Min(Threads.Count, MaxThreads); i++) Array.Clear(Threads[i].Caches);
            _ = Threads.RemoveAll(static entry => !entry.Thread.IsAlive);
        }
    }

    // This thread's caches, listed once (ended threads leave the list here and at Release)
    private static Cache?[] Register()
    {
        var caches = new Cache?[2];
        lock (Threads)
        {
            _ = Threads.RemoveAll(static entry => !entry.Thread.IsAlive);
            if (Threads.Count < MaxThreads) Threads.Add((Thread.CurrentThread, caches));
        }

        _caches = caches;
        return caches;
    }

    // Views this thread decoded (a miss each)
    public static int ThreadDecoded => _threadDecoded;

    public static bool Enabled { get; set; } = true;
    public static bool Usable { get; } = Present();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
    private static extern ref object Lock(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkdata")]
    private static extern ref ChunkData? Unpacked(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkdataVersion")]
    private static extern ref int Version(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "datapool")]
    private static extern ref ChunkDataPool? Pool(WorldChunk chunk);

    // Whether every member is there with the type the accessors expect: a missing one throws at its first use
    private static bool Present()
    {
        var chunk = typeof(WorldChunk);
        var found = AccessTools.DeclaredField(chunk, "packUnpackLock")?.FieldType == typeof(object) &&
                    AccessTools.DeclaredField(chunk, "chunkdata")?.FieldType == typeof(ChunkData) &&
                    AccessTools.DeclaredField(chunk, "chunkdataVersion")?.FieldType == typeof(int) &&
                    AccessTools.DeclaredField(chunk, "datapool")?.FieldType == typeof(ChunkDataPool) &&
                    AccessTools.DeclaredField(typeof(ChunkDataLayer), "dataBits")?.FieldType == typeof(int[][]) &&
                    AccessTools.DeclaredMethod(typeof(ChunkDataLayer), "Decompress", [typeof(byte[])]) is not null &&
                    AccessTools.DeclaredMethod(typeof(ChunkDataLayer), "DecompressSeparate", [typeof(byte[]), typeof(byte[])]) is not null;
        return found;
    }

    // Packed in the current format on a client pool: what Open would view
    public static bool Viewable(WorldChunk chunk) => Snapshot(chunk, out _) && Pool(chunk) is ClientChunkDataPool;

    // The chunk's data decoded into a view of this thread, or null: unpacked (read it there), disposed, another format,
    // or off
    public static ChunkData? Open(WorldChunk chunk)
    {
        if (!Enabled || !Usable || chunk is null || Pool(chunk) is not { } source || !Snapshot(chunk, out var packed)) return null;
        // A thread outlives a world, so a new game or server gets a new cache and the old world's chunks go with the old
        // one. Client and server chunks each have their thread's cache
        var caches = _caches ?? Register();
        if (source is ClientChunkDataPool own)
        {
            object game = (object?)own.game ?? own; // a pool without a game (tests) stands for itself
            var client = caches[ClientSlot];
            if (client is null || !ReferenceEquals(client.Owner, game))
                caches[ClientSlot] = client = new Cache(game, new ClientChunkDataPool(Size, own.game!) { CacheSize = MaxSpares / 2 });
            return client.View(chunk, packed);
        }

        if (source.server is not { } server) return null;
        var cache = caches[ServerSlot];
        if (cache is null || !ReferenceEquals(cache.Owner, server))
            caches[ServerSlot] = cache = new Cache(server, new ChunkDataPool(Size, server) { CacheSize = MaxSpares / 2 });
        return cache.View(chunk, packed);
    }

    // The packed arrays, read under the chunk's lock (Unpack and the pack commit change them under it)
    private static bool Snapshot(WorldChunk chunk, out Packing packed)
    {
        packed = default;
        if (!Usable || chunk.Disposed) return false;
        lock (Lock(chunk))
        {
            if (chunk.Disposed || Unpacked(chunk) is not null || Version(chunk) != Format) return false;
            packed = new Packing(chunk.blocksCompressed, chunk.lightCompressed, chunk.lightSatCompressed, chunk.fluidsCompressed);
        }

        return true;
    }

    // Compared by reference: the same arrays are the same data
    private readonly record struct Packing(byte[]? Blocks, byte[]? Light, byte[]? LightSat, byte[]? Fluids)
    {
        // Four reference compares: the record's == goes through EqualityComparer<byte[]>.Default per field, on every read
        public bool Same(in Packing other) =>
            ReferenceEquals(Blocks, other.Blocks) && ReferenceEquals(Light, other.Light) &&
            ReferenceEquals(LightSat, other.LightSat) && ReferenceEquals(Fluids, other.Fluids);
    }

    private sealed class Cache(object owner, ChunkDataPool pool)
    {
        private static void MarkDecoded() => _threadDecoded++;

        private readonly WorldChunk?[] _owners = new WorldChunk?[MaxViews];
        private readonly Packing[] _packed = new Packing[MaxViews];
        private readonly ChunkData?[] _views = new ChunkData?[MaxViews];
        private readonly (BlockChunkDataLayer Blocks, BlockChunkDataLayer Fluids, ChunkDataLayer Light)?[] _layers =
            new (BlockChunkDataLayer, BlockChunkDataLayer, ChunkDataLayer)?[MaxViews];
        private readonly long[] _used = new long[MaxViews];
        private long _clock;
        private int _last; // the slot read last: block accessors read one chunk many times in a row, no walk for those

        public object Owner => owner;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBits")]
        private static extern ref int[][]? Planes(ChunkDataLayer layer);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Decompress")]
        private static extern void Decompress(ChunkDataLayer layer, byte[] combined);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "DecompressSeparate")]
        private static extern void DecompressSeparate(ChunkDataLayer layer, byte[] data, byte[] palette);

        private static void Forget(ChunkDataLayer layer)
        {
            if (Planes(layer) is { } planes) Array.Clear(planes);
        }

        public ChunkData View(WorldChunk chunk, Packing packed)
        {
            if (Hit(_last, chunk, packed) is { } recent) return recent;
            var slot = 0;
            for (var i = 0; i < MaxViews; i++)
            {
                if (ReferenceEquals(_owners[i], chunk))
                {
                    if (Hit(i, chunk, packed) is { } hit) return hit;
                    slot = i; // its older bytes: decoded anew in the same view
                    break;
                }

                if (_used[i] < _used[slot]) slot = i;
            }

            return Decode(slot, chunk, packed);
        }

        // The slot's view when it holds this chunk's bytes, marked used; a chunk holds at most one slot
        private ChunkData? Hit(int slot, WorldChunk chunk, Packing packed)
        {
            if (!ReferenceEquals(_owners[slot], chunk) || !_packed[slot].Same(packed) || _views[slot] is not { } view)
                return null;
            (_used[slot], _last) = (++_clock, slot);
            return view;
        }

        // The view's planes back to the thread's pool, then the packed bytes into the view's own layers, as
        // ChunkData.DecompressFrom does for the current format but without new layers each time
        private ChunkData Decode(int slot, WorldChunk chunk, Packing packed)
        {
            var view = _views[slot] ??= pool.Request();
            var (blocks, fluids, light) = _layers[slot] ??= (new BlockChunkDataLayer(pool), new BlockChunkDataLayer(pool), new ChunkDataLayer(pool));
            (_owners[slot], _packed[slot]) = (null, default);
            pool.FreeArraysAndReset(view); // the layers' planes back, the view's layer fields cleared
            // Clear leaves the given-back planes in the layer's array, and the decoder fills an entry only when it is
            // null: left there, a plane would be written here and handed out by the pool at once
            Forget(blocks);
            Forget(fluids);
            Forget(light);
            if (packed.Blocks is { } blockBytes)
            {
                Decompress(blocks, blockBytes);
                view.blocksLayer = blocks;
            }

            if (packed.Fluids is { } fluidBytes)
            {
                Decompress(fluids, fluidBytes);
                view.fluidsLayer = fluids;
            }

            if (packed is { Light: { Length: > 0 } lightBytes, LightSat: { Length: > 0 } lightPalette })
            {
                DecompressSeparate(light, lightBytes, lightPalette);
                view.lightLayer = light;
            }

            (_owners[slot], _packed[slot], _used[slot], _last) = (chunk, packed, ++_clock, slot);
            MarkDecoded();
            return view;
        }
    }
}

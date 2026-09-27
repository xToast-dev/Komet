using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;

namespace Komet.World;

// EntityPartitioning (VSEssentials) sorts every loaded entity into 8x8-block cells 31 times a second, on the client and on the server
// of a singleplayer world alike. PartitionEntities clears its dictionary and builds everything anew: an EntityPartitionChunk with a
// List<Entity>[16] per occupied chunk, a List<Entity>(4) per occupied cell and its growth, and a second array per chunk that holds an
// inanimate entity - 7 MB/s of garbage on the server of the bench world, a quarter of which is still referenced from the dictionary
// when a collection comes and is copied into gen 1 each time. The rewrite swaps only those allocations; the partitioning itself, the
// order of the entities in each list and every value it writes stay the engine's.
//
// Dictionary.Clear becomes Retire: the chunks the call before last put into the dictionary go back to its pool, every list taken out
// of their two arrays, emptied and pooled, the Entities slots nulled and InanimateEntities (emptied too) pooled and nulled; the chunks
// of the last call take their place, and the dictionary is cleared as before. The chunk constructor, the array in
// EntityPartitionChunk.Add and the list in FetchOrCreateList take from that pool, but only inside the call (a finalizer closes it).
// So a chunk, list or array handed out looks as a new one would: Entities all null, InanimateEntities null, a list empty (a larger
// capacity is never read). Nothing outside the partitioning keeps a chunk or a list across its calls: WalkEntities and the lookups
// built on it read the dictionary in place, on the thread that partitions, and EntityPlayer.entityListForPartitioning, the one list
// kept on an entity, is replaced on every call and only ever has its own player removed from it (RePartitionPlayer, which partitions
// right after). What a call hands out stays as it is through the next call too, so a reader on another thread would have a whole
// tick. The pools belong to the dictionary (client and server keep one each, on their own threads) and go with it.
internal static class PartitionReuse
{
    private const string Partitioning = "Vintagestory.GameContent.EntityPartitioning";
    private const string ChunkType = "Vintagestory.GameContent.EntityPartitionChunk";
    private const int MaxInstructions = 512, MaxChunks = 8192, MaxLists = 1 << 16, MaxArrays = 8192, MaxCells = 64;
    private const int PartitionBit = 1, AddBit = 2, FetchBit = 4, AllBits = 7;

    private static int _rewritten; // Harmony reruns a transpiler whenever another mod patches the method
    private static long _reused;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;

    // Chunks, lists and arrays handed out again instead of allocated, a total while Counting.Hud
    public static long Reused => Interlocked.Read(ref _reused);

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        var (partitioning, chunk) = (AccessTools.TypeByName(Partitioning), AccessTools.TypeByName(ChunkType));
        if (!NotNull(harmony) || !NotNull(partitioning) || !NotNull(chunk) || !Fields(chunk)) return;
        var partition = AccessTools.DeclaredMethod(partitioning, "PartitionEntities", [typeof(ICollection<Entity>)]);
        var add = AccessTools.DeclaredMethod(chunk, "Add", [typeof(Entity), typeof(int)]);
        var fetch = AccessTools.DeclaredMethod(chunk, "FetchOrCreateList", [typeof(List<Entity>).MakeByRefType()]);
        if (!NotNull(partition) || !NotNull(add) || !NotNull(fetch)) return;
        _ = NotNull(harmony.Patch(fetch, transpiler: new HarmonyMethod(RewriteFetch)));
        _ = NotNull(harmony.Patch(add, transpiler: new HarmonyMethod(RewriteAdd)));
        _ = NotNull(harmony.Patch(partition, transpiler: new HarmonyMethod(RewritePartition),
            finalizer: new HarmonyMethod(Close)));
    }

    // Both arrays a chunk holds, as Recycle reads and clears them, and the constructor Take stands in for
    private static bool Fields(Type chunk) =>
        Assert(AccessTools.DeclaredField(chunk, "Entities")?.FieldType == typeof(List<Entity>[])) &&
        Assert(AccessTools.DeclaredField(chunk, "InanimateEntities")?.FieldType == typeof(List<Entity>[])) &&
        Assert(chunk.IsClass && AccessTools.DeclaredConstructor(chunk, []) is not null);

    // The one Dictionary<long, chunk>.Clear becomes Retire and the one chunk constructor after it Take; any other shape keeps the
    // engine's IL
    internal static List<CodeInstruction> RewritePartition(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~PartitionBit;
        var chunk = AccessTools.TypeByName(ChunkType);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(chunk)) return code;
        var dictionary = typeof(Dictionary<,>).MakeGenericType(typeof(long), chunk);
        var (clear, ctor) = (AccessTools.Method(dictionary, nameof(Dictionary<,>.Clear)),
            AccessTools.DeclaredConstructor(chunk, []));
        if (!NotNull(clear) || !NotNull(ctor)) return code;
        var (cleared, made) = (Il.Single(code, c => c.Calls(clear)), Il.Single(code, c => c.Is(OpCodes.Newobj, ctor)));
        if (!Assert(cleared >= 0) || !Assert(made > cleared)) return code;
        var retire = AccessTools.Method(typeof(PartitionReuse), nameof(Retire)).MakeGenericMethod(chunk);
        var take = AccessTools.Method(typeof(PartitionReuse), nameof(Take)).MakeGenericMethod(chunk);
        if (Il.Substitute(code, cleared, retire) && Il.Substitute(code, made, take)) _rewritten |= PartitionBit;
        return code;
    }

    // The one array Add makes, InanimateEntities; any other shape keeps the engine's IL
    internal static List<CodeInstruction> RewriteAdd(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~AddBit;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Newarr && Equals(c.operand, typeof(List<Entity>)));
        if (Assert(at >= 0) && Il.Substitute(code, at, AccessTools.Method(typeof(PartitionReuse), nameof(TakeArray))))
            _rewritten |= AddBit;
        return code;
    }

    // The one list FetchOrCreateList makes; any other shape keeps the engine's IL
    internal static List<CodeInstruction> RewriteFetch(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~FetchBit;
        var ctor = AccessTools.DeclaredConstructor(typeof(List<Entity>), [typeof(int)]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor)) return code;
        var at = Il.Single(code, c => c.Is(OpCodes.Newobj, ctor));
        if (Assert(at >= 0) && Il.Substitute(code, at, AccessTools.Method(typeof(PartitionReuse), nameof(TakeList))))
            _rewritten |= FetchBit;
        return code;
    }

    // Stands in for partitions.Clear() at the start of every PartitionEntities: this dictionary's pool serves the call
    internal static void Retire<TChunk>(Dictionary<long, TChunk> partitions) where TChunk : class, new()
    {
        if (!NotNull(partitions)) return;
        Spares.Current = null;
        if (Enabled)
        {
            var pool = Pools<TChunk>.Of(partitions);
            pool.Recycle();
            pool.Remember(partitions);
            Spares.Current = pool;
        }
        else Pools<TChunk>.Forget(partitions);

        partitions.Clear();
        _ = Assert(partitions.Count == 0);
    }

    // Harmony's finalizer on PartitionEntities: nothing is taken from the pool outside it, and a void finalizer rethrows the original
    // exception untouched
    internal static void Close() => Spares.Current = null;

    // Stands in for new EntityPartitionChunk(): the pool's, else a new one
    internal static TChunk Take<TChunk>() where TChunk : class, new()
    {
        if (!Enabled || Spares.Current is not Pool<TChunk> pool || !pool.Free.TryPop(out var chunk))
            return new TChunk();
        Counted();
        return chunk;
    }

    // Stands in for new List<Entity>[length] in Add
    internal static List<Entity>[] TakeArray(int length)
    {
        if (!Enabled || Spares.Current is not { } spares || !spares.Arrays.TryPeek(out var array) ||
            array.Length != length) return new List<Entity>[length];
        Counted();
        return spares.Arrays.Pop();
    }

    // Stands in for new List<Entity>(capacity) in FetchOrCreateList
    internal static List<Entity> TakeList(int capacity)
    {
        if (!Enabled || Spares.Current is not { } spares || !spares.Lists.TryPop(out var list))
            return new List<Entity>(capacity);
        Counted();
        return list;
    }

    private static void Counted()
    {
        if (Counting.Hud) _ = Interlocked.Increment(ref _reused);
    }

    // What the thread's partitioning takes lists and arrays from, while one runs
    private class Spares
    {
        [ThreadStatic] internal static Spares? Current;
        public readonly Stack<List<Entity>[]> Arrays = new();
        public readonly Stack<List<Entity>> Lists = new();

        // Every list out of the array, emptied into the pool (past its cap it is left to the collector), and the slots nulled
        protected void Empty(List<Entity>[] array)
        {
            if (!NotNull(array)) return;
            for (var i = 0; i < Math.Min(array.Length, MaxCells); i++)
            {
                if (array[i] is not { } list) continue;
                list.Clear();
                if (Lists.Count < MaxLists) Lists.Push(list);
            }

            Array.Clear(array);
            _ = Assert(array.Length > MaxCells || Array.TrueForAll(array, l => l is null));
        }

        protected void Keep(List<Entity>[] array)
        {
            Empty(array);
            if (Arrays.Count < MaxArrays) Arrays.Push(array);
        }
    }

    private sealed class Pool<TChunk> : Spares where TChunk : class
    {
        public readonly Stack<TChunk> Free = new();

        // The chunks the last call handed out, each once
        private readonly HashSet<TChunk> _last = new(ReferenceEqualityComparer.Instance);

        // The chunks of the call before last are no longer in the dictionary nor in anyone's hands: emptied, they are free. A chunk
        // keeps its Entities array (its constructor's); InanimateEntities goes to the pool.
        public void Recycle()
        {
            using var chunks = _last.GetEnumerator();
            for (var i = 0; i < MaxChunks && chunks.MoveNext(); i++)
            {
                var chunk = chunks.Current;
                if (Access<TChunk>.Entities(chunk) is { } entities) Empty(entities);
                ref var inanimate = ref Access<TChunk>.Inanimate(chunk);
                if (inanimate is not null) Keep(inanimate);
                inanimate = null;
                if (Free.Count < MaxChunks) Free.Push(chunk);
            }

            _last.Clear();
            _ = Assert(Free.Count <= MaxChunks);
        }

        // The chunks the last call put into the dictionary, before Clear drops them
        public void Remember(Dictionary<long, TChunk> partitions)
        {
            using var chunks = partitions.Values.GetEnumerator();
            for (var i = 0; i < MaxChunks && chunks.MoveNext(); i++)
                if (chunks.Current is { } chunk) _ = _last.Add(chunk);
            _ = Assert(_last.Count <= partitions.Count);
        }
    }

    private static class Pools<TChunk> where TChunk : class
    {
        private static readonly ConditionalWeakTable<Dictionary<long, TChunk>, Pool<TChunk>> Table = [];

        public static Pool<TChunk> Of(Dictionary<long, TChunk> partitions)
        {
            var pool = Table.GetValue(partitions, static _ => new Pool<TChunk>());
            return NotNull(pool) ? pool : new Pool<TChunk>();
        }

        // Switched off: the dictionary's pool goes, and with it every chunk and list it held
        public static void Forget(Dictionary<long, TChunk> partitions)
        {
            if (NotNull(partitions)) _ = Table.Remove(partitions);
        }
    }

    // The two fields of the chunk type, which Komet only knows by name
    private static class Access<TChunk> where TChunk : class
    {
        public static readonly AccessTools.FieldRef<TChunk, List<Entity>[]> Entities =
            AccessTools.FieldRefAccess<TChunk, List<Entity>[]>("Entities");

        public static readonly AccessTools.FieldRef<TChunk, List<Entity>[]> Inanimate =
            AccessTools.FieldRefAccess<TChunk, List<Entity>[]>("InanimateEntities");
    }
}

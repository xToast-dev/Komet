using Komet.Core;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace Komet.Chunks;

// The integrated server's worldgen threads walk the chunk column load queue (ConcurrentIndexedFifoQueue, a yield
// iterator) in a loop, also when the world is loaded and the queue is empty: every walk is a new iterator object, tens of
// thousands a second, garbage that starts the client's collections too (the server shares the process). Here each
// foreach over that queue in ServerSystemSupplyChunks takes a walker from a small per-thread pool instead, with the
// iterator's exact steps (the same snapshot of the end, the same jump to the bleeding edge). Off by default: an October
// measurement halved the GCs but showed a lower 1 % low, so the player compares it in game.
internal static class ChunkQueueWalk
{
    private const int MaxPatched = 32;

    public static bool Enabled { get; set; }
    public static bool Installed { get; private set; }
    public static int Sites { get; private set; }

    private static MethodInfo? _rent;
    private static Type? _queueType;

    public static void Install(Harmony harmony)
    {
        var supply = AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks");
        var request = AccessTools.TypeByName("Vintagestory.Server.ChunkColumnLoadRequest");
        var open = AccessTools.TypeByName("Vintagestory.Common.ConcurrentIndexedFifoQueue`1");
        if (supply is null || request is null || open is null) return;

        _queueType = open.MakeGenericType(request);
        var walker = typeof(Walker<>).MakeGenericType(request);
        if (!(bool)walker.GetMethod(nameof(Walker<>.Bind))!.Invoke(null, [_queueType])!) return;

        _rent = walker.GetMethod(nameof(Walker<>.Rent));
        var enumerate = _queueType.GetMethod("GetEnumerator", Type.EmptyTypes);
        Sites = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(supply).ToArray().Bounded(256))
        {
            if (Sites >= MaxPatched || method.IsAbstract || !Calls(method, enumerate)) continue;

            _ = harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ChunkQueueWalk), nameof(Rewrite)));
            Sites++;
        }

        Installed = Sites > 0;
    }

    private static bool Calls(MethodBase method, MethodInfo? target)
    {
        try
        {
            return target is not null && PatchProcessor.ReadMethodBody(method)
                .Any(i => i.Value is MethodInfo { Name: "GetEnumerator" } called && called.DeclaringType == target.DeclaringType);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    // GetEnumerator on the load queue becomes Rent(queue): same stack, same IEnumerator<T> type
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        if (!Il.Take(instructions, Il.MaxInstructions, out var code) || _rent is null) return code;

        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
        {
            if (code[i].operand is MethodInfo { Name: "GetEnumerator" } called && called.DeclaringType == _queueType)
            {
                code[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, _rent).WithLabels(code[i].labels)
                    .WithBlocks(code[i].blocks);
            }
        }

        return code;
    }

    // One walk over the queue; reused per thread, nested walks get their own (depth)
    internal sealed class Walker<T> : IEnumerator<T> where T : class
    {
        private const int Depth = 8;

        [ThreadStatic] private static Walker<T>?[]? _pool;
        [ThreadStatic] private static int _depth;

        private static System.Func<object, T[]>? _items;
        private static System.Func<object, int>? _length;
        private static System.Func<object, uint>? _endOf, _edge;

        private object _queue = null!;
        private uint _pos, _endSnapshot;
        private bool _started, _pooled;
        private T _current = null!;

        public T Current => _current;
        object IEnumerator.Current => _current;

        public static bool Bind(Type queue)
        {
            _items = Getter<T[]>(queue, "elements");
            _length = Getter<int>(queue, "length");
            _endOf = Getter<uint>(queue, "end");
            _edge = Getter<uint>(queue, "startBleedingEdge");
            return _items is not null && _length is not null && _endOf is not null && _edge is not null;
        }

        private static System.Func<object, TField>? Getter<TField>(Type owner, string name)
        {
            var field = AccessTools.Field(owner, name);
            if (field is null || field.FieldType != typeof(TField)) return null;

            var target = Expression.Parameter(typeof(object));
            return Expression.Lambda<System.Func<object, TField>>(Expression.Field(Expression.Convert(target, owner), field),
                target).Compile();
        }

        // Harmony: in place of queue.GetEnumerator()
        public static IEnumerator<T> Rent(object queue)
        {
            if (!Enabled)
            {
                return ((IEnumerable<T>)queue).GetEnumerator();
            }

            var pool = _pool ??= new Walker<T>?[Depth];
            Walker<T> walker;
            if (_depth < Depth)
            {
                walker = pool[_depth] ??= new Walker<T>();
                walker._pooled = true;
                _depth++;
            }
            else
            {
                walker = new Walker<T> { _pooled = false };
            }

            (walker._queue, walker._started) = (queue, false);
            walker._endSnapshot = _endOf!(queue);
            walker._pos = _edge!(queue);
            return walker;
        }

        // The iterator's loop over pos from the bleeding edge to the end snapshot, jumping forward when the edge moved
        public bool MoveNext()
        {
            if (_started) _pos++;

            _started = true;
            if (_pos == _endSnapshot) return false;

            var edge = _edge!(_queue);
            if ((int)(edge - _pos) > 0)
            {
                _pos = edge;
                if ((int)(_pos - _endSnapshot) >= 0) return false;
            }

            _current = _items!(_queue)[(ushort)_pos % _length!(_queue)];
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        private static void Return()
        {
            if (_depth > 0) _depth--;
        }

        public void Dispose()
        {
            (_queue, _current) = (null!, null!);
            if (_pooled) Return();

            _pooled = false;
        }
    }
}

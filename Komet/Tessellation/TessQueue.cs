using Vintagestory.API.Datastructures;

namespace Komet.Tessellation;

// Where the player stands and looks, in chunks: what the queue orders by
internal readonly record struct TessView(int X, int Y, int Z, double Fx, double Fz);

// The chunks waiting for a normal tessellation pass, nearest first: each has a score from its distance to the player and its angle to
// the camera, computed again on Aim, and the lowest is taken first. The engine's semantics of its queue are kept: a key is a chunk's
// index3d, an edge-only mark is the index with the sign bit set and is dropped while the full pass of the same chunk waits (the engine
// skips it when it dequeues it, ClientWorldMap does not even enqueue it then), and a full mark that arrives while the edge-only mark
// waits turns it into a full pass.
//
// Several threads take from it, all under one lock: the tessellation thread fills it, takes and defers, Komet's worker threads
// (TessWorkers) take and hand passes back (Home). A chunk is handed to one thread at a time: while it is tessellated a new mark for it
// waits, takeable again once that pass is Done. Two passes of one chunk at once would race to the upload queue, and the older mesh
// finishing last would replace the newer one - after a block edit, the block would come back. Count is published after every change,
// so the main thread reads it without the lock.
internal sealed class TessQueue
{
    public const int MaxPending = 1 << 20;

    // Within NearChunks direction does not matter: the player may turn at once
    private const int MaxStale = 1 << 16, NearChunks = 2;

    private const int SeqBits = 24, SeqMask = (1 << SeqBits) - 1;
    private const int MaxFlights = WorkerPool.MaxThreads + 1; // one chunk per tessellation thread

    private readonly Lock _gate = new();
    private readonly PriorityQueue<(long Index, long Version), long> _heap = new();

    // Passes a worker could not finish, for the tessellation thread (TryTakeHome)
    private readonly List<long> _home = [];

    // Blocked: pending while in flight, pushed again by Done
    private readonly HashSet<long> _inFlight = [], _blocked = [];

    private readonly Dictionary<long, Entry> _pending = [];
    private int _count;
    private List<long> _deferred = [], _returning = [];
    private long _seq, _version, _mulX = 1, _mulZ = 1;
    private TessView _view;

    public int Count => Volatile.Read(ref _count); // waiting, handed back and deferred marks

    // The map's index multipliers (index3dMulX, index3dMulZ): how an index splits into chunk coordinates
    public void Map(long mulX, long mulZ)
    {
        if (!Assert(mulX > 0) || !Assert(mulZ > 0)) return;
        lock (_gate) (_mulX, _mulZ) = (mulX, mulZ);
    }

    // Marks from the engine's queue: index3d, or index3d | long.MinValue for an edge-only pass
    public void AddRange(ReadOnlySpan<long> raws)
    {
        _ = Assert(raws.Length <= MaxPending);
        lock (_gate)
        {
            for (var i = 0; i < Math.Min(raws.Length, MaxPending); i++) Add(raws[i]);
            Publish();
        }
    }

    // The next chunk to tessellate, in flight until Done or Home; raw is what the engine's queue held
    public bool TryTake(out long raw)
    {
        raw = 0;
        lock (_gate)
        {
            var nodes = _heap.Count; // shrinks as the loop takes
            _ = Assert(nodes <= MaxPending + MaxStale); // Push rebuilds before the superseded nodes pass MaxStale
            for (var i = 0; i < Math.Min(nodes, MaxPending + MaxStale) && _heap.TryDequeue(out var node, out _); i++)
            {
                if (!_pending.TryGetValue(node.Index, out var entry) || entry.Version != node.Version)
                    continue; // superseded
                if (_inFlight.Contains(node.Index))
                {
                    _ = _blocked.Add(node.Index);
                    continue;
                }

                _ = _pending.Remove(node.Index);
                _ = _inFlight.Add(node.Index);
                raw = Raw(node.Index, entry);
                Publish();
                return Assert(node.Index >= 0) && Assert(_inFlight.Count <= MaxFlights);
            }
        }

        return false;
    }

    // A pass that did not come from the queue (a priority mark): the chunk in flight, false while another thread tessellates it
    public bool TryBegin(long index)
    {
        if (!Assert(index >= 0)) return false;
        lock (_gate)
        {
            var begun = _inFlight.Add(index);
            return Assert(_inFlight.Count <= MaxFlights) && begun;
        }
    }

    // The pass of a taken or begun chunk ended; a mark that arrived meanwhile becomes takeable. After a Clear the chunk is no longer
    // known and nothing happens.
    public void Done(long index)
    {
        lock (_gate) _ = Release(index);
    }

    // A worker's pass the engine wants again: done, and handed to the tessellation thread (RetryTesselationException queues its fix on
    // the main thread only when thrown there)
    public void Home(long raw)
    {
        lock (_gate)
        {
            if (!Release(raw & long.MaxValue)) return; // another world since
            if (Assert(_home.Count < MaxPending)) _home.Add(raw);
            Publish();
        }
    }

    // The oldest handed-back pass whose chunk no other thread is on, in flight until Done
    public bool TryTakeHome(out long raw)
    {
        raw = 0;
        lock (_gate)
        {
            _ = Assert(_home.Count <= MaxPending); // Home refuses more
            for (var i = 0; i < Math.Min(_home.Count, MaxPending); i++)
            {
                if (!_inFlight.Add(_home[i] & long.MaxValue)) continue;
                raw = _home[i];
                _home.RemoveAt(i);
                Publish();
                return Assert(_inFlight.Count <= MaxFlights);
            }
        }

        return false;
    }

    // The engine wants the chunk again (not loaded from the server yet, or RetryTesselationException): back with the next Undefer, not
    // at once, or the same chunk would be taken again in the same tick
    public void Defer(long raw)
    {
        lock (_gate)
        {
            if (Assert(_deferred.Count < MaxPending)) _deferred.Add(raw);
            Publish();
        }
    }

    public void Undefer()
    {
        lock (_gate)
        {
            (_deferred, _returning) = (_returning, _deferred);
            foreach (var raw in _returning.Bounded(MaxPending)) Add(raw);
            _returning.Clear();
            _ = Assert(_deferred.Count == 0);
            Publish();
        }
    }

    // Scores again from where the player is now
    public void Aim(TessView view)
    {
        if (!Finite(view.Fx) || !Finite(view.Fz)) return;
        lock (_gate)
        {
            _view = view;
            Rebuild();
        }
    }

    // Everything still waiting goes into the engine's queue, as it would hold it, for when the schedule is switched off; chunks in
    // flight stay in flight
    public void Drain(UniqueQueue<long> into)
    {
        if (!NotNull(into)) return;
        lock (_gate)
        {
            using var entries = _pending.GetEnumerator();
            for (var i = 0; i < MaxPending && entries.MoveNext(); i++)
                into.Enqueue(Raw(entries.Current.Key, entries.Current.Value));
            foreach (var raw in _deferred.Bounded(MaxPending)) into.Enqueue(raw);
            foreach (var raw in _home.Bounded(MaxPending)) into.Enqueue(raw);
            Empty(false);
        }
    }

    // How many waiting marks lie within radius columns of the view's chunk
    public int Near(TessView view, int radius)
    {
        if (!Assert(radius >= 0)) return 0;
        var n = 0;
        lock (_gate)
        {
            using var keys = _pending.Keys.GetEnumerator();
            for (var i = 0; i < MaxPending && keys.MoveNext(); i++)
                n += Within(keys.Current, view, radius, _mulX, _mulZ) ? 1 : 0;
        }

        return n;
    }

    // Another world: nothing waits and nothing is in flight any more
    public void Clear()
    {
        lock (_gate) Empty(true);
    }

    // Whether the chunk of an index lies within radius columns of the view's chunk
    public static bool Within(long index, TessView view, int radius, long mulX, long mulZ)
    {
        if (!Assert(index >= 0) || !Assert(mulX > 0 && mulZ > 0)) return false;
        int x = (int)(index % mulX), z = (int)(index / mulX % mulZ);
        return Math.Abs(x - view.X) <= radius && Math.Abs(z - view.Z) <= radius;
    }

    // Lower is sooner. Squared distance in chunks; past NearChunks weighted by the angle to where the camera looks: ×1 ahead, ×2 to the
    // side, ×3 behind. f is the camera's look direction on the ground, normalised.
    public static long Score(int dx, int dy, int dz, double fx, double fz)
    {
        if (!Finite(fx) || !Finite(fz)) return long.MaxValue >> SeqBits;
        var d2 = (long)dx * dx + (long)dy * dy + (long)dz * dz;
        if (!Assert(d2 >= 0) || d2 <= NearChunks * NearChunks) return d2;
        var h = Math.Sqrt((double)dx * dx + (double)dz * dz);
        var cos = h > 0 ? Math.Clamp((dx * fx + dz * fz) / h, -1, 1) : 1;
        return (long)(d2 * (2 - cos));
    }

    // The camera's yaw as the engine uses it (TesselatedChunk.RecalcPriority: Atan2(-dz, dx) against CameraYaw), on the ground
    public static (double Fx, double Fz) Facing(double yaw)
    {
        if (!Finite(yaw)) return (1, 0);
        var (fx, fz) = (Math.Cos(yaw), -Math.Sin(yaw));
        return Assert(Math.Abs(fx * fx + fz * fz - 1) < 1e-6) ? (fx, fz) : (1, 0);
    }

    // Under the lock, as every helper below
    private void Add(long raw)
    {
        var (index, full) = (raw & long.MaxValue, raw >= 0);
        if (!Assert(_pending.Count < MaxPending)) return;
        var known = _pending.TryGetValue(index, out var entry);
        if (known && (!full || entry.Full)) return; // the waiting pass covers it
        entry = known ? entry with { Full = true, Version = ++_version } : new Entry(full, ++_version, ++_seq);
        _pending[index] = entry;
        if (_inFlight.Contains(index)) _ = _blocked.Add(index); // takeable once the pass running on it is done
        else Push(index, entry.Version, entry.Seq);
    }

    // False when the chunk was not in flight: the queue was cleared for another world since it was taken
    private bool Release(long index)
    {
        if (!Assert(index >= 0) || !_inFlight.Remove(index)) return false;
        if (_blocked.Remove(index) && _pending.TryGetValue(index, out var entry)) Push(index, entry.Version, entry.Seq);
        return true;
    }

    private void Empty(bool flights)
    {
        _pending.Clear();
        _deferred.Clear();
        _home.Clear();
        _blocked.Clear();
        _heap.Clear();
        if (flights) _inFlight.Clear();
        (_seq, _version) = (0, 0);
        Publish();
    }

    private void Publish()
    {
        _ = Assert(_pending.Count <= MaxPending); // Add refuses more
        Volatile.Write(ref _count, _pending.Count + _deferred.Count + _home.Count);
    }

    private static long Raw(long index, Entry entry) => entry.Full ? index : index | long.MinValue;

    // The score in the high bits, the arrival order below it: equal scores keep the engine's FIFO
    private long Priority(long index, long seq)
    {
        int x = (int)(index % _mulX), y = (int)(index / (_mulX * _mulZ)), z = (int)(index / _mulX % _mulZ);
        var score = Score(x - _view.X, y - _view.Y, z - _view.Z, _view.Fx, _view.Fz);
        return Assert(score >= 0)
            ? (Math.Min(score, long.MaxValue >> SeqBits) << SeqBits) | (seq & SeqMask)
            : long.MaxValue;
    }

    private void Push(long index, long version, long seq)
    {
        _heap.Enqueue((index, version), Priority(index, seq));
        if (_heap.Count > _pending.Count + MaxStale) Rebuild(); // superseded nodes piled up
        _ = Assert(_heap.Count > 0);
    }

    private void Rebuild()
    {
        _heap.Clear();
        using var entries = _pending.GetEnumerator();
        for (var i = 0; i < MaxPending && entries.MoveNext(); i++)
        {
            var (index, entry) = (entries.Current.Key, entries.Current.Value);
            if (_inFlight.Contains(index)) _ = _blocked.Add(index);
            else _heap.Enqueue((index, entry.Version), Priority(index, entry.Seq));
        }

        _ = Assert(_heap.Count <= _pending.Count);
    }

    private readonly record struct Entry(bool Full, long Version, long Seq);
}

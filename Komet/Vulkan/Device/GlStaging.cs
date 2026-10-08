namespace Komet.Vulkan;

// Memory OpenGL writes for Vulkan to copy from - a texture packed into it, a buffer copied into it - so neither waits for the
// other: a ring carved from the shared arena, no exportable allocation of its own. What does not fit gets a buffer of its own,
// exportable only until its frame is done. A range is taken on OpenGL's timeline before the signal the copying segment waits
// for, and comes back once the frame that copied from it (Used) is done on the GPU, in the order taken.
internal sealed class GlStaging(VulkanDevice device) : IDisposable
{
    public const ulong RingBytes = SharedArena.BlockBytes / 4;
    private const ulong MaxOwnedBytes = 512UL << 20; // in flight in buffers of their own (a world's first frames copy its atlases)
    private const int MaxTaken = 1 << 14;
    private const ulong Alignment = 256;

    public readonly record struct Range(ulong Buffer, int Gl, ulong At, long Ticket);

    // Bytes: what it holds of the ring, alignment and the end skipped at a wrap included
    private sealed class Taken(ulong bytes)
    {
        public ulong Bytes { get; } = bytes;
        public SharedBuffer? Own { get; init; }
        public long Frame { get; set; } = long.MaxValue;
    }

    private readonly List<Taken> _taken = [];
    private readonly List<SharedBuffer> _owned = []; // the buffers of their own, as long as their ranges
    private SharedBuffer? _ring;
    private ulong _head, _used, _ownedBytes;
    private long _first; // the ticket of _taken[0]
    private uint[]? _names;

    public long Version { get; private set; } // bumps when a buffer comes or goes
    public long Owned { get; private set; } // ranges that got a buffer of their own
    public ulong Bytes { get; private set; } // taken so far

    // The GL names of every buffer that may hold a range, for OpenGL's signal
    public uint[] Buffers => _names ??= Named();

    private uint[] Named()
    {
        var names = new List<uint>(_taken.Count + 1);
        if (_ring is { } ring) names.Add((uint)ring.Gl);
        foreach (var taken in _taken.Bounded(MaxTaken))
            if (taken.Own is { } own)
                names.Add((uint)own.Gl);
        return Assert(names.Count <= MaxTaken + 1) ? [.. names] : [];
    }

    // frame: the frame whose segment copies from it, long.MaxValue while not known (Used names it later)
    public Range? Take(ulong bytes, long frame, out string why)
    {
        why = "";
        if (!Assert(bytes > 0) || !Assert(_taken.Count <= MaxTaken) || _taken.Count == MaxTaken)
            return None(out why, "too many ranges in flight");
        Bytes += bytes;
        if (Carved(bytes) is { } at) return Taking(new Taken(at.Consumed), (_ring!, at.Start), frame);
        return Own(bytes, frame, out why);
    }

    // A buffer of its own (the newest one's, until Collect disposes it)
    private Range? Own(ulong bytes, long frame, out string why)
    {
        why = $"no staging for {bytes >> 10} KB";
        if (_owned.Count >= MaxTaken || (_ownedBytes > 0 && _ownedBytes + bytes > MaxOwnedBytes)) return null;
        _newest = SharedBuffer.Create(device, bytes, out var refused, dedicated: true);
        if (_newest is not { } own) return None(out why, $"{why}: {refused}");
        _owned.Add(own);
        (Owned, _ownedBytes) = (Owned + 1, _ownedBytes + bytes);
        _ = Assert(_ownedBytes >= bytes) && Assert(own.Size == bytes);
        return Taking(new Taken(0) { Own = own }, (own, 0), frame);
    }

    private SharedBuffer? _newest;

    private Range Taking(Taken taken, (SharedBuffer Buffer, ulong At) into, long frame)
    {
        _ = Assert(into.At < into.Buffer.Size) && NotNull(taken);
        taken.Frame = frame;
        _taken.Add(taken);
        if (taken.Own is not null) (_names, Version) = (null, Version + 1);
        return new Range(into.Buffer.Buffer, into.Buffer.Gl, into.At, _first + _taken.Count - 1);
    }

    private static Range? None(out string why, string reason)
    {
        why = reason;
        _ = Assert(reason.Length > 0);
        return null;
    }

    // Where in the ring, and what the ring gives up for it
    private (ulong Start, ulong Consumed)? Carved(ulong bytes)
    {
        if (bytes > RingBytes / 2 || !Assert(_used <= RingBytes)) return null;
        if (_ring is null)
        {
            _ring = SharedBuffer.Create(device, RingBytes, out _);
            if (_ring is null) return null;
            (_names, Version) = (null, Version + 1);
        }

        var start = (_head + Alignment - 1) / Alignment * Alignment;
        var wraps = start + bytes > RingBytes;
        var consumed = wraps ? RingBytes - _head + bytes : start - _head + bytes;
        if (_used + consumed > RingBytes) return null;
        if (wraps) start = 0;
        (_head, _used) = (start + bytes, _used + consumed);
        return Assert(start + bytes <= RingBytes) ? (start, consumed) : null;
    }

    public void Used(long ticket, long frame)
    {
        var at = ticket - _first;
        if (!Assert(frame >= 0) || !Assert(at >= 0 && at < _taken.Count)) return;
        _taken[(int)at].Frame = frame;
    }

    // Gives back, in the order taken, the ranges whose frame the GPU is done with
    public void Collect(long done)
    {
        var count = 0;
        for (var i = 0; i < Math.Min(_taken.Count, MaxTaken) && _taken[i].Frame <= done; i++) count++;
        if (count == 0) return;
        for (var i = 0; i < Math.Min(count, MaxTaken); i++)
        {
            var taken = _taken[i];
            _used -= taken.Bytes;
            if (taken.Own is not { } own) continue;
            _ = _owned.Remove(own);
            _ownedBytes -= own.Size;
            own.Dispose();
            (_names, Version) = (null, Version + 1);
        }

        _taken.RemoveRange(0, count);
        _first += count;
        if (_taken.Count == 0) (_head, _used) = (0, 0); // empty: the next starts at the front again
        _ = Assert(_used <= RingBytes) && Assert(_first >= count);
    }

    public void Dispose()
    {
        _ = Assert(_taken.Count <= MaxTaken) && device.WaitIdle();
        _newest?.Dispose(); // one of _owned, or gone already: disposing is idempotent
        foreach (var own in _owned.Bounded(MaxTaken)) own.Dispose();
        _owned.Clear();
        _taken.Clear();
        _ring?.Dispose();
        (_ring, _names, _head, _used, _newest, _ownedBytes) = (null, null, 0, 0, null, 0);
        device.Arena.Trim(); // the ring's block, when nothing else is carved from it
    }
}

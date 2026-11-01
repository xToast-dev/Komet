namespace Komet.Vulkan;

// Pool uploads (PoolWritten) copied from frame host memory: in the open segment, else in order into the next segment's
// setup or, at frame end, a transfer of their own (PoolsAlone)
internal sealed partial class TerrainRenderer
{
    private const int MaxPoolCopies = 1024;

    private readonly List<PoolCopy> _poolCopies = [];
    private readonly Dictionary<SharedBuffer, (long From, long To)> _poolSpans = new(ReferenceEqualityComparer.Instance);
    private FrameStaging? _poolStaging;
    private Action<IntPtr>? _poolsRecorded;

    // Pool uploads copied, and those without host memory (the pool buffer misses them)
    public long PoolWrites { get; private set; }
    public long PoolWritesLost { get; private set; }

    private readonly record struct PoolCopy(HostBuffer From, long At, SharedBuffer To, long Offset, int Size);

    // TerrainRebuild: all pools go, and so do pending writes (their buffers would be gone)
    public void PoolWritesDropped() => _poolCopies.Clear();

    // Copied in the open segment, else queued for the next segment's setup or the frame's end; a full queue with no
    // segment open runs as a transfer of its own first. False: lost (the queue full with a segment open)
    private bool Copied(PoolCopy copy)
    {
        if (Frame.Open && Frame.CopyBuffer((copy.From.Buffer, (ulong)copy.At), (copy.To.Buffer, (ulong)copy.Offset), (ulong)copy.Size))
            return true;
        if (_poolCopies.Count >= MaxPoolCopies && !Frame.Open) _ = Frame.Alone(_poolsRecorded ??= PoolsRecorded);
        if (_poolCopies.Count >= MaxPoolCopies) return false;
        _poolCopies.Add(copy);
        return true;
    }

    public void PoolsOpened()
    {
        if (_poolCopies.Count == 0 || !Assert(Frame.Open)) return;
        PoolsRecorded(Frame.Setup);
    }

    // In order into a segment's setup or their own transfer
    private unsafe void PoolsRecorded(IntPtr setup)
    {
        if (!Assert(setup != IntPtr.Zero)) return;
        var written = 0; // the copies since the last barrier: _poolCopies[first..]
        var first = 0;
        _poolSpans.Clear();
        for (var c = 0; c < Math.Min(_poolCopies.Count, MaxPoolCopies); c++)
        {
            var copy = _poolCopies[c];
            var end = copy.Offset + copy.Size;
            // the walk only inside the bytes written to its buffer since the barrier: n² checks for a full queue otherwise
            var known = _poolSpans.TryGetValue(copy.To, out var span);
            if (known && span.From < end && copy.Offset < span.To && Overlaps(copy, first, written))
            {
                var memory = new Vk.GlobalBarrier
                {
                    SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessTransferWrite
                };
                VkApi.CmdPipelineBarrier(setup, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
                (first, written) = (c, 0); // the same range written twice: the later copy after the earlier one
                _poolSpans.Clear();
                known = false;
            }

            _poolSpans[copy.To] = known ? (Math.Min(span.From, copy.Offset), Math.Max(span.To, end)) : (copy.Offset, end);
            var region = new Vk.BufferCopy
            {
                SourceOffset = (ulong)copy.At, TargetOffset = (ulong)copy.Offset, Size = (ulong)copy.Size
            };
            VkApi.CmdCopyBuffer(setup, copy.From.Buffer, copy.To.Buffer, 1, &region); // the setup's barriers order the rest
            written++;
        }

        _poolCopies.Clear();
        _poolSpans.Clear(); // holds no pool buffer past the flush
    }

    private bool Overlaps(PoolCopy copy, int first, int written)
    {
        for (var i = first; i < Math.Min(first + written, MaxPoolCopies); i++)
        {
            var w = _poolCopies[i];
            if (ReferenceEquals(w.To, copy.To) && w.Offset < copy.Offset + copy.Size && copy.Offset < w.Offset + w.Size)
                return true;
        }

        return false;
    }

    // glBufferSubData into a pool buffer (OpenGL gets it too): copied from client memory in the open segment, before
    // the next segment's draws, or at frame end
    public unsafe bool PoolWritten(uint buffer, long offset, long size, IntPtr data)
    {
        if (data == 0 || size <= 0 || Pools.BufferOf((int)buffer) is not { } to || offset < 0 || size > int.MaxValue)
            return false;
        var written = new ReadOnlySpan<byte>((void*)data, (int)size);
        if (offset + size > (long)to.Size) return Tallied(false);
        if (Frame.Staging is null) // before the first frame: with the textures' uploads
            return Images.Write((to.Buffer, (ulong)offset), written) ? Tallied(true) : Tallied(false);

        _poolStaging ??= new FrameStaging(Device, Frame);
        if (_poolStaging.Take((int)size, out var at) is not { } staging || !Assert(at >= 0)) return Tallied(false);
        written.CopyTo(new Span<byte>(staging.Pointer(at), (int)size));
        return Tallied(Copied(new PoolCopy(staging, at, to, offset, (int)size)));
    }

    // A pool write its caller fills itself, as the face packing does: where to write, and what the commit copies
    public readonly record struct PoolSlot(IntPtr Into, int Size, long Offset, long At, HostBuffer? Host, SharedBuffer? Pool);

    // Staging for the given bytes of face data at the pool offset; false where only the copying write can do it: no
    // staging before the first frame, staging full
    public unsafe bool PoolReserve(uint buffer, long offset, long size, out PoolSlot slot)
    {
        slot = default;
        if (size <= 0 || Pools.BufferOf((int)buffer) is not { } to || offset < 0 || size > int.MaxValue || Frame.Staging is null)
            return false;
        if (offset + size > (long)to.Size || _poolCopies.Count >= MaxPoolCopies) return false;
        _poolStaging ??= new FrameStaging(Device, Frame);
        if (_poolStaging.Take((int)size, out var at) is not { } staging || !Assert(at >= 0)) return false;
        slot = new PoolSlot((IntPtr)staging.Pointer(at), (int)size, offset, at, staging, to);
        return Assert(slot.Into != IntPtr.Zero);
    }

    // The reserved staging filled: copied into the pool as PoolWritten copies
    public bool PoolCommitted(PoolSlot slot)
    {
        if (slot.Host is not { } staging || slot.Pool is not { } to || !Assert(slot.Size > 0)) return false;
        return Tallied(Copied(new PoolCopy(staging, slot.At, to, slot.Offset, slot.Size)));
    }

    private bool Tallied(bool written)
    {
        if (written) PoolWrites++;
        else PoolWritesLost++;
        return written;
    }

    private void PoolsAlone()
    {
        if (_poolCopies.Count > 0 && Assert(!Frame.Open)) _ = Frame.Alone(_poolsRecorded ??= PoolsRecorded);
        _poolStaging?.Collect();
    }
}

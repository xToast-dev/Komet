using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Writes are replayed in the order OpenGL got them. A buffer mapped for good (persistent: the engine writes through the
// pointer, unseen) has no mirror. A write goes into the open segment's setup, which runs before its draws, while none of
// them read the mirror; once one did, the mirror is renamed: a new range takes the old contents and the write, later draws
// read that, earlier ones keep the old range until their frame is done. A buffer a draw reads without a mirror is imported on
// the GPU: OpenGL copies it into GlStaging, the open segment's setup copies that into the mirror after OpenGL's signal (a late
// one when the segment had it already), so neither the engine's thread nor the draw waits for OpenGL's queue.
internal sealed unsafe class BufferMirrors : IDisposable
{
    public const long EagerLimit = 16L << 20;

    // A new buffer of at most this many bytes is mirrored from its first contents on, before any draw read it: a draw reading a
    // buffer without a mirror reads it back from OpenGL, which waits for everything queued before (it was most of the scene's
    // stutter, a new entity or block entity mesh at a time); a larger one only once a draw wanted it
    public const long FirstLimit = 1L << 20;
    private const int MaxMirrors = 1 << 17, MaxPending = 1 << 14, MaxWritten = 256, MaxPersistent = 1 << 12,
        MaxReasons = 8, Recent = 128;

    private readonly VulkanDevice _device;
    private readonly VulkanFrame _frame;
    private readonly DeviceHeap _heap;
    private readonly Dictionary<uint, Mirror> _mirrors = [];
    private readonly HashSet<uint> _persistent = [];
    private readonly HashSet<uint> _wanted = []; // buffers some draw read: their writes are mirrored as they come
    private readonly HashSet<uint> _specified = []; // buffers given contents since the mirrors began, for the report
    private readonly List<Copy> _pending = [];
    private readonly List<(ulong Buffer, ulong Offset, ulong Size)> _written = [];
    private readonly FrameStaging _staging;
    private readonly Dictionary<string, long> _reasons = new(StringComparer.Ordinal);
    private readonly GlStaging? _imports;
    private readonly Dictionary<uint, long> _sizes = []; // each buffer's size as glBufferData or glBufferStorage gave it

    // In front of _mirrors, by the buffer's low bits: a frame's draws read the same few buffers again and again
    private readonly (uint Buffer, Mirror? Mirror)[] _recent = new (uint, Mirror?)[Recent];

    private sealed class Mirror(DeviceHeap.Range range, long size)
    {
        public DeviceHeap.Range Range { get; set; } = range;
        public long Size { get; } = size;
        public long ReadIn { get; set; } = -1; // the segment (VulkanFrame.Serial) that last read the range
        public bool Stale { get; private set; }
        public string Why { get; private set; } = ""; // what made it stale, for the report

        // Its buffer is in _wanted: only Gone and Forget take it out, and they drop the mirror too
        public bool Wanted { get; set; }

        // No longer the buffer's mirror (dropped or replaced): a stale entry of _recent
        public bool Gone { get; set; }

        public void Spoil(string why)
        {
            (Stale, Why) = (true, why);
            _ = Assert(why.Length > 0) && Assert(Size > 0);
        }
    }

    private readonly record struct Copy(ulong From, ulong FromOffset, ulong To, ulong ToOffset, ulong Size);

    // imports: OpenGL's staging for buffers read back (none: no import, the draw is left to OpenGL)
    public BufferMirrors(VulkanDevice device, VulkanFrame frame, GlStaging? imports)
    {
        _ = NotNull(device) && NotNull(frame);
        (_device, _frame, _heap, _staging) = (device, frame, new DeviceHeap(device), new FrameStaging(device, frame));
        _imports = imports;
    }

    public int Count => _mirrors.Count;

    // About what the mirrors keep on the managed heap, for the report (the contents are in device memory): a mirror and its entry, a
    // size and the set entries of each buffer known
    public long ManagedBytes => Assert(_mirrors.Count <= MaxMirrors) && Assert(_sizes.Count <= MaxMirrors)
        ? (96L * _mirrors.Count) + (24L * _sizes.Count) + (16L * (_wanted.Count + _specified.Count + _persistent.Count))
        : 0;
    public ulong Bytes => _heap.Bytes;
    public long Imports { get; private set; }
    public long ImportedBytes { get; private set; }
    public long Asked { get; private set; } // sizes asked of OpenGL (a buffer made before the mirrors): a wait for glthread

    public string Reasons =>
        string.Join(", ", _reasons.OrderByDescending(r => r.Value).Take(MaxReasons).Select(r => $"{r.Key} {r.Value}"));
    public long Renames { get; private set; }
    public long Writes { get; private set; }

    public bool Persistent(uint buffer) => Assert(_persistent.Count <= MaxPersistent) && _persistent.Contains(buffer);

    public (ulong Buffer, ulong Offset, ulong Size)? Read(uint buffer)
    {
        if (buffer == 0 || !Assert(_frame.Open)) return null;
        // read for every draw: a buffer with a mirror is never persistent (Data drops it, Imported is not asked for one)
        var mirror = Find(buffer);
        if ((mirror is null || mirror.Stale) && _persistent.Contains(buffer)) return null;
        if (mirror is null || mirror.Stale) mirror = Imported(buffer, mirror);
        if (mirror is null) return null;
        if (!mirror.Wanted && _wanted.Count < MaxMirrors)
        {
            _ = _wanted.Add(buffer);
            mirror.Wanted = true;
        }

        mirror.ReadIn = _frame.Serial;
        _ = Assert(mirror.Range.Size >= (ulong)mirror.Size);
        return (mirror.Range.Buffer, mirror.Range.Offset, (ulong)mirror.Size);
    }

    private Mirror? Find(uint buffer)
    {
        ref var slot = ref _recent[buffer % Recent];
        if (slot.Buffer == buffer && slot.Mirror is { Gone: false } known) return known;
        var found = _mirrors.GetValueOrDefault(buffer);
        if (found is not null) slot = (buffer, found);
        _ = Assert(found is null || !found.Gone) && Assert(_recent.Length == Recent);
        return found;
    }

    // glBufferData and glBufferStorage
    public void Data(uint buffer, long size, IntPtr data, bool persistent)
    {
        if (buffer == 0 || !Assert(size >= 0)) return;
        Drop(buffer);
        if (_specified.Count < MaxMirrors) _ = _specified.Add(buffer);
        if (_sizes.Count < MaxMirrors || _sizes.ContainsKey(buffer)) _sizes[buffer] = size;
        if (persistent && _persistent.Count < MaxPersistent) _ = _persistent.Add(buffer);
        else _ = _persistent.Remove(buffer);
        if (persistent || size is 0 or > EagerLimit || _mirrors.Count >= MaxMirrors ||
            (!_wanted.Contains(buffer) && size > FirstLimit)) return;
        if (_heap.Take((ulong)size) is not { } range) return;
        var mirror = new Mirror(range, size);
        _mirrors[buffer] = mirror;
        _ = Assert(range.Size >= (ulong)size);
        if (data != IntPtr.Zero && !Stage(new ReadOnlySpan<byte>((void*)data, (int)size), range, 0))
            mirror.Spoil("no staging room"); // read back when next read
        Writes++;
    }

    public void SubData(uint buffer, long offset, long size, IntPtr data)
    {
        if (Find(buffer) is not { } mirror || mirror.Stale || !Assert(offset >= 0 && size >= 0)) return;
        if (data == IntPtr.Zero || offset + size > mirror.Size || size > int.MaxValue)
        {
            mirror.Spoil(data == IntPtr.Zero ? "sub-data from a bound buffer" : "sub-data past the end");
            return;
        }

        if (size == 0 || !Assert(mirror.Size > 0)) return;
        Renamed(mirror);
        if (!Stage(new ReadOnlySpan<byte>((void*)data, (int)size), mirror.Range, (ulong)offset))
            mirror.Spoil("no staging room");
        Writes++;
    }

    // glCopyBufferSubData
    public void Copied(uint from, uint to, long fromOffset, long toOffset, long size)
    {
        if (!_mirrors.TryGetValue(to, out var target) || target.Stale || !Assert(size >= 0)) return;
        if (!_mirrors.TryGetValue(from, out var source) || source.Stale || fromOffset + size > source.Size ||
            toOffset + size > target.Size || fromOffset < 0 || toOffset < 0)
        {
            target.Spoil("a copy from an unmirrored buffer");
            return;
        }

        if (size == 0 || !Assert(source.Size > 0)) return;
        Renamed(target);
        if (!Enqueue(new Copy(source.Range.Buffer, source.Range.Offset + (ulong)fromOffset, target.Range.Buffer,
                target.Range.Offset + (ulong)toOffset, (ulong)size))) target.Spoil("copies queued full");
        Writes++;
    }

    public void Unseen(uint buffer, string why = "a write unseen")
    {
        if (why == GlTap.OwnData) _ = _sizes.Remove(buffer);
        if (_mirrors.TryGetValue(buffer, out var mirror) && Assert(mirror.Size > 0)) mirror.Spoil(why);
        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    public void Forget()
    {
        foreach (var buffer in _mirrors.Keys.ToArray().Bounded(MaxMirrors)) Drop(buffer);
        _persistent.Clear();
        _wanted.Clear();
        _specified.Clear();
        _sizes.Clear();
        _ = Assert(_mirrors.Count == 0);
    }

    public void Gone(ReadOnlySpan<uint> buffers)
    {
        foreach (var buffer in buffers.Bounded(4096))
        {
            Drop(buffer);
            _ = _persistent.Remove(buffer);
            _ = _wanted.Remove(buffer);
            _ = _specified.Remove(buffer);
            _ = _sizes.Remove(buffer);
        }

        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    private void Drop(uint buffer)
    {
        if (!_mirrors.Remove(buffer, out var mirror) || !Assert(mirror.Size > 0)) return;
        mirror.Gone = true;
        _heap.Retire(mirror.Range, _frame.Number);
        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    private void Renamed(Mirror mirror)
    {
        if (!NotNull(mirror) || !_frame.Open || mirror.ReadIn != _frame.Serial) return;
        if (_heap.Take((ulong)mirror.Size) is not { } range)
        {
            mirror.Spoil("no heap room to rename");
            return;
        }

        var old = mirror.Range;
        if (!Enqueue(new Copy(old.Buffer, old.Offset, range.Buffer, range.Offset, (ulong)mirror.Size)))
            mirror.Spoil("copies queued full");
        _heap.Retire(old, _frame.Number);
        (mirror.Range, mirror.ReadIn) = (range, -1);
        Renames++;
        _ = Assert(range.Size >= (ulong)mirror.Size);
    }

    private Mirror? Imported(uint buffer, Mirror? old)
    {
        if (!Assert(buffer > 0) || !Assert(_mirrors.Count <= MaxMirrors) || _imports is null) return null;
        var size = SizeOf(buffer);
        VulkanWatch.Mark($"mirror: importing buffer {buffer} of {size >> 10} KB");
        if (size <= 0 || size > int.MaxValue || _heap.Take((ulong)size) is not { } range) return null;
        if (!_frame.Late() || _imports.Take((ulong)size, _frame.Number, out _) is not { } staged)
        {
            _heap.Retire(range, _frame.Number);
            return null;
        }

        if (old is not null)
        {
            _heap.Retire(old.Range, _frame.Number);
            old.Gone = true; // removed or replaced below
        }

        var start = Hitches.Now;
        using (GlTap.Quietly())
            GL.CopyNamedBufferSubData((int)buffer, staged.Gl, IntPtr.Zero, new IntPtr((long)staged.At), (int)size);
        Hitches.Since(Hitches.Kind.BufferRead, start); // no wait: what it costs the engine's thread
        var mirror = new Mirror(range, size);
        if (!Enqueue(new Copy(staged.Buffer, staged.At, range.Buffer, range.Offset, (ulong)size)))
            mirror.Spoil("copies queued full");
        _mirrors[buffer] = mirror;
        (Imports, ImportedBytes) = (Imports + 1, ImportedBytes + size);
        var why = old?.Why ?? FirstWhy(buffer, size);
        if (_reasons.Count < MaxReasons || _reasons.ContainsKey(why)) _reasons[why] = _reasons.GetValueOrDefault(why) + 1;
        return mirror;
    }

    // As OpenGL was told it; asked of OpenGL (a wait for Mesa's glthread, not for the GPU) for a buffer made before the mirrors
    private long SizeOf(uint buffer)
    {
        if (_sizes.TryGetValue(buffer, out var size) || !Assert(buffer > 0)) return size;
        GL.GetNamedBufferParameter((int)buffer, BufferParameterName.BufferSize, out int asked);
        Asked++;
        if (_sizes.Count < MaxMirrors) _sizes[buffer] = asked;
        _ = Assert(asked >= 0);
        return asked;
    }

    private string FirstWhy(uint buffer, long size)
    {
        if (!Assert(buffer > 0) || _wanted.Contains(buffer)) return "respecified without a mirror";
        if (size > FirstLimit) return "first read of a large buffer";
        _ = Assert(_specified.Count <= MaxMirrors);
        return _specified.Contains(buffer) ? "first read" : "first read of a buffer made before the mirrors";
    }

    private bool Stage(ReadOnlySpan<byte> data, DeviceHeap.Range range, ulong offset)
    {
        if (!Assert(offset + (ulong)data.Length <= range.Size) || data.IsEmpty) return true;
        var staging = _staging.Take(data.Length, out var at);
        if (staging is null) return false;
        _ = Assert(at >= 0);
        data.CopyTo(new Span<byte>(staging.Pointer(at), data.Length));
        return Enqueue(new Copy(staging.Buffer, (ulong)at, range.Buffer, range.Offset + offset, (ulong)data.Length));
    }

    private bool Enqueue(Copy copy)
    {
        if (!Assert(copy.Size > 0) || !Assert(_pending.Count <= MaxPending)) return false;
        if (_frame.Open)
        {
            Record(_frame.Setup, copy);
            return true;
        }

        if (_pending.Count >= MaxPending) return false;
        _pending.Add(copy);
        return true;
    }

    // Copies overlapping what an earlier copy of the same transfers wrote wait for it
    private void Record(IntPtr commands, Copy copy)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(_written.Count <= MaxWritten)) return;
        if (Overlaps(copy.From, copy.FromOffset, copy.Size) || Overlaps(copy.To, copy.ToOffset, copy.Size) ||
            _written.Count == MaxWritten)
        {
            var memory = new Vk.GlobalBarrier
            {
                SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite,
                DstAccess = Vk.AccessTransferRead | Vk.AccessTransferWrite
            };
            VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
            _written.Clear();
        }

        var region = new Vk.BufferCopy { SourceOffset = copy.FromOffset, TargetOffset = copy.ToOffset, Size = copy.Size };
        VkApi.CmdCopyBuffer(commands, copy.From, copy.To, 1, &region);
        _written.Add((copy.To, copy.ToOffset, copy.Size));
    }

    private bool Overlaps(ulong buffer, ulong offset, ulong size)
    {
        _ = Assert(size > 0) && Assert(_written.Count <= MaxWritten);
        foreach (var (b, o, s) in _written.Bounded(MaxWritten))
            if (b == buffer && offset < o + s && o < offset + size)
                return true;
        return false;
    }

    public void Opened()
    {
        _written.Clear(); // the setup began with a barrier after everything before
        if (!Assert(_frame.Open) || _pending.Count == 0) return;
        foreach (var copy in _pending.Bounded(MaxPending)) Record(_frame.Setup, copy);
        _pending.Clear();
    }

    public void Ending()
    {
        if (_pending.Count > 0 && Assert(!_frame.Open) && _frame.Alone(commands =>
            {
                _written.Clear();
                foreach (var copy in _pending.Bounded(MaxPending)) Record(commands, copy);
            }))
            _pending.Clear();

        _heap.Collect(_frame.Done);
        _staging.Collect();
        _ = Assert(_pending.Count <= MaxPending);
    }

    public void Dispose()
    {
        _ = Assert(_mirrors.Count <= MaxMirrors) && _device.WaitIdle();
        _staging.Dispose();
        _mirrors.Clear();
        Array.Clear(_recent);
        _persistent.Clear();
        _wanted.Clear();
        _sizes.Clear();
        _pending.Clear();
        _heap.Dispose();
    }
}

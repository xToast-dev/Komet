using System.Runtime.InteropServices;
using Komet.Rendering;

namespace Komet.Vulkan;

// OpenGL semantics the passes rely on: Upload gives fresh contents (an upload-arena region) so earlier commands see the
// old ones; Zero is ordered before later dispatches; an unbound declared resource gets a placeholder. On a frame the
// passes record into the frame's open segment on its recording thread, sharing one command buffer with the draws.
// Deleting waits for the GPU (only at world end or a switch).
internal sealed unsafe partial class VulkanBackend : IGpuBackend, IDisposable
{
    private const int MaxHandles = 4096, Align = 256;

    private const ulong ArenaBytes = 64UL << 20,
        BufferUsage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageUniform |
                      Vk.UsageStorage | Vk.UsageIndirect;

    private const uint HostMemory = Vk.HostVisible | Vk.HostCoherent;
    private const int MaxOutside = 16;

    // Read by the CPU: device-local where the CPU sees it (resizable BAR), else system memory. The GPU's alone: device-local.
    private static readonly (uint Want, uint Avoid)[] HostTypes = [(HostMemory | Vk.DeviceLocal, 0), (HostMemory, 0)];
    private static readonly (uint Want, uint Avoid)[] DeviceTypes = [(Vk.DeviceLocal, 0)];
    private readonly BufferSlot?[] _buffers = new BufferSlot?[MaxHandles];

    private readonly VulkanDevice _device;
    private readonly VulkanFrame? _frame;
    private readonly IntPtr _handle;
    private readonly ImageSlot?[] _images = new ImageSlot?[MaxHandles];
    private readonly Dictionary<SharedImage, int> _outside = [];
    private readonly FrameStaging? _overflow;
    private readonly System.Func<int, SharedImage?>? _shared;
    private BufferSlot? _arena;
    private ulong _arenaUsed;
    private long _collected = -1;
    private IntPtr _commands;
    private bool _disposed;
    private int _placeholderImage, _placeholderBuffer;
    private bool _transferred; // copies or fills recorded since the last barrier

    // frame: recording into its segments; shared: the shared image of an engine texture, for Engine
    internal VulkanBackend(VulkanDevice device, VulkanFrame? frame, System.Func<int, SharedImage?>? shared)
    {
        (_device, _frame, _shared) = (device, frame, shared);
        _overflow = frame is null ? null : new FrameStaging(device, frame);
        _handle = NotNull(device) ? device.Handle : IntPtr.Zero;
        if (frame is null)
        {
            _arena = Allocate(ArenaBytes, true); // on a frame uploads go into the frame's own
        }

        _placeholderBuffer = Buffer();
        Upload<uint>(_placeholderBuffer, [], Align);
        if (frame is null)
        {
            _placeholderImage = Pyramid(1, 1, out _); // on a frame: at the first dispatch, in a segment
        }

        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, AddressU = 2, AddressV = 2, AddressW = 2, MaxLod = 16 // nearest, clamped
        };
        ulong sampler;
        _sampler = Assert(_handle != IntPtr.Zero) && VkApi.CreateSampler(_handle, &info, null, &sampler) == Vk.Success
            ? sampler
            : 0;
    }

    public void Dispose()
    {
        if (!Assert(_handle != IntPtr.Zero) || _disposed) return;

        Idle();
        _overflow?.Dispose();
        _disposed = true;
        for (var i = 1; i < MaxHandles; i++)
        {
            if (_buffers[i] is { } b) Destroy(b);

            if (_images[i] is { Shared: null } image) DestroyImage(image);

            if (_programs[i] is { } program) DestroyProgram(program);

            (_buffers[i], _images[i], _programs[i]) = (null, null, null);
        }

        if (_arena is { } arena) Destroy(arena);

        if (_sampler != 0) VkApi.DestroySampler(_handle, _sampler, null);

        (_arena, _sampler, _placeholderBuffer, _placeholderImage) = (null, 0, 0, 0);
        _outside.Clear();
    }

    // Work the frame had no segment or upload memory for: its result is missing (a pyramid level, a pass)
    public long Refused { get; private set; }

    public bool Supported() =>
        (_arena is not null || _frame is not null) && (_placeholderImage != 0 || _frame is not null) &&
               _sampler != 0 && !_disposed;

    public bool Doubles() => _device.Float64;

    public int Buffer()
    {
        var at = Free(_buffers);
        if (at > 0) _buffers[at] = new BufferSlot(0, 0, 0, null) { View = (0, 0, 0) };

        return at;
    }

    // Empty data: own storage of >= minBytes (UploadAt, Read). Data: fresh contents in the arena (frame uploads).
    public void Upload<T>(int buffer, ReadOnlySpan<T> data, int minBytes = 0) where T : unmanaged
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(minBytes >= 0)) return;

        var bytes = (ulong)Math.Max(data.Length * sizeof(T), Math.Max(minBytes, 16));
        if (data.IsEmpty)
        {
            if (b.Size < bytes) _buffers[buffer] = Own(b, bytes, b.Host);

            return;
        }

        if (_frame is not null) // into the frame's uploads, which the frame keeps until the GPU is done with its commands
        {
            if (!Assert(bytes <= int.MaxValue)) return;

            var uploads = _frame.Uploads;
            var staged = uploads?.Take((int)bytes, out _) ?? -1;
            if (staged < 0)
            {
                uploads = Staging((int)bytes, out staged, Align); // read where it lies, as the uploads are
            }

            if (uploads is null) return;

            MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(uploads.Pointer(staged), (int)bytes));
            b.View = (uploads.Buffer, (ulong)staged, bytes);
            return;
        }

        if (_arenaUsed + bytes + Align > ArenaBytes)
        {
            Flush(); // the arena is reused once its commands are done
        }

        var offset = (_arenaUsed + Align - 1) / Align * Align;
        _arenaUsed = offset + bytes;
        if (_arena is not { } arena || !Assert(_arenaUsed <= ArenaBytes)) return;

        fixed (T* source = data)
        {
            System.Buffer.MemoryCopy(source, arena.Mapped + offset, bytes, (ulong)(data.Length * sizeof(T)));
        }

        b.View = (arena.Handle, offset, bytes);
    }

    // On a frame through its staging into any own storage, else written where it is mapped
    public void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b ||
            (_frame is null ? b.Mapped == null : b.Handle == 0) ||
            !Assert(offsetBytes >= 0) || data.IsEmpty)
        {
            return;
        }

        var bytes = (ulong)(data.Length * sizeof(T));
        if (!Assert((ulong)offsetBytes + bytes <= b.Size)) return;

        if (_frame is not null) // the GPU may still read the frames before: the data goes in with the frame's commands
        {
            if (Staging((int)bytes, out var at) is not { } staging) return;

            MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(staging.Pointer(at), (int)bytes));
            Copied(staging.Buffer, b.Handle,
                new Vk.BufferCopy { SourceOffset = (ulong)at, TargetOffset = (ulong)offsetBytes, Size = bytes });
            b.View = (b.Handle, 0, b.Size);
            return;
        }

        fixed (T* source = data)
        {
            System.Buffer.MemoryCopy(source, b.Mapped + offsetBytes, b.Size - (ulong)offsetBytes, bytes);
        }

        b.View = (b.Handle, 0, b.Size);
    }

    // IGpuBackend.Staged: on a frame, a region of its staging copied into the buffer among its commands
    public byte* Staged(int buffer, int offsetBytes, int bytes)
    {
        if (_frame is null || !Index(buffer, MaxHandles) || _buffers[buffer] is not { } b ||
            !Assert(offsetBytes >= 0 && bytes > 0) || (ulong)offsetBytes + (ulong)bytes > b.Size ||
            Staging(bytes, out var at) is not { } staging)
        {
            return null;
        }

        Copied(staging.Buffer, b.Handle,
            new Vk.BufferCopy { SourceOffset = (ulong)at, TargetOffset = (ulong)offsetBytes, Size = (ulong)bytes });
        b.View = (b.Handle, 0, b.Size);
        return staging.Pointer(at);
    }

    // Engine texture: on a frame its shared image (0 without one); else this backend's own name (tests)
    public int Engine(int texture)
    {
        if (!Assert(texture >= 0) || _shared is null) return texture;

        if (texture == 0 || _shared(texture) is not { } image) return 0;

        if (_outside.TryGetValue(image, out var known) && _images[known]?.Shared == image) return known;

        if (_outside.Count >= MaxOutside) // the frame's framebuffers were made anew: the handles of the old ones go
        {
            foreach (var old in _outside.Values.Bounded(MaxOutside))
            {
                _images[old] = null;
                Unbound(old);
            }

            _outside.Clear();
        }

        var at = Free(_images);
        if (at == 0) return 0;

        _images[at] = new ImageSlot(image.Image, 0, image.Sampled, [image.View]) { Shared = image };
        _outside[image] = at;
        return Assert(image.Sampled != 0) ? at : 0;
    }

    public void Zero(int buffer, int firstUint, int uints)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0 && uints > 0)) return;

        var (target, at, size) = (b.View.Buffer, b.View.Offset + 4 * (ulong)firstUint, 4 * (ulong)uints);
        if (target == 0) return;

        if (_frame is not null) // a pooled op: no closure per fill, a few dozen a frame
        {
            var op = Taken();
            (op.Copy, op.Fill) = ((0, target, new Vk.BufferCopy { TargetOffset = at, Size = size }), true);
            _transferred = true;
            Record(op.Run);
            return;
        }

        Filled(target, at, size);
    }

    // On a frame nothing waits: reads are of what the GPU wrote frames ago, already fenced
    public void Read(int buffer, int firstUint, Span<uint> into)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0)) return;

        if (_frame is null) Flush();

        byte* source = null;
        if (b.View.Buffer == b.Handle)
        {
            source = b.Mapped;
        }
        else if (_arena is { } arena && b.View.Buffer == arena.Handle)
        {
            source = arena.Mapped + b.View.Offset;
        }

        if (source is null || !Assert(4 * (ulong)(firstUint + into.Length) <= b.View.Range)) return;

        new ReadOnlySpan<uint>(source + 4 * firstUint, into.Length).CopyTo(into);
    }

    // Host-visible and coherent storage: nothing to flush before Read
    public void Readable(int buffer, int bytes)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(bytes > 0)) return;

        if (b.Size < (ulong)bytes || !b.Host) _buffers[buffer] = Own(b, Math.Max((ulong)bytes, b.Size), true);
    }

    public void Copy(int from, int to, int firstUint, int uints)
    {
        if (!Index(from, MaxHandles) || !Index(to, MaxHandles) || _buffers[from] is not { } source ||
            _buffers[to] is not { } target || !Assert(firstUint >= 0 && uints > 0))
        {
            return;
        }

        if (source.View.Buffer == 0 || target.View.Buffer == 0) return;

        var at = 4 * (ulong)firstUint;
        Copied(source.View.Buffer, target.View.Buffer,
            new Vk.BufferCopy
            {
                SourceOffset = source.View.Offset + at, TargetOffset = target.View.Offset + at, Size = 4 * (ulong)uints
            });
    }

    public void DeleteBuffer(ref int buffer)
    {
        if (!Index(buffer, MaxHandles) || buffer == 0 || !Assert(buffer != _placeholderBuffer || _disposed)) return;

        Idle();
        if (_buffers[buffer] is { } b) Destroy(b);

        _buffers[buffer] = null;
        buffer = 0;
    }

    // On a frame: frame staging, past it an own buffer kept until GPU-done (FrameStaging); a rows region takes tens of MB
    private HostBuffer? Staging(int bytes, out long at, int align = 16)
    {
        at = -1;
        if (_overflow is null || _frame is null || !Assert(bytes > 0)) return null;

        if (_collected != _frame.Number)
        {
            _overflow.Collect();
            _collected = _frame.Number;
        }

        var host = _overflow.Take(bytes, out at, align);
        if (host is null)
        {
            Refused++; // no memory for a buffer of its own
        }

        return host;
    }

    internal (ulong Buffer, ulong Offset) Native(int buffer) =>
        Index(buffer, MaxHandles) && _buffers[buffer] is { } b && Assert(b.View.Buffer != 0)
            ? (b.View.Buffer, b.View.Offset)
            : (0, 0);

    // Its own method: a closure's locals are allocated where they are declared, on every call, taken or not
    private void Filled(ulong target, ulong at, ulong size)
    {
        Record(commands =>
        {
            VkApi.CmdFillBuffer(commands, target, at, size, 0);
            Ordered(commands); // ordered before what follows, as a GL buffer clear is
        });
    }

    // Records the copy; the next dispatch or barrier orders it before what follows, as GL orders a buffer copy. Copies
    // and fills between two dispatches write apart (the culling's uploads), so one barrier serves them all: a barrier
    // each drained the GPU some forty times a frame.
    private void Copied(ulong from, ulong to, Vk.BufferCopy region)
    {
        var op = Taken();
        (op.Copy, op.Fill) = ((from, to, region), false);
        _transferred = true;
        Record(op.Run);
    }

    // Waits for the GPU, which frees the arena; on a frame the frame submits instead
    public void Flush()
    {
        if (_frame is not null) return;

        if (_commands == IntPtr.Zero)
        {
            _arenaUsed = 0;
            return;
        }

        _commands = IntPtr.Zero;
        _ = Assert(_device.Submit([]));
        _arenaUsed = 0;
    }

    private void Record(Action<IntPtr> record)
    {
        if (!Assert(_handle != IntPtr.Zero) || _disposed) return;

        if (_frame is { } frame)
        {
            if (!frame.Compute(record))
            {
                Refused++; // an op left unrecorded is not reused: the collector takes it
            }

            return;
        }

        var commands = Recording();
        if (commands != IntPtr.Zero) record(commands);
    }

    // Before a delete: the GPU done with everything recorded (on a frame, the open segment submitted first)
    private void Idle()
    {
        if (_disposed || !Assert(_handle != IntPtr.Zero)) return;

        if (_frame is null)
        {
            Flush();
            return;
        }

        _frame.Close("Komet's GPU passes let go of a resource");
        _ = Assert(_device.WaitIdle());
    }

    private IntPtr Recording()
    {
        if (_commands == IntPtr.Zero) _commands = _device.Begin();

        return Assert(_commands != IntPtr.Zero) ? _commands : IntPtr.Zero;
    }

    private static int Free<T>(T?[] table) where T : class
    {
        for (var i = 1; i < Math.Min(table.Length, MaxHandles); i++)
        {
            if (table[i] is null) return i;
        }

        _ = Assert(false); // a table full of live handles
        return 0;
    }

    // Fresh storage, zeroed, holding the old one's contents; the old one goes once the GPU is done with it
    private BufferSlot Own(BufferSlot old, ulong bytes, bool host)
    {
        var fresh = Allocate(bytes, host || _frame is null);
        if (!NotNull(fresh)) return old;

        if (fresh.Mapped == null && !(_frame?.Fill(fresh.Handle, 0, bytes) ?? false)) Refused++;

        if (old.Handle == 0) return fresh;

        var mapped = old.Mapped != null && fresh.Mapped != null;
        if (!mapped) Copied(old.Handle, fresh.Handle, new Vk.BufferCopy { Size = old.Size });

        Idle(); // the old store may be in flight, or the copy out of it recorded
        if (mapped) System.Buffer.MemoryCopy(old.Mapped, fresh.Mapped, bytes, old.Size);

        Destroy(old);
        return fresh;
    }

    // host: mapped (Readable, and every buffer off a frame). The GPU's own storage is zeroed by the caller.
    private BufferSlot? Allocate(ulong bytes, bool host)
    {
        if (!Assert(_handle != IntPtr.Zero) ||
            _device.MakeBuffer(bytes, (uint)BufferUsage, host ? HostTypes : DeviceTypes, host,
                out var made) is not null)
        {
            return null;
        }

        var mapped = (byte*)made.Mapped;
        if (mapped != null) new Span<byte>(mapped, (int)Math.Min(bytes, int.MaxValue)).Clear();

        return new BufferSlot(made.Buffer, made.Memory, bytes, mapped) { View = (made.Buffer, 0, bytes) };
    }

    private void Destroy(BufferSlot buffer)
    {
        if (buffer.Handle != 0) VkApi.DestroyBuffer(_handle, buffer.Handle, null);

        if (buffer.Memory != 0)
        {
            VkApi.FreeMemory(_handle, buffer.Memory, null); // unmaps it
        }
    }

    // A buffer: its own storage (Handle, zero before the first Upload without data), and what descriptors use (View)
    private sealed class BufferSlot(ulong handle, ulong memory, ulong size, byte* mapped)
    {
        public ulong Handle { get; } = handle;
        public ulong Memory { get; } = memory;
        public ulong Size { get; } = size;
        public byte* Mapped { get; } = mapped;
        public bool Host => Mapped != null;
        public (ulong Buffer, ulong Offset, ulong Range) View { get; set; }
    }
}

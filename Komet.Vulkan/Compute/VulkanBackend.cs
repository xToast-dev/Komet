using Komet.Rendering;

namespace Komet.Vulkan;

// OpenGL semantics the passes rely on: Upload gives fresh contents (an upload-arena region) so earlier commands see the
// old ones; Zero is ordered before later dispatches; an unbound declared resource gets a placeholder. On a frame the
// passes record into the frame's open segment on its recording thread, sharing one command buffer with the draws.
// Deleting waits for the GPU (only at world end or a switch).
internal sealed unsafe partial class VulkanBackend : IGpuBackend, IDisposable
{
    private const int MaxHandles = 4096, Align = 256;
    private const ulong ArenaBytes = 64UL << 20, BufferUsage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageUniform |
                                                               Vk.UsageStorage | Vk.UsageIndirect;
    private const uint HostMemory = Vk.HostVisible | Vk.HostCoherent;
    private const int MaxOutside = 16;

    // Read by the CPU: device-local where the CPU sees it (resizable BAR), else system memory. The GPU's alone: device-local.
    private static readonly (uint Want, uint Avoid)[] HostTypes = [(HostMemory | Vk.DeviceLocal, 0), (HostMemory, 0)];
    private static readonly (uint Want, uint Avoid)[] DeviceTypes = [(Vk.DeviceLocal, 0)];

    private readonly VulkanDevice _device;
    private readonly VulkanFrame? _frame;
    private readonly System.Func<int, SharedImage?>? _shared;
    private readonly FrameStaging? _overflow;
    private readonly IntPtr _handle;
    private readonly BufferSlot?[] _buffers = new BufferSlot?[MaxHandles];
    private readonly ImageSlot?[] _images = new ImageSlot?[MaxHandles];
    private readonly Dictionary<SharedImage, int> _outside = [];
    private BufferSlot? _arena;
    private ulong _arenaUsed;
    private IntPtr _commands;
    private int _placeholderImage, _placeholderBuffer;
    private long _collected = -1;
    private bool _disposed;

    // frame: recording into its segments; shared: the shared image of an engine texture, for Engine
    internal VulkanBackend(VulkanDevice device, VulkanFrame? frame, System.Func<int, SharedImage?>? shared)
    {
        (_device, _frame, _shared) = (device, frame, shared);
        _overflow = frame is null ? null : new FrameStaging(device, frame);
        _handle = NotNull(device) ? device.Handle : IntPtr.Zero;
        if (frame is null) _arena = Allocate(ArenaBytes, host: true); // on a frame uploads go into the frame's own
        _placeholderBuffer = Buffer();
        Upload<uint>(_placeholderBuffer, [], Align);
        if (frame is null) _placeholderImage = Pyramid(1, 1, out _); // on a frame: at the first dispatch, in a segment
        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, AddressU = 2, AddressV = 2, AddressW = 2, MaxLod = 16 // nearest, clamped
        };
        ulong sampler;
        _sampler = Assert(_handle != IntPtr.Zero) && VkApi.CreateSampler(_handle, &info, null, &sampler) == Vk.Success
            ? sampler
            : 0;
    }

    // Work the frame had no segment or upload memory for: its result is missing (a pyramid level, a pass)
    public long Refused { get; private set; }

    public bool Supported() => (_arena is not null || _frame is not null) && (_placeholderImage != 0 || _frame is not null) &&
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
            var staged = uploads?.Take((int)bytes, out _, Align) ?? -1;
            if (staged < 0) uploads = Staging((int)bytes, out staged, Align); // read where it lies, as the uploads are
            if (uploads is null) return;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(uploads.Pointer(staged), (int)bytes));
            b.View = (uploads.Buffer, (ulong)staged, bytes);
            return;
        }

        if (_arenaUsed + bytes + Align > ArenaBytes) Flush(); // the arena is reused once its commands are done
        var offset = (_arenaUsed + Align - 1) / Align * Align;
        _arenaUsed = offset + bytes;
        if (_arena is not { } arena || !Assert(_arenaUsed <= ArenaBytes)) return;
        fixed (T* source = data)
            System.Buffer.MemoryCopy(source, arena.Mapped + offset, bytes, (ulong)(data.Length * sizeof(T)));
        b.View = (arena.Handle, offset, bytes);
    }

    // On a frame through its staging into any own storage, else written where it is mapped
    public void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || (_frame is null ? b.Mapped == null : b.Handle == 0) ||
            !Assert(offsetBytes >= 0) || data.IsEmpty) return;
        var bytes = (ulong)(data.Length * sizeof(T));
        if (!Assert((ulong)offsetBytes + bytes <= b.Size)) return;
        if (_frame is not null) // the GPU may still read the frames before: the data goes in with the frame's commands
        {
            if (Staging((int)bytes, out var at) is not { } staging) return;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(staging.Pointer(at), (int)bytes));
            Copied(staging.Buffer, b.Handle,
                new Vk.BufferCopy { SourceOffset = (ulong)at, TargetOffset = (ulong)offsetBytes, Size = bytes });
            b.View = (b.Handle, 0, b.Size);
            return;
        }

        fixed (T* source = data)
            System.Buffer.MemoryCopy(source, b.Mapped + offsetBytes, b.Size - (ulong)offsetBytes, bytes);
        b.View = (b.Handle, 0, b.Size);
    }

    // IGpuBackend.Staged: on a frame, a region of its staging copied into the buffer among its commands
    public byte* Staged(int buffer, int offsetBytes, int bytes)
    {
        if (_frame is null || !Index(buffer, MaxHandles) || _buffers[buffer] is not { } b ||
            !Assert(offsetBytes >= 0 && bytes > 0) || (ulong)offsetBytes + (ulong)bytes > b.Size ||
            Staging(bytes, out var at) is not { } staging) return null;

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
        if (host is null) Refused++; // no memory for a buffer of its own
        return host;
    }

    internal (ulong Buffer, ulong Offset) Native(int buffer) =>
        Index(buffer, MaxHandles) && _buffers[buffer] is { } b && Assert(b.View.Buffer != 0)
            ? (b.View.Buffer, b.View.Offset)
            : (0, 0);

    public void Zero(int buffer, int firstUint, int uints)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0 && uints > 0)) return;
        var (target, at, size) = (b.View.Buffer, b.View.Offset + 4 * (ulong)firstUint, 4 * (ulong)uints);
        if (target == 0) return;
        if (_frame is { } frame) // an op of its own: no closure per fill, a few dozen a frame
        {
            if (!frame.Fill(target, at, size)) Refused++;
            return;
        }

        Filled(target, at, size);
    }

    // Its own method: a closure's locals are allocated where they are declared, on every call, taken or not
    private void Filled(ulong target, ulong at, ulong size)
    {
        Record(commands =>
        {
            VkApi.CmdFillBuffer(commands, target, at, size, 0);
            Ordered(commands); // ordered before what follows, as a GL buffer clear is
        });
    }

    // On a frame nothing waits: reads are of what the GPU wrote frames ago, already fenced
    public void Read(int buffer, int firstUint, Span<uint> into)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0)) return;
        if (_frame is null) Flush();
        byte* source = null;
        if (b.View.Buffer == b.Handle) source = b.Mapped;
        else if (_arena is { } arena && b.View.Buffer == arena.Handle) source = arena.Mapped + b.View.Offset;
        if (source is null || !Assert(4 * (ulong)(firstUint + into.Length) <= b.View.Range)) return;
        new ReadOnlySpan<uint>(source + 4 * firstUint, into.Length).CopyTo(into);
    }

    // Host-visible and coherent storage: nothing to flush before Read
    public void Readable(int buffer, int bytes)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(bytes > 0)) return;
        if (b.Size < (ulong)bytes || !b.Host) _buffers[buffer] = Own(b, Math.Max((ulong)bytes, b.Size), host: true);
    }

    public void Copy(int from, int to, int firstUint, int uints)
    {
        if (!Index(from, MaxHandles) || !Index(to, MaxHandles) || _buffers[from] is not { } source ||
            _buffers[to] is not { } target || !Assert(firstUint >= 0 && uints > 0)) return;
        if (source.View.Buffer == 0 || target.View.Buffer == 0) return;
        var at = 4 * (ulong)firstUint;
        Copied(source.View.Buffer, target.View.Buffer,
            new Vk.BufferCopy
            {
                SourceOffset = source.View.Offset + at, TargetOffset = target.View.Offset + at, Size = 4 * (ulong)uints
            });
    }

    // Records the copy, ordered before what follows as a GL buffer copy is
    private void Copied(ulong from, ulong to, Vk.BufferCopy region)
    {
        var op = Taken();
        op.Copy = (from, to, region);
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
            if (!frame.Compute(record)) Refused++; // an op left unrecorded is not reused: the collector takes it
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
            if (table[i] is null)
                return i;
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
            _device.MakeBuffer(bytes, (uint)BufferUsage, host ? HostTypes : DeviceTypes, host, out var made) is not null) return null;
        var mapped = (byte*)made.Mapped;
        if (mapped != null) new Span<byte>(mapped, (int)Math.Min(bytes, int.MaxValue)).Clear();
        return new BufferSlot(made.Buffer, made.Memory, bytes, mapped) { View = (made.Buffer, 0, bytes) };
    }

    private void Destroy(BufferSlot buffer)
    {
        if (buffer.Handle != 0) VkApi.DestroyBuffer(_handle, buffer.Handle, null);
        if (buffer.Memory != 0) VkApi.FreeMemory(_handle, buffer.Memory, null); // unmaps it
    }

    public void DeleteBuffer(ref int buffer)
    {
        if (!Index(buffer, MaxHandles) || buffer == 0 || !Assert(buffer != _placeholderBuffer || _disposed)) return;
        Idle();
        if (_buffers[buffer] is { } b) Destroy(b);
        _buffers[buffer] = null;
        buffer = 0;
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

// Images are kept in the general layout from their creation on
internal sealed unsafe partial class VulkanBackend
{
    private const uint ImageUsage = Vk.Storage | Vk.Sampled | Vk.TransferDst | Vk.TransferSrc;

    public int Pyramid(int width, int height, out int levels)
    {
        levels = 0;
        if (!Assert(width > 0 && height > 0) || !Assert(_handle != IntPtr.Zero)) return 0;
        levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        var at = Free(_images);
        if (at == 0 || Create(width, height, levels) is not { } image) return 0;
        _images[at] = image;
        return at;
    }

    // A one-level image of the given size filled with value: depth for tests and callers without a GL depth buffer
    internal int Filled(int width, int height, float value)
    {
        if (!Assert(width > 0 && height > 0) || !Finite(value)) return 0;
        var at = Free(_images);
        if (at == 0 || Create(width, height, 1) is not { } image) return 0;
        _images[at] = image;
        var staging = Buffer();
        var pixels = new float[width * height];
        Array.Fill(pixels, value);
        Upload<float>(staging, pixels);
        var commands = Recording();
        var copy = new Vk.BufferImageCopy
        {
            Offset = _buffers[staging]!.View.Offset, Aspect = Vk.AspectColor, Layers = 1, Width = (uint)width,
            Height = (uint)height, Depth = 1
        };
        VkApi.CmdCopyBufferToImage(commands, _buffers[staging]!.View.Buffer, image.Handle, Vk.LayoutGeneral, 1, &copy);
        Barrier(GpuBarrier.Textures);
        Flush();
        DeleteBuffer(ref staging);
        return at;
    }

    private ImageSlot? Create(int width, int height, int levels)
    {
        if (!Assert(levels is > 0 and <= 16)) return null;
        var info = new Vk.ImageInfo
        {
            SType = Vk.ImageCreateInfo, ImageType = 1, Format = Vk.FormatR32F, Width = (uint)width,
            Height = (uint)height, Depth = 1, MipLevels = (uint)levels, ArrayLayers = 1, Samples = 1,
            Usage = ImageUsage,
            InitialLayout = Vk.LayoutUndefined
        };
        ulong handle, memory = 0;
        if (VkApi.CreateImage(_handle, &info, null, &handle) != Vk.Success) return null;
        Vk.MemoryRequirements needs;
        VkApi.GetImageMemoryRequirements(_handle, handle, &needs);
        var type = _device.MemoryType(needs.TypeBits, Vk.DeviceLocal);
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        if (type < 0 || VkApi.AllocateMemory(_handle, &allocate, null, &memory) != Vk.Success ||
            VkApi.BindImageMemory(_handle, handle, memory, 0) != Vk.Success)
        {
            VkApi.DestroyImage(_handle, handle, null);
            VkApi.FreeMemory(_handle, memory, null); // 0 when the allocation failed
            return null;
        }

        var image = new ImageSlot(handle, memory, View(handle, 0, levels), new ulong[levels]);
        for (var level = 0; level < Math.Min(levels, 16); level++) image.Levels[level] = View(handle, level, 1);
        General(handle, levels);
        return image;
    }

    private ulong View(ulong image, int level, int count)
    {
        var info = new Vk.ViewInfo
        {
            SType = Vk.ImageViewCreateInfo, Image = image, ViewType = Vk.ViewType2D, Format = Vk.FormatR32F,
            Range = new Vk.ColorRange
            {
                Aspect = Vk.AspectColor, BaseLevel = (uint)level, Levels = (uint)count, Layers = 1
            }
        };
        ulong view;
        return VkApi.CreateImageView(_handle, &info, null, &view) == Vk.Success ? view : 0;
    }

    private void General(ulong image, int levels)
    {
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier,
            DstAccess = Vk.AccessShaderRead | Vk.AccessShaderWrite | Vk.AccessTransferWrite,
            OldLayout = Vk.LayoutUndefined, NewLayout = Vk.LayoutGeneral, SrcFamily = Vk.QueueFamilyIgnored,
            DstFamily = Vk.QueueFamilyIgnored, Image = image,
            Range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = (uint)levels, Layers = 1 }
        };
        Record(commands =>
        {
            var moved = barrier;
            VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.PipelineCompute | Vk.StageTransfer, 0, 0, null, 0, null, 1,
                &moved);
        });
    }

    public void DeleteTexture(ref int texture)
    {
        if (!Index(texture, MaxHandles) || texture == 0 || !Assert(texture != _placeholderImage || _disposed)) return;
        Idle();
        if (_images[texture] is { Shared: null } image) DestroyImage(image);
        _images[texture] = null;
        Unbound(texture);
        texture = 0;
    }

    private void DestroyImage(ImageSlot image)
    {
        foreach (var view in image.Levels.Bounded(16)) VkApi.DestroyImageView(_handle, view, null);
        VkApi.DestroyImageView(_handle, image.All, null);
        VkApi.DestroyImage(_handle, image.Handle, null);
        VkApi.FreeMemory(_handle, image.Memory, null);
    }

    // Shared: an engine's image the frame owns (Engine), not this backend's to destroy
    private sealed record ImageSlot(ulong Handle, ulong Memory, ulong All, ulong[] Levels)
    {
        public SharedImage? Shared { get; init; }
    }
}

// Each dispatch takes every resource from the per-kind binding tables (OpenGL's namespaces), or the placeholder. A frame
// records dispatches and buffer copies later on its thread without closures (Recorded): pooled, dropped ones go to the GC.
internal sealed unsafe partial class VulkanBackend
{
    private const int MaxUnits = 64, MaxResources = 16, MaxIdle = 512;

    private readonly ProgramSlot?[] _programs = new ProgramSlot?[MaxHandles];
    private readonly int[] _storage = new int[MaxUnits], _uniforms = new int[MaxUnits], _textures = new int[MaxUnits];
    private readonly (int Image, int Level)[] _imageUnits = new (int, int)[MaxUnits];
    private readonly Core.BoundedPool<Recorded> _idle = new(MaxIdle); // the frame's recording thread gives them back
    private ulong _sampler;

    public int Program(string shader, ILogger logger)
    {
        if (!NotNull(logger) || !Assert(shader.Length > 0) || GpuShaders.Source(shader) is not { } source) return 0;
        var words = Shaderc.Compile(source, Shaderc.Compute, shader, out var error);
        if (words is null)
        {
            logger.Warning("Komet: {0} does not compile for Vulkan: {1}", shader, error);
            return 0;
        }

        var at = Free(_programs);
        if (at == 0 || Pipeline(words, Spirv.Resources(words)) is not { } program) return 0;
        _programs[at] = program;
        return at;
    }

    private ProgramSlot? Pipeline(uint[] words, Spirv.Resource[] resources)
    {
        if (!Assert(resources.Length <= MaxResources) || !Assert(words.Length > 5)) return null;
        var bindings = stackalloc Vk.LayoutBinding[MaxResources];
        for (var i = 0; i < Math.Min(resources.Length, MaxResources); i++)
            bindings[i] = new Vk.LayoutBinding
            {
                Binding = (uint)resources[i].Binding, Type = resources[i].Type, Count = 1, Stages = Vk.StageCompute
            };
        ulong module, set = 0, layout = 0, pipeline = 0;
        fixed (uint* code = words)
        {
            var info = new Vk.ModuleInfo
            {
                SType = Vk.ShaderModuleCreateInfo, CodeSize = (nuint)(4 * words.Length), Code = code
            };
            if (VkApi.CreateShaderModule(_handle, &info, null, &module) != Vk.Success) return null;
        }

        var setInfo = new Vk.SetLayoutInfo
        {
            SType = Vk.SetLayoutCreateInfo, Flags = Vk.PushDescriptorLayout, BindingCount = (uint)resources.Length,
            Bindings = bindings
        };
        var layoutInfo = new Vk.PipelineLayoutInfo
        {
            SType = Vk.PipelineLayoutCreateInfo, SetLayoutCount = 1, SetLayouts = &set
        };
        var made = VkApi.CreateSetLayout(_handle, &setInfo, null, &set) == Vk.Success &&
                   VkApi.CreatePipelineLayout(_handle, &layoutInfo, null, &layout) == Vk.Success;
        var main = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var compute = new Vk.ComputeInfo
        {
            SType = Vk.ComputePipelineCreateInfo, Layout = layout,
            Stage = new Vk.StageInfo
            {
                SType = Vk.StageCreateInfo, Stage = Vk.StageCompute, Module = module, Name = main
            }
        };
        made = made &&
               VkApi.CreateComputePipelines(_handle, _device.PipelineCache, 1, &compute, null, &pipeline) == Vk.Success;
        VkApi.DestroyShaderModule(_handle, module, null);
        var program = new ProgramSlot(pipeline, layout, set, resources);
        if (made) return program;
        DestroyProgram(program);
        return null;
    }

    public void Storage(int binding, int buffer) => Bind(_storage, binding, buffer);

    public void Uniforms(int binding, int buffer) => Bind(_uniforms, binding, buffer);

    public void Texture(int unit, int texture) => Bind(_textures, unit, texture);

    public void Image(int unit, int texture, int level, bool write)
    {
        if (!Index(unit, MaxUnits) || !Assert(level >= 0) || !Index(texture, MaxHandles)) return;
        _imageUnits[unit] = (texture, level);
    }

    // As GL unbinds a deleted texture: Free hands the number to the next image, which an old binding would then sample
    private void Unbound(int image)
    {
        for (var i = 0; i < MaxUnits; i++)
        {
            if (_textures[i] == image) _textures[i] = 0;
            if (_imageUnits[i].Image == image) _imageUnits[i] = (0, 0);
        }
    }

    private static void Bind(int[] table, int at, int handle)
    {
        if (!Index(at, MaxUnits) || !Index(handle, MaxHandles)) return;
        table[at] = handle;
    }

    // Descriptors from the binding tables as they stand now; recorded now, or on a frame posted to its thread
    public void Dispatch(int program, int x, int y = 1)
    {
        if (!Index(program, MaxHandles) || _programs[program] is not { } p || !Assert(x >= 0 && y >= 0) ||
            x * y == 0) return;
        if (_placeholderImage == 0 && _frame is not null) _placeholderImage = Pyramid(1, 1, out _);
        if (!Assert(_placeholderImage != 0)) return;
        VulkanRenderer.Mark(VulkanRenderer.Drawn.Compute);
        var op = Taken();
        (op.Count, op.Groups) = (Math.Min(p.Resources.Length, MaxResources), (x, y));
        for (var i = 0; i < Math.Min(op.Count, MaxResources); i++)
        {
            var r = p.Resources[i];
            (op.Buffers[i], op.Images[i]) = (default, default);
            if (r.Type is Spirv.StorageBuffer or Spirv.UniformBuffer) op.Buffers[i] = Described(r);
            else if (Viewed(r) is { } image) op.Images[i] = image;
            else
            {
                Recycled(op); // an engine image the frame could not make readable: nothing is recorded
                return;
            }
        }

        op.Program = p;
        Record(op.Run);
    }

    private static void Dispatched(IntPtr commands, ProgramSlot p, Recorded described)
    {
        var (count, groups) = (Math.Min(described.Count, MaxResources), described.Groups);
        var writes = stackalloc Vk.WriteInfo[MaxResources];
        fixed (Vk.BufferDescriptor* buffers = described.Buffers)
        fixed (Vk.ImageDescriptor* images = described.Images)
        {
            for (var i = 0; i < Math.Min(count, MaxResources); i++)
                writes[i] = new Vk.WriteInfo
                {
                    SType = Vk.WriteDescriptorSet, Binding = (uint)p.Resources[i].Binding, Count = 1,
                    Type = p.Resources[i].Type, Buffers = buffers + i, Images = images + i
                };
            VkApi.CmdBindPipeline(commands, Vk.BindCompute, p.Pipeline);
            VkApi.CmdPushDescriptors(commands, Vk.BindCompute, p.Layout, 0, (uint)count, writes);
        }

        VkApi.CmdDispatch(commands, (uint)groups.X, (uint)groups.Y, 1);
    }

    private Vk.BufferDescriptor Described(Spirv.Resource r)
    {
        var table = r.Type == Spirv.StorageBuffer ? _storage : _uniforms;
        var handle = Index(r.Binding, MaxUnits) && table[r.Binding] != 0 ? table[r.Binding] : _placeholderBuffer;
        var view = _buffers[handle] is { } b && b.View.Buffer != 0 ? b.View : _buffers[_placeholderBuffer]!.View;
        return Assert(view.Buffer != 0)
            ? new Vk.BufferDescriptor { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range }
            : default;
    }

    // An engine image (Engine) is sampled in the layout the frame moved it into; null when the frame could not
    private Vk.ImageDescriptor? Viewed(Spirv.Resource r)
    {
        var (storage, unit) = (r.Type == Spirv.StorageImage, Math.Clamp(r.Binding, 0, MaxUnits - 1));
        var (handle, level) = storage ? _imageUnits[unit] : (_textures[unit], 0);
        if (!Index(handle, MaxHandles) || _images[handle] is null) handle = _placeholderImage;
        var image = _images[handle]!;
        var (view, sampler) = storage
            ? (image.Levels[Math.Clamp(level, 0, image.Levels.Length - 1)], 0UL)
            : (image.All, _sampler);
        var layout = Vk.LayoutGeneral;
        if (image.Shared is { } shared) // the frame moves it to be sampled, unless its open segment does not hold it
        {
            layout = shared.Format.Aspect == Vk.AspectDepth ? Vk.LayoutDepthReadOnly : Vk.LayoutShaderRead;
            if (_frame is not { } frame || !frame.Sample(shared, layout))
            {
                Refused++;
                return null;
            }
        }

        return Assert(view != 0)
            ? new Vk.ImageDescriptor { Sampler = sampler, View = view, Layout = layout }
            : default;
    }

    // Every write before, by shaders or copies, is seen by every read or write after, including indirect commands
    public void Barrier(GpuBarrier wait)
    {
        if (Assert(wait != GpuBarrier.None)) Record(Ordered);
    }

    internal static void Ordered(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return;
        var barrier = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessShaderWrite | Vk.AccessTransferWrite,
            DstAccess = Vk.AccessShaderRead | Vk.AccessShaderWrite | Vk.AccessIndirect | Vk.AccessTransferRead |
                        Vk.AccessTransferWrite
        };
        VkApi.CmdPipelineBarrier(commands, Vk.PipelineCompute | Vk.StageTransfer,
            Vk.PipelineCompute | Vk.StageTransfer | Vk.PipelineIndirect, 0, 1, &barrier, 0, null, 0, null);
    }

    private void DestroyProgram(ProgramSlot program)
    {
        if (program.Pipeline != 0) VkApi.DestroyPipeline(_handle, program.Pipeline, null);
        if (program.Layout != 0) VkApi.DestroyPipelineLayout(_handle, program.Layout, null);
        if (program.Set != 0) VkApi.DestroySetLayout(_handle, program.Set, null);
    }

    public void DeleteProgram(ref int program)
    {
        if (!Index(program, MaxHandles) || program == 0 || !Assert(_programs[program] is not null || _disposed)) return;
        Idle();
        if (_programs[program] is { } p) DestroyProgram(p);
        _programs[program] = null;
        program = 0;
    }

    private Recorded Taken() => _idle.Take() is { Program: null } op ? op : new Recorded(this);

    private void Recycled(Recorded op) => _idle.Give(op);

    private sealed class Recorded
    {
        public readonly Vk.BufferDescriptor[] Buffers = new Vk.BufferDescriptor[MaxResources];
        public readonly Vk.ImageDescriptor[] Images = new Vk.ImageDescriptor[MaxResources];
        public readonly Action<IntPtr> Run;
        private readonly VulkanBackend _owner;
        public ProgramSlot? Program; // a dispatch; null: the copy
        public int Count;
        public (int X, int Y) Groups;
        public (ulong From, ulong To, Vk.BufferCopy Region) Copy;

        public Recorded(VulkanBackend owner)
        {
            (_owner, Run) = (owner, Execute);
        }

        private void Execute(IntPtr commands)
        {
            if (Program is { } p) Dispatched(commands, p, this);
            else CopyRecorded(commands, Copy);
            Program = null;
            _owner.Recycled(this);
        }
    }

    private static void CopyRecorded(IntPtr commands, (ulong From, ulong To, Vk.BufferCopy Region) copy)
    {
        var region = copy.Region;
        VkApi.CmdCopyBuffer(commands, copy.From, copy.To, 1, &region);
        Ordered(commands);
    }

    private sealed record ProgramSlot(ulong Pipeline, ulong Layout, ulong Set, Spirv.Resource[] Resources);
}

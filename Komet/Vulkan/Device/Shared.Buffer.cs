using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// A SharedSemaphore orders the two APIs' use of it, naming the buffer
internal sealed unsafe class SharedBuffer : IDisposable
{
    private const uint Usage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageStorage | Vk.UsageVertex | Vk.UsageIndex |
                               Vk.UsageIndirect;

    private readonly IntPtr _device;
    private ulong _buffer, _memory;
    private uint _glMemory;
    private SharedArena? _arena;
    private bool _mapped;
    private (SharedArena.Block Block, ulong At, ulong Size)? _range;

    private SharedBuffer(IntPtr device, ulong size)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(size > 0);
        (_device, Size) = (device, size);
    }

    public ulong Buffer => _buffer;
    public int Gl { get; private set; }
    public ulong Size { get; }

    public nint Pointer { get; private set; }

    // Where in the shared arena, when carved from it
    public (SharedArena.Block Block, ulong At)? Carved => _range is { } range ? (range.Block, range.At) : null;

    // dedicated: an allocation of its own even where a range of the arena would do (one that goes again soon, for which the
    // arena must not make a block that stays until its frame is done)
    public static SharedBuffer? Create(VulkanDevice device, ulong size, out string why, bool mapped = false,
        bool dedicated = false)
    {
        why = "";
        if (!NotNull(device) || !Assert(size is > 0 and <= int.MaxValue)) return null;
        var shared = new SharedBuffer(device.Handle, size) { _mapped = mapped };
        why = size <= SharedArena.BlockBytes / 4 && !dedicated
            ? shared.Carve(device) ?? ""
            : shared.Allocate(device) ?? shared.Import() ?? "";
        if (why.Length == 0 && mapped && shared.Pointer == 0) why = "no memory the CPU can write for the buffer";
        if (why.Length == 0) return shared;
        shared.Dispose();
        return null;
    }

    private string? Created(VulkanDevice device, out Vk.MemoryRequirements needs, out int type)
    {
        (needs, type) = (default, -1);
        if (!Assert(_buffer == 0) || !NotNull(device)) return "allocated already";
        var external = new Vk.Chained { SType = Vk.ExternalMemoryBufferCreateInfo, HandleTypes = Vk.OpaqueFd };
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Next = &external, Size = Size, Usage = Usage };
        ulong buffer;
        if (VkApi.CreateBuffer(_device, &info, null, &buffer) != Vk.Success) return "vkCreateBuffer failed";
        _buffer = buffer;
        Vk.MemoryRequirements requirements;
        VkApi.GetBufferRequirements(_device, buffer, &requirements);
        var bits = requirements.TypeBits;
        type = device.MemoryType(bits, _mapped ? Vk.DeviceLocal | Vk.HostVisible | Vk.HostCoherent : Vk.DeviceLocal);
        if (type < 0 && _mapped) type = device.MemoryType(bits, Vk.HostVisible | Vk.HostCoherent);
        needs = requirements;
        return Assert(bits != 0) && type >= 0 ? null : "no device-local memory for the buffer";
    }

    private string? Allocate(VulkanDevice device)
    {
        if (Created(device, out var needs, out var type) is { } failed) return failed;
        var dedicated = new Vk.DedicatedInfo { SType = Vk.MemoryDedicatedAllocateInfo, Buffer = _buffer };
        var export = new Vk.Chained
        {
            SType = Vk.ExportMemoryAllocateInfo, Next = &dedicated, HandleTypes = Vk.OpaqueFd
        };
        var allocate = new Vk.AllocateInfo
        {
            SType = Vk.MemoryAllocateInfo, Next = &export, Size = needs.Size, TypeIndex = (uint)type
        };
        ulong memory;
        if (VkApi.AllocateMemory(_device, &allocate, null, &memory) != Vk.Success) return "vkAllocateMemory failed";
        (_memory, Allocated) = (memory, needs.Size);
        if (VkApi.BindBufferMemory(_device, _buffer, memory, 0) != Vk.Success) return "vkBindBufferMemory failed";
        void* mapped = null;
        if (_mapped && VkApi.MapMemory(_device, memory, 0, needs.Size, 0, &mapped) == Vk.Success) Pointer = (nint)mapped;
        return null;
    }

    private ulong Allocated { get; set; }

    private string? Carve(VulkanDevice device)
    {
        if (Created(device, out var needs, out var type) is { } failed) return failed;
        var arena = device.Arena;
        if (arena.Take(needs.Size, Math.Max(needs.Alignment, 256), (uint)type, Serving(device, needs.TypeBits)) is not
            { } range)
            return "no arena memory for the buffer";
        (_arena, _range) = (arena, (range.Block, range.At, needs.Size));
        if (_mapped && arena.Map(range.Block) is var block and not 0) Pointer = block + (nint)range.At;
        if (VkApi.BindBufferMemory(_device, _buffer, range.Block.Memory, range.At) != Vk.Success)
            return "vkBindBufferMemory failed";
        Gl = GlInterop.Buffer(range.Block.GlMemory, range.At, Size);
        return Gl != 0 ? null : "OpenGL did not take the buffer's memory";
    }

    // The types of the buffer's whose memory has every property the buffer asked for
    private uint Serving(VulkanDevice device, uint bits)
    {
        var wanted = _mapped ? Vk.DeviceLocal | Vk.HostVisible | Vk.HostCoherent : Vk.DeviceLocal;
        var serving = 0u;
        for (var i = 0; i < 32; i++)
            if (device.MemoryType(bits & (1u << i), wanted) == i)
                serving |= 1u << i;
        return Assert(bits != 0) && Assert((serving & ~bits) == 0) ? serving : 0;
    }

    private string? Import()
    {
        if (!Assert(_memory != 0) || !Assert(Allocated >= Size)) return "no memory";
        var get = new Vk.GetFdInfo { SType = Vk.MemoryGetFdInfo, Handle = _memory, HandleType = Vk.OpaqueFd };
        int fd;
        if (VkApi.GetMemoryFd(_device, &get, &fd) != Vk.Success || fd < 0) return "vkGetMemoryFdKHR failed";
        (Gl, _glMemory) = GlInterop.Buffer(fd, Allocated, Size);
        return Gl != 0 ? null : "OpenGL did not take the buffer's memory";
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Gl >= 0)) return;
        if (Gl != 0) GL.DeleteBuffer(Gl);
        GlInterop.Delete(_glMemory, 0);
        if (_buffer != 0) VkApi.DestroyBuffer(_device, _buffer, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        if (_range is { } range) _arena?.Give(range.Block, range.At, range.Size);
        (Gl, _glMemory, _buffer, _memory, _range, Pointer) = (0, 0, 0, 0, null, 0);
    }
}

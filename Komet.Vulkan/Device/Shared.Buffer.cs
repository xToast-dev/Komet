namespace Komet.Vulkan;

// Vulkan's own buffer standing for an engine buffer (Gl names it; OpenGL keeps its own), in memory of its own or a
// buffer and mapping someone else made and frees (Wrapping). A file of its own: VkMemory's report names an allocation's
// owner by the file that made it, so the pools' memory shows as Shared.Buffer.
internal sealed unsafe class SharedBuffer : IDisposable
{
    private const uint Usage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageStorage | Vk.UsageVertex | Vk.UsageIndex |
                               Vk.UsageIndirect;
    private const uint Host = Vk.HostVisible | Vk.HostCoherent;

    private static readonly (uint Want, uint Avoid)[] Device = [(Vk.DeviceLocal, 0)];
    private static readonly (uint Want, uint Avoid)[] Written = [(Vk.DeviceLocal | Host, 0), (Host, 0)];

    private readonly IntPtr _device;
    private ulong _buffer, _memory;
    private bool _mapped, _wrapped;

    private SharedBuffer(IntPtr device, ulong size)
    {
        (_device, Size) = (device, size);
    }

    public ulong Buffer => _buffer;
    public int Gl { get; private set; }

    public ulong Size { get; }

    public nint Pointer { get; private set; }

    // Engine buffer gl's contents in Vulkan's memory (mapped: CPU writes too); GL keeps its own buffer
    public static SharedBuffer? Private(VulkanDevice device, ulong size, int gl, bool mapped, out string why)
    {
        why = "";
        if (!NotNull(device) || !Assert(size is > 0 and <= int.MaxValue) || !Assert(gl > 0)) return null;
        return Owned.Kept(new SharedBuffer(device.Handle, size) { _mapped = mapped, Gl = gl }, device,
            static (made, device) => made.Allocate(device), out why);
    }

    // A buffer and mapping someone else made and frees, standing for the engine buffer gl
    public static SharedBuffer Wrapping(VulkanDevice device, (ulong Buffer, nint Pointer, long Size) made, int gl)
    {
        _ = Assert(made.Buffer != 0) && Assert(made.Size > 0);
        return new SharedBuffer(device.Handle, (ulong)made.Size)
        {
            _buffer = made.Buffer, Pointer = made.Pointer, Gl = gl, _wrapped = true, _mapped = true
        };
    }

    // Mapped: the CPU writes it, in device memory where the CPU sees it (resizable BAR)
    private string? Allocate(VulkanDevice device)
    {
        if (device.MakeBuffer(Size, Usage, _mapped ? Written : Device, _mapped, out var made) is { } failed) return failed;
        (_buffer, _memory, Pointer) = (made.Buffer, made.Memory, made.Mapped);
        return Assert(!_mapped || Pointer != 0) ? null : "no memory the CPU can write for the buffer";
    }

    // The engine's buffer stays: OpenGL deletes it
    public void Dispose()
    {
        if (_buffer != 0 && !_wrapped) VkApi.DestroyBuffer(_device, _buffer, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (Gl, _buffer, _memory, Pointer) = (0, 0, 0, 0);
    }
}

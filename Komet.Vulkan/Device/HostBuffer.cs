namespace Komet.Vulkan;

// Mapped for life, device-local where CPU-visible (resizable BAR). A bulk buffer is only a copy source: cached system
// memory, so CPU writes (and GL readbacks) run at memory speed, not across the bus.
internal sealed unsafe class HostBuffer : IDisposable
{
    private const uint Usage = Vk.UsageUniform | Vk.UsageStorage | Vk.UsageIndirect | Vk.UsageIndex | Vk.UsageVertex |
                               Vk.TransferSrc | Vk.TransferDst;
    private const uint Host = Vk.HostVisible | Vk.HostCoherent;

    private static readonly (uint Want, uint Avoid)[] Near = [(Host | Vk.DeviceLocal, 0), (Host, 0)];
    private static readonly (uint Want, uint Avoid)[] Bulk = [(Host | Vk.HostCached, Vk.DeviceLocal), (Host, Vk.DeviceLocal), (Host, 0)];

    private readonly IntPtr _device;
    private ulong _buffer, _memory, _used;
    private byte* _mapped;

    private HostBuffer(IntPtr device, ulong size)
    {
        (_device, Size) = (device, size);
    }

    public ulong Buffer => _buffer;
    public ulong Size { get; }
    public long Epoch { get; private set; } // Resets so far: an offset taken in one epoch means nothing in the next

    public static HostBuffer? Create(VulkanDevice device, ulong size, bool bulk = false)
    {
        if (!NotNull(device) || !Assert(size is > 0 and <= 1UL << 31)) return null;
        return Owned.Kept(new HostBuffer(device.Handle, size), (device, bulk),
            static (host, s) => host.Allocate(s.device, s.bulk) ? null : "no memory", out _);
    }

    private bool Allocate(VulkanDevice device, bool bulk)
    {
        if (device.MakeBuffer(Size, Usage, bulk ? Bulk : Near, true, out var made) is not null) return false;
        (_buffer, _memory) = (made.Buffer, made.Memory);
        _mapped = (byte*)made.Mapped;
        return Assert(_mapped != null);
    }

    public long Take(int bytes, out Span<byte> into, int align = 256)
    {
        into = [];
        if (!Assert(bytes > 0 && align is > 0 and <= 4096) || !Assert(_mapped != null)) return -1;
        var at = (_used + (ulong)align - 1) / (ulong)align * (ulong)align;
        if (at + (ulong)bytes > Size) return -1;
        _used = at + (ulong)bytes;
        into = new Span<byte>(_mapped + at, bytes);
        return (long)at;
    }

    public byte* Pointer(long at) => Assert(at >= 0 && (ulong)at < Size) && Assert(_mapped != null) ? _mapped + at : null;

    public void Reset()
    {
        if (!Assert(_buffer != 0)) return;
        _used = 0;
        Epoch++;
    }

    public void Dispose()
    {
        if (_buffer != 0) VkApi.DestroyBuffer(_device, _buffer, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (_buffer, _memory) = (0, 0);
        _mapped = null;
    }
}

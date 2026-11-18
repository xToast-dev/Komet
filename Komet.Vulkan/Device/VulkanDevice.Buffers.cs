using System.Runtime.CompilerServices;

namespace Komet.Vulkan;

// Komet's own buffers and their memory, made in one place: memory types tried in the caller's order, a device-local one
// the driver refuses for want of room tried again outside device memory (a GPU without resizable BAR has 256 MB of
// CPU-visible device memory), every handle let go on failure.
internal sealed unsafe partial class VulkanDevice
{
    private const int MaxTypeChoices = 4;
    private const int NoType = int.MinValue;

    // types: memory property flags each type must have (Want) and must not (Avoid), best first. map: mapped for life.
    // null when made, else why not. file: whose memory it is in VkMemory's report.
    public string? MakeBuffer(ulong size, uint usage, ReadOnlySpan<(uint Want, uint Avoid)> types, bool map,
        out Made made,
        [CallerFilePath] string file = "")
    {
        made = default;
        if (!Assert(Handle != IntPtr.Zero) || !Assert(size > 0) || !Assert(types.Length is > 0 and <= MaxTypeChoices))
        {
            return "no device";
        }

        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = size, Usage = usage };
        ulong buffer;
        if (VkApi.CreateBuffer(Handle, &info, null, &buffer) != Vk.Success) return "vkCreateBuffer failed";

        Vk.MemoryRequirements needs;
        VkApi.GetBufferRequirements(Handle, buffer, &needs);
        var memory = Allocated(needs, types, file, out var why);
        void* mapped = null;
        if (memory != 0 && VkApi.BindBufferMemory(Handle, buffer, memory, 0) != Vk.Success)
        {
            why = "vkBindBufferMemory failed";
        }
        else if (memory != 0 && map && VkApi.MapMemory(Handle, memory, 0, needs.Size, 0, &mapped) != Vk.Success)
        {
            why = "vkMapMemory failed";
        }

        if (why.Length > 0)
        {
            VkApi.DestroyBuffer(Handle, buffer, null);
            VkApi.FreeMemory(Handle, memory, null); // nothing for 0
            return why;
        }

        made = new Made(buffer, memory, (nint)mapped);
        return null;
    }

    // The first type choice that allocates
    private ulong Allocated(Vk.MemoryRequirements needs, ReadOnlySpan<(uint Want, uint Avoid)> types, string file,
        out string why)
    {
        why = "no memory type for the buffer";
        if (!Assert(needs.TypeBits != 0)) return 0;

        for (var i = 0; i < Math.Min(types.Length, MaxTypeChoices); i++)
        {
            var (want, avoid) = types[i];
            var result = Allocation(needs, MemoryType(needs.TypeBits, want, avoid), file, out var memory);
            if (result == Vk.OutOfDeviceMemory && (want & Vk.DeviceLocal) != 0)
            {
                // outside device memory; system memory is host-visible on every device. A device with only device-local
                // types (integrated, UMA) has none: the full memory stays the reason.
                var outside = want & ~Vk.DeviceLocal;
                var full = result;
                result = Allocation(needs,
                    MemoryType(needs.TypeBits, outside != 0 ? outside : Vk.HostVisible, avoid | Vk.DeviceLocal),
                    file, out memory);
                if (result == NoType) result = full;
            }

            if (result == Vk.Success)
            {
                why = "";
                return memory;
            }

            if (result != NoType) why = $"vkAllocateMemory returned {result}";
        }

        return 0;
    }

    // NoType for type -1 (no such memory type)
    private int Allocation(Vk.MemoryRequirements needs, int type, string file, out ulong memory)
    {
        memory = 0;
        if (type < 0 || !Assert(needs.Size > 0)) return NoType;

        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        ulong made;
        var result = VkApi.AllocateMemoryFor(Handle, &allocate, null, &made, file);
        if (result == Vk.Success) memory = made;

        return result;
    }

    // Mapped is 0 unless the buffer was asked to be mapped
    public readonly record struct Made(ulong Buffer, ulong Memory, nint Mapped);
}

using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// The device whose UUID GL reports (GL_DEVICE_UUID_EXT), so Vulkan draws on the GPU OpenGL runs on. Without it, or when no
// device has it: the GL_RENDERER-named device, else GL's vendor, else the first discrete GPU.
internal sealed unsafe partial class VulkanDevice
{
    private const int DiscreteGpu = 2, CpuDevice = 4, MaxStrip = 8, ApiVersionOffset = 16, SameUuid = 1000;

    public string Picked { get; private set; } = "";

    private string? Pick(ReadOnlySpan<byte> uuid, (string? Vendor, string? Renderer) gl)
    {
        var count = (uint)MaxDevices;
        var devices = stackalloc IntPtr[MaxDevices];
        if (VkApi.EnumeratePhysicalDevices(InstanceHandle, &count, devices) < 0) return "no Vulkan devices";

        var properties = stackalloc byte[Vk.PropertiesBytes];
        var (best, score) = (-1, int.MinValue);
        for (var i = 0; i < Math.Min((int)count, MaxDevices); i++)
        {
            var id = new Vk.IdProperties { SType = Vk.PhysicalDeviceIdProperties };
            Described(devices[i], properties, &id);
            var mine = Same(&id, uuid) ? SameUuid : Likeness(properties, gl);
            if (mine > score) (best, score) = (i, mine);

            if (mine == SameUuid) break;
        }

        if (best < 0 || score == int.MinValue) return "no Vulkan device";

        var chosen = new Vk.IdProperties { SType = Vk.PhysicalDeviceIdProperties };
        Described(devices[best], properties, &chosen);
        (PhysicalHandle, Name) = (devices[best],
            Marshal.PtrToStringUTF8((IntPtr)(properties + Vk.DeviceNameOffset)) ?? "");
        Picked = How(score);
        var api = *(uint*)(properties + ApiVersionOffset); // variant bits 0 for Vulkan itself: compares as a number
        if (api < Vk.ApiVersion13)
        {
            return
                $"{Name} offers Vulkan {(api >> 22) & 0x7F}.{(api >> 12) & 0x3FF}, and Komet's renderer needs Vulkan 1.3 " +
                "(a newer driver may offer it)";
        }

        Identify(properties);
        return Queues();
    }

    // OpenGL reported a UUID and this device has it
    private static bool Same(Vk.IdProperties* id, ReadOnlySpan<byte> uuid) =>
        uuid.Length == UuidBytes && new ReadOnlySpan<byte>(id->DeviceUuid, UuidBytes).SequenceEqual(uuid);

    private static string How(int score)
    {
        if (score >= SameUuid) return "OpenGL's UUID";

        if (score >= 8) return "OpenGL's renderer name";

        if (score >= 4) return "OpenGL's vendor";

        return score >= 2 ? "the first discrete GPU" : "the first device";
    }

    private static void Described(IntPtr device, byte* properties, Vk.IdProperties* id)
    {
        if (!Assert(device != IntPtr.Zero)) return;

        new Span<byte>(properties, Vk.PropertiesBytes).Clear();
        *(int*)properties = Vk.PhysicalDeviceProperties2;
        *(Vk.IdProperties**)(properties + 8) = id;
        VkApi.GetPhysicalDeviceProperties2(device, properties);
    }

    // 8 for OpenGL's renderer by name, 4 for its vendor, 2 for a discrete GPU, 1 for any GPU; a CPU device last
    private static int Likeness(byte* properties, (string? Vendor, string? Renderer) gl)
    {
        var type = *(int*)(properties + Vk.TypeOffset);
        var vendor = *(uint*)(properties + Vk.VendorOffset);
        var name = Stripped(Marshal.PtrToStringUTF8((IntPtr)(properties + Vk.DeviceNameOffset)) ?? "");
        var renderer = Stripped(gl.Renderer ?? "");
        var named = name.Length >= 4 && renderer.Length >= 4 &&
                    (renderer.Contains(name, StringComparison.Ordinal) ||
                     name.Contains(renderer, StringComparison.Ordinal));
        var wanted = VendorOf($"{gl.Vendor} {gl.Renderer}");
        var score = (named ? 8 : 0) + (wanted != 0 && wanted == vendor ? 4 : 0) + (type == DiscreteGpu ? 2 : 0) + 1;
        return type == CpuDevice ? score - 16 : score;
    }

    // Lower case, without the parenthesised parts ("(RADV GFX1201)", "(R)") and what follows a slash ("/PCIe/SSE2")
    private static string Stripped(string name)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var cut = slash >= 0 ? name[..slash] : name;
        for (var i = 0; i < MaxStrip; i++)
        {
            var open = cut.IndexOf('(', StringComparison.Ordinal);
            var close = cut.IndexOf(')', StringComparison.Ordinal);
            if (open < 0) break;

            cut = close > open ? cut[..open] + cut[(close + 1)..] : cut[..open];
        }

        var words = cut.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }

    // The PCI vendor OpenGL's strings name, 0 for none known
    private static uint VendorOf(string gl)
    {
        var lower = gl.ToLowerInvariant();
        if (lower.Contains("nvidia", StringComparison.Ordinal)) return 0x10DE;

        if (lower.Contains("amd", StringComparison.Ordinal) || lower.Contains("radeon", StringComparison.Ordinal) ||
            lower.Contains("ati ", StringComparison.Ordinal))
        {
            return 0x1002;
        }

        return lower.Contains("intel", StringComparison.Ordinal) ? 0x8086u : 0u;
    }

    private string? Queues()
    {
        var families = stackalloc Vk.QueueFamily[MaxFamilies];
        var count = (uint)MaxFamilies;
        VkApi.GetQueueFamilies(PhysicalHandle, &count, families);
        for (var i = 0u; i < Math.Min(count, MaxFamilies); i++)
        {
            if ((families[i].Flags & (Vk.QueueGraphics | Vk.QueueCompute)) == (Vk.QueueGraphics | Vk.QueueCompute))
            {
                Family = i;
                if (families[i].TimestampBits == 0) TimestampPeriod = 0;

                Vk.MemoryProperties memory;
                VkApi.GetMemoryProperties(PhysicalHandle, &memory);
                _memory = memory;
                return null;
            }
        }

        return "no queue family does graphics and compute";
    }
}

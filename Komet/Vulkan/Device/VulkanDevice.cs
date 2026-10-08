using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// The device whose UUID OpenGL reports (GL_EXT_memory_object), which is what lets the two share memory and semaphores. Linux
// shares through file descriptors (VK_KHR_external_memory_fd, VK_KHR_external_semaphore_fd); Windows' handles come later.
internal sealed unsafe partial class VulkanDevice : IDisposable
{
    public const int UuidBytes = 16;
    private const int MaxDevices = 16, MaxFamilies = 32;
    private const string ClipControl = "VK_EXT_depth_clip_control", LineRasterization = "VK_KHR_line_rasterization";
    private const int MaxExtensions = 1024, MaxWanted = 8;

    private static readonly string[] Extensions =
        ["VK_KHR_external_memory_fd", "VK_KHR_external_semaphore_fd", "VK_KHR_push_descriptor"];

    private IntPtr _instance, _physical, _device, _queue, _commands;
    private ulong _pool, _fence;
    private uint _family;
    private Vk.MemoryProperties _memory;

    public string Name { get; private set; } = "";

    // VK_EXT_depth_clip_control is on: a pipeline can take OpenGL's clip space depth, -1 to 1, as it is
    public bool DepthClipControl { get; private set; }

    // Lines wider than a pixel (the engine outlines the selected block 1.6 wide), rasterized as OpenGL does (Bresenham's, not
    // Vulkan's parallelograms) where the device offers it
    public bool WideLines { get; private set; }
    public bool BresenhamLines { get; private set; }

    public bool SmoothLines { get; private set; }
    public bool NonSolidFill { get; private set; }
    public bool LogicOps { get; private set; }

    public bool IndirectCount { get; private set; }

    // occlusionQueryPrecise: an occlusion query counts the samples that passed, as OpenGL's GL_SAMPLES_PASSED
    public bool PreciseQueries { get; private set; }

    // shaderFloat64, enabled when offered: doubles in compute shaders (rows.comp)
    public bool Float64 { get; private set; }

    // pipelineStatisticsQuery: what the opaque terrain cost in the pipeline (VulkanFrame.Statistics)
    public bool PipelineStatistics { get; private set; }

    // Nanoseconds per timestamp tick, 0 when the queue takes no timestamps (VulkanFrame.Times)
    public float TimestampPeriod { get; private set; }
    public IntPtr Handle => _device;
    public uint Family => _family;

    public IntPtr InstanceHandle => _instance;
    public IntPtr PhysicalHandle => _physical;

    public bool Surfaces { get; private set; }
    public bool XlibSurfaces { get; private set; }
    public bool WaylandSurfaces { get; private set; }
    public bool Swapchains { get; private set; }

    public SharedArena Arena => _arena ??= new SharedArena(this);
    private SharedArena? _arena;

    public static VulkanDevice? Create(ReadOnlySpan<byte> uuid, out string why)
    {
        why = "";
        if (!Assert(uuid.Length == UuidBytes)) return null;
        if (!VkApi.Load())
        {
            why = "no Vulkan loader";
            return null;
        }

        var device = new VulkanDevice();
        why = device.Instance() ?? device.Pick(uuid) ?? device.Logical() ?? device.Pool() ?? "";
        if (why.Length == 0)
        {
            device.OpenCache();
            return device;
        }

        device.Dispose();
        return null;
    }

    private string? Instance()
    {
        if (!Assert(_instance == IntPtr.Zero)) return "an instance already";
        var surfaces = VkApi.InstanceOffers(Vk.SurfaceExtension);
        XlibSurfaces = surfaces && VkApi.InstanceOffers(Vk.XlibSurfaceExtension);
        WaylandSurfaces = surfaces && VkApi.InstanceOffers(Vk.WaylandSurfaceExtension);
        Surfaces = XlibSurfaces || WaylandSurfaces;
        fixed (byte* name = "Komet\0"u8, surface = Encoding.UTF8.GetBytes(Vk.SurfaceExtension + "\0"),
               xlib = Encoding.UTF8.GetBytes(Vk.XlibSurfaceExtension + "\0"),
               wayland = Encoding.UTF8.GetBytes(Vk.WaylandSurfaceExtension + "\0"))
        {
            var app = new Vk.AppInfo
            {
                SType = Vk.ApplicationInfo, ApplicationName = name, EngineName = name, Api = Vk.ApiVersion13
            };
            var extensions = stackalloc byte*[3];
            var count = 0u;
            if (Surfaces) extensions[count++] = surface;
            if (XlibSurfaces) extensions[count++] = xlib;
            if (WaylandSurfaces) extensions[count++] = wayland;
            var info = new Vk.InstanceInfo
            {
                SType = Vk.InstanceCreateInfo, Application = &app, ExtensionCount = count,
                Extensions = count > 0 ? extensions : null
            };
            IntPtr instance;
            if (VkApi.CreateInstance(&info, null, &instance) != Vk.Success || !Assert(instance != IntPtr.Zero))
                return "vkCreateInstance failed";
            _instance = instance;
            return VkApi.LoadInstance(instance) ? null : "Vulkan instance functions missing";
        }
    }

    private string? Pick(ReadOnlySpan<byte> uuid)
    {
        if (!Assert(_instance != IntPtr.Zero) || !Assert(uuid.Length == UuidBytes)) return "no instance";
        var count = (uint)MaxDevices;
        var devices = stackalloc IntPtr[MaxDevices];
        if (VkApi.EnumeratePhysicalDevices(_instance, &count, devices) < 0) return "no Vulkan devices";
        var properties = stackalloc byte[Vk.PropertiesBytes];
        for (var i = 0; i < Math.Min((int)count, MaxDevices); i++)
        {
            var id = new Vk.IdProperties { SType = Vk.PhysicalDeviceIdProperties };
            new Span<byte>(properties, Vk.PropertiesBytes).Clear();
            *(int*)properties = Vk.PhysicalDeviceProperties2;
            *(Vk.IdProperties**)(properties + 8) = &id;
            VkApi.GetPhysicalDeviceProperties2(devices[i], properties);
            if (!new ReadOnlySpan<byte>(id.DeviceUuid, UuidBytes).SequenceEqual(uuid)) continue;
            (_physical, Name) = (devices[i], Marshal.PtrToStringUTF8((IntPtr)(properties + Vk.DeviceNameOffset)) ?? "");
            Identify(properties);
            break;
        }

        if (_physical == IntPtr.Zero) return "no Vulkan device is the OpenGL one";
        var families = stackalloc Vk.QueueFamily[MaxFamilies];
        count = MaxFamilies;
        VkApi.GetQueueFamilies(_physical, &count, families);
        for (var i = 0u; i < Math.Min(count, MaxFamilies); i++)
            if ((families[i].Flags & (Vk.QueueGraphics | Vk.QueueCompute)) == (Vk.QueueGraphics | Vk.QueueCompute))
            {
                _family = i;
                if (families[i].TimestampBits == 0) TimestampPeriod = 0;
                Vk.MemoryProperties memory;
                VkApi.GetMemoryProperties(_physical, &memory);
                _memory = memory;
                return null;
            }

        return Assert(count <= MaxFamilies) ? "no queue family does graphics and compute" : "too many queue families";
    }

    private bool Offers(string extension)
    {
        if (!Assert(_physical != IntPtr.Zero) || !Assert(extension.Length < Vk.ExtensionNameBytes)) return false;
        var count = 0u;
        _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, null);
        var properties = new Vk.ExtensionProperties[Math.Min(count, MaxExtensions)];
        count = (uint)properties.Length;
        fixed (Vk.ExtensionProperties* p = properties) _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, p);
        return VkApi.Lists(properties, count, extension);
    }

    // Every feature the terrain uses only where the device offers it, so the device is made and a pipeline that needs a missing
    // one refuses instead
    private void Wanted(Vk.Features2* features, Vk.Features12* v12, Vk.LineFeaturesInfo* lines)
    {
        if (!Assert(features != null && v12 != null) || !Assert(_physical != IntPtr.Zero)) return;
        var offeredLines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
        var offered12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = BresenhamLines ? &offeredLines : null };
        var offered = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &offered12 };
        VkApi.GetPhysicalDeviceFeatures2(_physical, &offered);
        foreach (var feature in (ReadOnlySpan<int>)[Vk.IndependentBlend, Vk.MultiDrawIndirect, Vk.SamplerAnisotropy,
                     Vk.FeatureDepthClamp, Vk.DrawIndirectFirstInstance, Vk.OcclusionQueryPrecise, Vk.WideLines,
                     Vk.FillModeNonSolid, Vk.FeatureLogicOp, Vk.ShaderFloat64, Vk.PipelineStatisticsQuery])
            features->Features[feature] = offered.Features[feature];
        PreciseQueries = offered.Features[Vk.OcclusionQueryPrecise] != 0;
        Float64 = offered.Features[Vk.ShaderFloat64] != 0;
        PipelineStatistics = offered.Features[Vk.PipelineStatisticsQuery] != 0;
        WideLines = offered.Features[Vk.WideLines] != 0;
        (NonSolidFill, LogicOps) = (offered.Features[Vk.FillModeNonSolid] != 0, offered.Features[Vk.FeatureLogicOp] != 0);
        if (BresenhamLines && lines != null)
        {
            (lines->Bresenham, lines->Smooth) = (offeredLines.Bresenham, offeredLines.Smooth);
            (BresenhamLines, SmoothLines) = (offeredLines.Bresenham != 0, offeredLines.Smooth != 0);
        }
        v12->Features[Vk.DrawIndirectCount] = offered12.Features[Vk.DrawIndirectCount];
        IndirectCount = offered12.Features[Vk.DrawIndirectCount] != 0;
        _ = Assert(v12->SType == Vk.Vulkan12Features);
    }

    private string? Logical()
    {
        if (!Assert(_physical != IntPtr.Zero) || !Assert(_device == IntPtr.Zero)) return "no device";
        DepthClipControl = Offers(ClipControl);
        BresenhamLines = Offers(LineRasterization);
        Swapchains = Surfaces && Offers(Vk.SwapchainExtension);
        string[] wanted = DepthClipControl ? [.. Extensions, ClipControl] : Extensions;
        if (BresenhamLines) wanted = [.. wanted, LineRasterization];
        if (Swapchains) wanted = [.. wanted, Vk.SwapchainExtension];
        var names = Array.ConvertAll(wanted, Marshal.StringToCoTaskMemUTF8);
        try
        {
            var pointers = stackalloc byte*[MaxWanted];
            for (var i = 0; i < Math.Min(names.Length, MaxWanted); i++) pointers[i] = (byte*)names[i];
            var lines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
            var clip = new Vk.OneFeature
            {
                SType = Vk.DepthClipControlFeatures, Enabled = 1, Next = BresenhamLines ? &lines : null
            };
            void* chain = BresenhamLines ? &lines : null;
            if (DepthClipControl) chain = &clip;
            var v13 = new Vk.Features13 { SType = Vk.Vulkan13Features, Next = chain };
            v13.Features[Vk.DynamicRendering] = 1;
            var v12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = &v13 };
            var features = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &v12 };
            Wanted(&features, &v12, &lines);
            var priority = 1f;
            var queue = new Vk.QueueInfo
            {
                SType = Vk.DeviceQueueCreateInfo, Family = _family, Count = 1, Priorities = &priority
            };
            var info = new Vk.DeviceInfo
            {
                SType = Vk.DeviceCreateInfo, Next = &features, QueueInfoCount = 1, QueueInfos = &queue,
                ExtensionCount = (uint)Math.Min(wanted.Length, MaxWanted), Extensions = pointers
            };
            IntPtr device;
            if (VkApi.CreateDevice(_physical, &info, null, &device) != Vk.Success) return "vkCreateDevice failed";
            _device = device;
            return VkApi.LoadDevice(device) ? null : "Vulkan device functions missing";
        }
        finally
        {
            foreach (var name in names.Bounded(MaxWanted)) Marshal.FreeCoTaskMem(name);
        }
    }

    private string? Pool()
    {
        if (!Assert(_device != IntPtr.Zero)) return "no device";
        IntPtr queue, commands;
        VkApi.GetDeviceQueue(_device, _family, 0, &queue);
        var pool = new Vk.PoolInfo
        {
            SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = _family
        };
        ulong handle, fence;
        if (VkApi.CreateCommandPool(_device, &pool, null, &handle) != Vk.Success) return "vkCreateCommandPool failed";
        _pool = handle;
        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = handle, Count = 1 };
        if (VkApi.AllocateCommandBuffers(_device, &allocate, &commands) != Vk.Success) return "no command buffer";
        var fenceInfo = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo };
        if (VkApi.CreateFence(_device, &fenceInfo, null, &fence) != Vk.Success) return "vkCreateFence failed";
        (_queue, _commands, _fence) = (queue, commands, fence);
        return Assert(queue != IntPtr.Zero) ? null : "no queue";
    }

    public int MemoryType(uint typeBits, uint wanted, uint unwanted = 0)
    {
        if (!Assert(_memory.TypeCount <= 32) || !Assert(wanted != 0)) return -1;
        for (var i = 0; i < Math.Min((int)_memory.TypeCount, 32); i++)
            if ((typeBits & (1u << i)) != 0 && (_memory.Types[2 * i] & wanted) == wanted &&
                (_memory.Types[2 * i] & unwanted) == 0)
                return i;
        return -1;
    }

    // Records into the command buffer, submits it signalling the given semaphores, and waits for it to finish
    public bool Run(Action<IntPtr> record, ReadOnlySpan<ulong> signal) =>
        Assert(signal.Length <= 4) && Run(record, [], signal);

    // The same, the commands waiting for the wait semaphores first (OpenGL's signal)
    public bool Run(Action<IntPtr> record, ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal)
    {
        if (!NotNull(record) || !Assert(signal.Length <= 4)) return false;
        var commands = Begin();
        if (commands == IntPtr.Zero) return false;
        record(commands);
        return Submit(wait, signal);
    }

    public IntPtr Begin() =>
        Assert(_commands != IntPtr.Zero) && Assert(_device != IntPtr.Zero) && Begin(_commands) ? _commands : IntPtr.Zero;

    // Ends and submits what Begin's buffer holds, signalling the semaphores, and waits for the GPU to finish it
    public bool Submit(ReadOnlySpan<ulong> signal) => Assert(signal.Length <= 4) && Submit([], signal);

    // The same after the wait semaphores, which every stage waits for
    public bool Submit(ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal)
    {
        var fence = _fence;
        if (!Submit([_commands], wait, signal, fence)) return false;
        var done = VkApi.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue) == Vk.Success;
        return VkApi.ResetFences(_device, 1, &fence) == Vk.Success && Assert(done) && Assert(fence != 0);
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            _ = VkApi.DeviceWaitIdle(_device);
            CloseCache();
            _arena?.Dispose();
            _arena = null;
            if (_fence != 0) VkApi.DestroyFence(_device, _fence, null);
            if (_pool != 0) VkApi.DestroyCommandPool(_device, _pool, null);
            VkApi.DestroyDevice(_device, null);
        }

        if (_instance != IntPtr.Zero) VkApi.DestroyInstance(_instance, null);
        (_device, _instance, _queue, _commands) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        (_pool, _fence) = (0, 0);
        _ = Assert(_device == IntPtr.Zero) && Assert(_instance == IntPtr.Zero);
    }
}

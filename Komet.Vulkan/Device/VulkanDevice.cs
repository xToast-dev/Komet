using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// The device whose UUID GL reports (GL_DEVICE_UUID_EXT where offered), else picked by GL's vendor and renderer names
// (Pick). Nothing is shared with OpenGL.
internal sealed unsafe partial class VulkanDevice : IDisposable
{
    public const int UuidBytes = 16;
    private const int MaxDevices = 16, MaxFamilies = 32;
    private const string ClipControl = "VK_EXT_depth_clip_control", LineRasterization = "VK_KHR_line_rasterization";
    private const int MaxExtensions = 1024, MaxWanted = 12;

    private static readonly string[] Extensions = ["VK_KHR_push_descriptor"];

    // Formats whose optimal-tiling features mods' raw GL asks for (Formats)
    private static readonly uint[] Asked =
        [Vk.FormatD24S8, Vk.FormatD32S8, Vk.FormatA2B10G10R10, Vk.FormatB10G11R11F, Vk.FormatR16, Vk.FormatRgba16];

    // The last device's optimal-tiling features of the formats Asked (one device runs at a time)
    private static readonly Dictionary<uint, uint> Optimal = [];
    private Vk.MemoryProperties _memory;
    private ulong _pool, _fence;

    private IntPtr _queue, _commands;

    public string Name { get; private set; } = "";

    // VK_EXT_depth_clip_control is on: a pipeline can take OpenGL's clip space depth, -1 to 1, as it is
    public bool DepthClipControl { get; private set; }

    // Lines wider than a pixel (block outline is 1.6), rasterized like GL (Bresenham, not parallelograms) where offered
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

    // pipelineStatisticsQuery: what the opaque terrain cost in the pipeline (VulkanFrame.CountedFrame)
    public bool PipelineStatistics { get; private set; }

    // Nanoseconds per timestamp tick, 0 when the queue takes no timestamps (VulkanFrame.Times)
    public float TimestampPeriod { get; private set; }
    public IntPtr Handle { get; private set; }

    public uint Family { get; private set; }

    public IntPtr InstanceHandle { get; private set; }

    public IntPtr PhysicalHandle { get; private set; }

    private bool Surfaces { get; set; }
    public bool XlibSurfaces { get; private set; }
    public bool WaylandSurfaces { get; private set; }
    public bool Win32Surfaces { get; private set; }
    public bool Swapchains { get; private set; }

    // What a Vulkan-only device offers mods' raw GL: a depth-stencil format (DEPTH24_STENCIL8 as D24S8 if drawable and
    // sampleable, else D32S8; 0 for none), 8-bit indices, instance divisors past 1, multi-draw indirect, and extra blit
    // formats (Blits)
    public uint DepthStencil { get; private set; }
    public bool IndexUint8 { get; private set; }
    public bool Divisors { get; private set; }

    // multiDrawIndirect: an indirect draw takes several commands at once
    public bool MultiDraws { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            lock (_queueLock)
            {
                _ = VkApi.DeviceWaitIdle(Handle); // vkDeviceWaitIdle syncs every queue externally
            }

            CloseCache();
            if (_fence != 0) VkApi.DestroyFence(Handle, _fence, null);

            if (_pool != 0) VkApi.DestroyCommandPool(Handle, _pool, null);

            VkApi.DestroyDevice(Handle, null);
        }

        if (InstanceHandle != IntPtr.Zero) VkApi.DestroyInstance(InstanceHandle, null);

        (Handle, InstanceHandle, _queue, _commands) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        (_pool, _fence) = (0, 0);
    }

    // No UUID: picked by GL_VENDOR/GL_RENDERER (Pick)
    public static VulkanDevice? Create(ReadOnlySpan<byte> uuid, (string? Vendor, string? Renderer) gl, out string why)
    {
        why = "";
        if (!Assert(uuid.Length is UuidBytes or 0)) return null;

        if (!VkApi.Load())
        {
            why = "no Vulkan loader";
            return null;
        }

        var device = Owned.Kept(new VulkanDevice(), (uuid: uuid.ToArray(), gl),
            static (device, s) => device.Instance() ?? device.Pick(s.uuid, s.gl) ?? device.Logical() ?? device.Pool(),
            out why);
        device?.OpenCache();
        return device;
    }

    private string? Instance()
    {
        var surfaces = VkApi.InstanceOffers(Vk.SurfaceExtension);
        XlibSurfaces = surfaces && VkApi.InstanceOffers(Vk.XlibSurfaceExtension);
        WaylandSurfaces = surfaces && VkApi.InstanceOffers(Vk.WaylandSurfaceExtension);
        Win32Surfaces = surfaces && VkApi.InstanceOffers(Vk.Win32SurfaceExtension);
        Surfaces = XlibSurfaces || WaylandSurfaces || Win32Surfaces;
        fixed (byte* name = "Komet\0"u8, surface = Encoding.UTF8.GetBytes(Vk.SurfaceExtension + "\0"),
               xlib = Encoding.UTF8.GetBytes(Vk.XlibSurfaceExtension + "\0"),
               wayland = Encoding.UTF8.GetBytes(Vk.WaylandSurfaceExtension + "\0"),
               win32 = Encoding.UTF8.GetBytes(Vk.Win32SurfaceExtension + "\0"))
        {
            var app = new Vk.AppInfo
            {
                SType = Vk.ApplicationInfo, ApplicationName = name, EngineName = name, Api = Vk.ApiVersion13
            };
            var extensions = stackalloc byte*[4];
            var count = 0u;
            if (Surfaces) extensions[count++] = surface;

            if (XlibSurfaces) extensions[count++] = xlib;

            if (WaylandSurfaces) extensions[count++] = wayland;

            if (Win32Surfaces) extensions[count++] = win32;

            var info = new Vk.InstanceInfo
            {
                SType = Vk.InstanceCreateInfo, Application = &app, ExtensionCount = count,
                Extensions = count > 0 ? extensions : null
            };
            IntPtr instance;
            if (VkApi.CreateInstance(&info, null, &instance) != Vk.Success || !Assert(instance != IntPtr.Zero))
            {
                return "vkCreateInstance failed";
            }

            InstanceHandle = instance;
            return VkApi.LoadInstance(instance) ? null : "Vulkan instance functions missing";
        }
    }

    private bool Offers(string extension)
    {
        var count = 0u;
        _ = VkApi.EnumerateDeviceExtensions(PhysicalHandle, null, &count, null);
        var properties = new Vk.ExtensionProperties[Math.Min(count, MaxExtensions)];
        count = (uint)properties.Length;
        fixed (Vk.ExtensionProperties* p = properties)
        {
            _ = VkApi.EnumerateDeviceExtensions(PhysicalHandle, null, &count, p);
        }

        return VkApi.Lists(properties, count, extension);
    }

    // Each terrain feature enabled only where offered: the device is made, a pipeline needing a missing one refuses
    private void Wanted(Vk.Features2* features, Vk.Features12* v12, Vk.LineFeaturesInfo* lines)
    {
        var offeredLines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
        void* only = BresenhamLines ? &offeredLines : null;
        var offeredUint8 = new Vk.OneFeature { SType = Vk.IndexTypeUint8Features, Next = only };
        if (IndexUint8) only = &offeredUint8;

        var offeredDivisor = new Vk.TwoFeatures { SType = Vk.DivisorFeatures, Next = only };
        if (Divisors) only = &offeredDivisor;

        var offered12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = only };
        var offered = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &offered12 };
        VkApi.GetPhysicalDeviceFeatures2(PhysicalHandle, &offered);
        foreach (var feature in (ReadOnlySpan<int>)
                 [
                     Vk.IndependentBlend, Vk.MultiDrawIndirect, Vk.SamplerAnisotropy,
                     Vk.FeatureDepthClamp, Vk.DrawIndirectFirstInstance, Vk.OcclusionQueryPrecise, Vk.WideLines,
                     Vk.FillModeNonSolid, Vk.FeatureLogicOp, Vk.ShaderFloat64, Vk.PipelineStatisticsQuery
                 ])
        {
            features->Features[feature] = offered.Features[feature];
        }

        PreciseQueries = offered.Features[Vk.OcclusionQueryPrecise] != 0;
        MultiDraws = offered.Features[Vk.MultiDrawIndirect] != 0;
        (IndexUint8, Divisors) = (IndexUint8 && offeredUint8.Enabled != 0, Divisors && offeredDivisor.First != 0);
        Float64 = offered.Features[Vk.ShaderFloat64] != 0;
        PipelineStatistics = offered.Features[Vk.PipelineStatisticsQuery] != 0;
        WideLines = offered.Features[Vk.WideLines] != 0;
        (NonSolidFill, LogicOps) =
            (offered.Features[Vk.FillModeNonSolid] != 0, offered.Features[Vk.FeatureLogicOp] != 0);
        if (BresenhamLines && lines != null)
        {
            (lines->Bresenham, lines->Smooth) = (offeredLines.Bresenham, offeredLines.Smooth);
            (BresenhamLines, SmoothLines) = (offeredLines.Bresenham != 0, offeredLines.Smooth != 0);
        }

        v12->Features[Vk.DrawIndirectCount] = offered12.Features[Vk.DrawIndirectCount];
        IndirectCount = offered12.Features[Vk.DrawIndirectCount] != 0;
    }

    // The extensions the renderer needs (else why not) and the optional ones the device has
    private string? Offered()
    {
        foreach (var extension in Extensions.Bounded(MaxWanted))
        {
            if (!Offers(extension)) return $"{Name} has no {extension}, which Komet's renderer needs";
        }

        DepthClipControl = Offers(ClipControl);
        BresenhamLines = Offers(LineRasterization);
        (IndexUint8, Divisors) =
            (Offers(Vk.IndexUint8Extension), Offers(Vk.DivisorExtension)); // what mods' raw OpenGL needs
        Swapchains = Surfaces && Offers(Vk.SwapchainExtension);
        return null;
    }

    private string? Logical()
    {
        if (Offered() is { } missing) return missing;

        var wanted = Extras(Extensions);
        var names = Array.ConvertAll(wanted, Marshal.StringToCoTaskMemUTF8);
        try
        {
            var pointers = stackalloc byte*[MaxWanted];
            for (var i = 0; i < Math.Min(names.Length, MaxWanted); i++)
            {
                pointers[i] = (byte*)names[i];
            }

            var lines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
            var clip = new Vk.OneFeature
            {
                SType = Vk.DepthClipControlFeatures, Enabled = 1, Next = BresenhamLines ? &lines : null
            };
            void* chain = BresenhamLines ? &lines : null;
            if (DepthClipControl) chain = &clip;

            var uint8 = new Vk.OneFeature { SType = Vk.IndexTypeUint8Features, Enabled = 1, Next = chain };
            if (IndexUint8) chain = &uint8;

            var divisor = new Vk.TwoFeatures { SType = Vk.DivisorFeatures, First = 1, Next = chain };
            if (Divisors) chain = &divisor;

            var v13 = new Vk.Features13 { SType = Vk.Vulkan13Features, Next = chain };
            v13.Features[Vk.DynamicRendering] = 1;
            var v12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = &v13 };
            var features = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &v12 };
            Wanted(&features, &v12, &lines);
            (uint8.Enabled, divisor.First) = (IndexUint8 ? 1u : 0, Divisors ? 1u : 0); // as offered
            var priority = 1f;
            var queue = new Vk.QueueInfo
            {
                SType = Vk.DeviceQueueCreateInfo, Family = Family, Count = 1, Priorities = &priority
            };
            var info = new Vk.DeviceInfo
            {
                SType = Vk.DeviceCreateInfo, Next = &features, QueueInfoCount = 1, QueueInfos = &queue,
                ExtensionCount = (uint)Math.Min(wanted.Length, MaxWanted), Extensions = pointers
            };
            IntPtr device;
            if (VkApi.CreateDevice(PhysicalHandle, &info, null, &device) != Vk.Success) return "vkCreateDevice failed";

            Handle = device;
            Formats();
            var loaded = VkApi.LoadDevice(device);
            return loaded ? null : "Vulkan device functions missing";
        }
        finally
        {
            foreach (var name in names.Bounded(MaxWanted))
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
    }

    private string? Pool()
    {
        IntPtr queue, commands;
        VkApi.GetDeviceQueue(Handle, Family, 0, &queue);
        var pool = new Vk.PoolInfo
        {
            SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = Family
        };
        ulong handle, fence;
        if (VkApi.CreateCommandPool(Handle, &pool, null, &handle) != Vk.Success) return "vkCreateCommandPool failed";

        _pool = handle;
        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = handle, Count = 1 };
        if (VkApi.AllocateCommandBuffers(Handle, &allocate, &commands) != Vk.Success) return "no command buffer";

        var fenceInfo = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo };
        if (VkApi.CreateFence(Handle, &fenceInfo, null, &fence) != Vk.Success) return "vkCreateFence failed";

        (_queue, _commands, _fence) = (queue, commands, fence);
        return Assert(queue != IntPtr.Zero) ? null : "no queue";
    }

    private void Formats()
    {
        Optimal.Clear(); // first: an earlier device's formats are never this one's
        SharedFormat.Stencils = null;
        if (VkApi.GetFormatProperties == null) return;

        foreach (var format in Asked.Bounded(Asked.Length))
        {
            Vk.FormatProperties properties;
            VkApi.GetFormatProperties(PhysicalHandle, format, &properties);
            Optimal[format] = properties.Optimal;
        }

        const uint wanted = Vk.FeatureDepthStencilAttachment | Vk.FeatureSampled;
        DepthStencil = (Optimal[Vk.FormatD32S8] & wanted) == wanted ? Vk.FormatD32S8 : 0;
        if ((Optimal[Vk.FormatD24S8] & wanted) == wanted) DepthStencil = Vk.FormatD24S8;

        SharedFormat.Stencils = DepthStencil != 0 ? new SharedFormat(DepthStencil, Vk.AspectDepthStencil) : null;
    }

    // The extensions wanted beside those every device is made with
    private string[] Extras(string[] wanted)
    {
        if (DepthClipControl) wanted = [.. wanted, ClipControl];

        if (BresenhamLines) wanted = [.. wanted, LineRasterization];

        if (Swapchains) wanted = [.. wanted, Vk.SwapchainExtension];

        if (IndexUint8) wanted = [.. wanted, Vk.IndexUint8Extension];

        if (Divisors) wanted = [.. wanted, Vk.DivisorExtension];

        return wanted;
    }

    // Whether the device blits from (into: to) the format, of those Asked; false for any other
    public static bool Blits(uint format, bool into) =>
        Optimal.TryGetValue(format, out var features) &&
        (features & (into ? Vk.FeatureBlitTarget : Vk.FeatureBlitSource)) != 0;

    public int MemoryType(uint typeBits, uint wanted, uint unwanted = 0)
    {
        if (!Assert(_memory.TypeCount <= 32) || !Assert(wanted != 0)) return -1;

        for (var i = 0; i < Math.Min((int)_memory.TypeCount, 32); i++)
        {
            if ((typeBits & (1u << i)) != 0 && (_memory.Types[2 * i] & wanted) == wanted &&
                (_memory.Types[2 * i] & unwanted) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // Run and Submit wait for the GPU to finish, unlike the frames' Submit
    public bool Run(Action<IntPtr> record, ReadOnlySpan<ulong> signal)
    {
        if (!NotNull(record) || !Assert(signal.Length <= 4)) return false;

        var commands = Begin();
        if (commands == IntPtr.Zero) return false;

        record(commands);
        return Submit(signal);
    }

    public IntPtr Begin() => Begin(_commands) ? _commands : IntPtr.Zero;

    public bool Submit(ReadOnlySpan<ulong> signal)
    {
        var fence = _fence;
        if (!Submit([_commands], signal, fence)) return false;

        var done = VkApi.WaitForFences(Handle, 1, &fence, 1, ulong.MaxValue) == Vk.Success;
        return VkApi.ResetFences(Handle, 1, &fence) == Vk.Success && Assert(done);
    }
}

using System.Runtime.CompilerServices;
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

    private IntPtr _instance, _physical, _device, _queue, _commands;
    private ulong _pool, _fence;
    private uint _family;
    private Vk.MemoryProperties _memory;

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
    public IntPtr Handle => _device;
    public uint Family => _family;

    public IntPtr InstanceHandle => _instance;
    public IntPtr PhysicalHandle => _physical;

    public bool Surfaces { get; private set; }
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
            static (device, s) => device.Instance() ?? device.Pick(s.uuid, s.gl) ?? device.Logical() ?? device.Pool(), out why);
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
                return "vkCreateInstance failed";
            _instance = instance;
            return VkApi.LoadInstance(instance) ? null : "Vulkan instance functions missing";
        }
    }

    private bool Offers(string extension)
    {
        var count = 0u;
        _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, null);
        var properties = new Vk.ExtensionProperties[Math.Min(count, MaxExtensions)];
        count = (uint)properties.Length;
        fixed (Vk.ExtensionProperties* p = properties) _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, p);
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
        VkApi.GetPhysicalDeviceFeatures2(_physical, &offered);
        foreach (var feature in (ReadOnlySpan<int>)[Vk.IndependentBlend, Vk.MultiDrawIndirect, Vk.SamplerAnisotropy,
                     Vk.FeatureDepthClamp, Vk.DrawIndirectFirstInstance, Vk.OcclusionQueryPrecise, Vk.WideLines,
                     Vk.FillModeNonSolid, Vk.FeatureLogicOp, Vk.ShaderFloat64, Vk.PipelineStatisticsQuery])
            features->Features[feature] = offered.Features[feature];
        PreciseQueries = offered.Features[Vk.OcclusionQueryPrecise] != 0;
        MultiDraws = offered.Features[Vk.MultiDrawIndirect] != 0;
        (IndexUint8, Divisors) = (IndexUint8 && offeredUint8.Enabled != 0, Divisors && offeredDivisor.First != 0);
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
    }

    private string? Logical()
    {
        foreach (var extension in Extensions.Bounded(MaxWanted))
            if (!Offers(extension))
                return $"{Name} has no {extension}, which Komet's renderer needs";
        DepthClipControl = Offers(ClipControl);
        BresenhamLines = Offers(LineRasterization);
        (IndexUint8, Divisors) = (Offers(Vk.IndexUint8Extension), Offers(Vk.DivisorExtension)); // what mods' raw OpenGL needs
        Swapchains = Surfaces && Offers(Vk.SwapchainExtension);
        var wanted = Extras(Extensions);
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
            Formats();
            var loaded = VkApi.LoadDevice(device);
            return loaded ? null : "Vulkan device functions missing";
        }
        finally
        {
            foreach (var name in names.Bounded(MaxWanted)) Marshal.FreeCoTaskMem(name);
        }
    }

    private string? Pool()
    {
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

    private void Formats()
    {
        Optimal.Clear(); // first: an earlier device's formats are never this one's
        SharedFormat.Stencils = null;
        if (VkApi.GetFormatProperties == null) return;
        foreach (var format in Asked.Bounded(Asked.Length))
        {
            Vk.FormatProperties properties;
            VkApi.GetFormatProperties(_physical, format, &properties);
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
        Optimal.TryGetValue(format, out var features) && (features & (into ? Vk.FeatureBlitTarget : Vk.FeatureBlitSource)) != 0;

    public int MemoryType(uint typeBits, uint wanted, uint unwanted = 0)
    {
        if (!Assert(_memory.TypeCount <= 32) || !Assert(wanted != 0)) return -1;
        for (var i = 0; i < Math.Min((int)_memory.TypeCount, 32); i++)
            if ((typeBits & (1u << i)) != 0 && (_memory.Types[2 * i] & wanted) == wanted &&
                (_memory.Types[2 * i] & unwanted) == 0)
                return i;
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
        var done = VkApi.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue) == Vk.Success;
        return VkApi.ResetFences(_device, 1, &fence) == Vk.Success && Assert(done);
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            lock (_queueLock) _ = VkApi.DeviceWaitIdle(_device); // vkDeviceWaitIdle syncs every queue externally
            CloseCache();
            if (_fence != 0) VkApi.DestroyFence(_device, _fence, null);
            if (_pool != 0) VkApi.DestroyCommandPool(_device, _pool, null);
            VkApi.DestroyDevice(_device, null);
        }

        if (_instance != IntPtr.Zero) VkApi.DestroyInstance(_instance, null);
        (_device, _instance, _queue, _commands) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        (_pool, _fence) = (0, 0);
    }
}

// Submitted without waiting; a frame's command buffer is reused only once its fence says the GPU is done with it
internal sealed unsafe partial class VulkanDevice
{
    public const ulong Forever = ulong.MaxValue;

    private readonly Lock _queueLock = new(); // the queue is used by one thread at a time

    // Before another thread's queue operation: the recording thread caught up (incl. its submissions), so this follows
    public Action? CatchUp { get; set; }

    // A command pool of its own, for a thread that records alone
    public ulong CommandPool()
    {
        if (!Assert(_device != IntPtr.Zero)) return 0;
        var info = new Vk.PoolInfo { SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = _family };
        ulong pool;
        return VkApi.CreateCommandPool(_device, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroyPool(ulong pool)
    {
        if (Assert(_device != IntPtr.Zero) && Assert(pool != _pool) && pool != 0)
            VkApi.DestroyCommandPool(_device, pool, null); // its command buffers with it
    }

    public IntPtr Allocate(ulong pool)
    {
        if (!Assert(pool != 0) || !Assert(_device != IntPtr.Zero)) return IntPtr.Zero;
        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = pool, Count = 1 };
        IntPtr commands;
        return VkApi.AllocateCommandBuffers(_device, &allocate, &commands) == Vk.Success ? commands : IntPtr.Zero;
    }

    public IntPtr Allocate() => Allocate(_pool);

    public ulong Fence(bool signalled)
    {
        if (!Assert(_device != IntPtr.Zero)) return 0;
        var info = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo, Flags = signalled ? 1u : 0 };
        ulong fence;
        return VkApi.CreateFence(_device, &info, null, &fence) == Vk.Success ? fence : 0;
    }

    public ulong Semaphore()
    {
        if (!Assert(_device != IntPtr.Zero)) return 0;
        var info = new Vk.FlagsInfo { SType = Vk.SemaphoreCreateInfo };
        ulong semaphore;
        return VkApi.CreateSemaphore(_device, &info, null, &semaphore) == Vk.Success ? semaphore : 0;
    }

    // 0 when the driver refuses it
    public ulong QueryPool(uint type, uint count, uint statistics = 0)
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(count > 0)) return 0;
        var info = new Vk.QueryPoolInfo
        {
            SType = Vk.QueryPoolCreateInfo, QueryType = type, QueryCount = count, PipelineStatistics = statistics
        };
        ulong pool;
        return VkApi.CreateQueryPool(_device, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroySemaphore(ulong semaphore)
    {
        if (Assert(_device != IntPtr.Zero) && semaphore != 0)
            VkApi.DestroySemaphore(_device, semaphore, null);
    }

    public void DestroyFence(ulong fence)
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(fence != _fence) || fence == 0) return;
        VkApi.DestroyFence(_device, fence, null);
    }

    public static bool Begin(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return false;
        var begin = new Vk.BeginInfo { SType = Vk.CommandBufferBeginInfo, Flags = Vk.OneTimeSubmit };
        return VkApi.BeginCommandBuffer(commands, &begin) == Vk.Success;
    }

    public static bool End(IntPtr commands) => Assert(commands != IntPtr.Zero) && VkApi.EndCommandBuffer(commands) == Vk.Success;

    // ended: how many of the first buffers were ended already
    public bool Submit(ReadOnlySpan<IntPtr> buffers, ReadOnlySpan<ulong> signal, ulong fence, int ended = 0)
    {
        if (!Assert(buffers.Length is > 0 and <= 4) || !Assert(signal.Length <= 4)) return false;
        foreach (var commands in buffers[Math.Clamp(ended, 0, buffers.Length)..].Bounded(4))
            if (VkApi.EndCommandBuffer(commands) != Vk.Success)
                return false;
        fixed (ulong* signals = signal)
        fixed (IntPtr* commands = buffers)
        {
            var submit = new Vk.Submit
            {
                SType = Vk.SubmitInfo, CommandBufferCount = (uint)buffers.Length, CommandBuffers = commands,
                SignalCount = (uint)signal.Length, Signals = signals
            };
            return Queued(&submit, 1, fence);
        }
    }

    private bool Queued(Vk.Submit* submit, uint count, ulong fence)
    {
        CatchUp?.Invoke();
        lock (_queueLock)
            return Assert(_queue != IntPtr.Zero) && VkApi.QueueSubmit(_queue, count, submit, fence) == Vk.Success;
    }

    public int Present(Vk.PresentInfo* info)
    {
        if (!Assert(info != null) || !Assert(_queue != IntPtr.Zero)) return -1;
        CatchUp?.Invoke();
        lock (_queueLock) return VkApi.QueuePresent(_queue, info);
    }

    // From a thread of its own whose work needs no order with the recording thread's beyond the queue's (FrameBridge: it
    // reads only what an earlier present published), so without CatchUp; ended before submitting
    public bool SubmitAlone(IntPtr commands, ulong signal, ulong fence)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(signal != 0) || VkApi.EndCommandBuffer(commands) != Vk.Success) return false;
        var stage = Vk.StageAll;
        var submit = new Vk.Submit
        {
            SType = Vk.SubmitInfo, CommandBufferCount = 1, CommandBuffers = &commands, SignalCount = 1, Signals = &signal,
            WaitStages = &stage
        };
        lock (_queueLock) return Assert(_queue != IntPtr.Zero) && VkApi.QueueSubmit(_queue, 1, &submit, fence) == Vk.Success;
    }

    public int PresentAlone(Vk.PresentInfo* info)
    {
        if (!Assert(info != null) || !Assert(_queue != IntPtr.Zero)) return -1;
        lock (_queueLock) return VkApi.QueuePresent(_queue, info);
    }

    public bool Signal(ulong fence) => Assert(fence != 0) && Queued(null, 0, fence);

    public bool WaitIdle()
    {
        if (!Assert(_device != IntPtr.Zero)) return false;
        CatchUp?.Invoke();
        lock (_queueLock) return VkApi.DeviceWaitIdle(_device) == Vk.Success;
    }

    // Resets the fence once signalled
    public bool Wait(ulong fence, ulong timeout = Forever)
    {
        if (!Assert(fence != 0) || !Assert(_device != IntPtr.Zero)) return false;
        if (VkApi.WaitForFences(_device, 1, &fence, 1, timeout) != Vk.Success) return false;
        return VkApi.ResetFences(_device, 1, &fence) == Vk.Success;
    }
}

// Kept across starts (ShaderCache). VkPipelineCacheHeaderVersionOne must match vendor, device and pipelineCacheUUID.
internal sealed unsafe partial class VulkanDevice
{
    private const int HeaderBytes = 32, HeaderVersionOne = 1;

    private readonly byte[] _cacheUuid = new byte[UuidBytes];
    private uint _vendor, _model;
    private int _keeping; // 1 while a KeepCache write is queued or running
    private int _closing; // 1 once CloseCache began: no write is queued after its wait
    private nuint _kept; // the cache's size when last written: the same size is not read or written again

    public ulong PipelineCache { get; private set; }

    private void Identify(byte* properties)
    {
        (_vendor, _model) = (*(uint*)(properties + Vk.VendorOffset), *(uint*)(properties + Vk.ModelOffset));
        // a bool that is not 0 or 1 says the offsets are wrong
        var (both, period) = (*(uint*)(properties + Vk.TimestampsOffset), *(float*)(properties + Vk.TimestampPeriodOffset));
        TimestampPeriod = both == 1 && float.IsFinite(period) && period is > 0 and < 1e4f ? period : 0;
        new ReadOnlySpan<byte>(properties + Vk.CacheUuidOffset, UuidBytes).CopyTo(_cacheUuid);
    }

    private void OpenCache()
    {
        var data = ShaderCache.Pipelines(_vendor, _model);
        if (data is not null && !Matches(data)) data = null;
        ulong cache;
        fixed (byte* initial = data)
        {
            var info = new Vk.PipelineCacheInfo
            {
                SType = Vk.PipelineCacheCreateInfo, InitialDataSize = (nuint)(data?.Length ?? 0), InitialData = initial
            };
            if (VkApi.CreatePipelineCache(_device, &info, null, &cache) != Vk.Success) return;
        }

        PipelineCache = cache;
        ShaderCache.PipelineBytes = data?.Length ?? 0;
    }

    private bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes) return false;
        fixed (byte* header = data)
        {
            var words = (uint*)header;
            return words[0] >= HeaderBytes && words[0] <= data.Length && words[1] == HeaderVersionOne &&
                   words[2] == _vendor && words[3] == _model &&
                   new ReadOnlySpan<byte>(header + 16, UuidBytes).SequenceEqual(_cacheUuid);
        }
    }

    // Also written during play off the engine thread once requested pipelines exist (PipelineSet), so a crash keeps
    // them. Reads may run beside pipeline creation (cache not externally synchronized). While a write is queued or
    // running (_keeping 1) asks fold into it; CloseCache waits for it. _keeping set, then _closing read (both full
    // fences, CloseCache the other way round): either this backs out or CloseCache sees the write coming.
    public void KeepCache()
    {
        if (PipelineCache == 0 || !ShaderCache.Active || Volatile.Read(ref _closing) == 1) return;
        if (Interlocked.Exchange(ref _keeping, 1) == 1) return;
        if (Volatile.Read(ref _closing) == 1)
        {
            Volatile.Write(ref _keeping, 0);
            return;
        }

        var cache = PipelineCache;
        _ = Builds.Run(() =>
        {
            try
            {
                Kept(cache);
            }
            finally
            {
                Volatile.Write(ref _keeping, 0); // a throw would stop every later write
            }
        });
    }

    private void Kept(ulong cache)
    {
        nuint size = 0;
        var sized = VkApi.GetPipelineCacheData(_device, cache, &size, null) == Vk.Success && size > 0 && size < int.MaxValue;
        if (!sized || size == _kept) return; // nothing new: a join finishes pipeline sets dozens of times
        var data = new byte[(int)size];
        fixed (byte* bytes = data)
            if (VkApi.GetPipelineCacheData(_device, cache, &size, bytes) != Vk.Success || !Assert(size <= (nuint)data.Length)) return;
        ShaderCache.KeepPipelines(_vendor, _model, size == (nuint)data.Length ? data : data[..(int)size]);
        _kept = size;
    }

    // After every build: none may still use the cache or the device
    private void CloseCache()
    {
        _ = Interlocked.Exchange(ref _closing, 1);
        // a write past the check may not be queued yet, so Builds.Wait would miss it
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _keeping) == 0, TimeSpan.FromSeconds(Builds.LongestSeconds));
        _ = Builds.Wait(Timeout.InfiniteTimeSpan);
        if (PipelineCache == 0) return;
        if (ShaderCache.Active) Kept(PipelineCache);
        VkApi.DestroyPipelineCache(_device, PipelineCache, null);
        PipelineCache = 0;
    }
}

// Komet's own buffers and their memory, made in one place: memory types tried in the caller's order, a device-local one
// the driver refuses for want of room tried again outside device memory (a GPU without resizable BAR has 256 MB of
// CPU-visible device memory), every handle let go on failure.
internal sealed unsafe partial class VulkanDevice
{
    private const int MaxTypeChoices = 4;
    private const int NoType = int.MinValue;

    // Mapped is 0 unless the buffer was asked to be mapped
    public readonly record struct Made(ulong Buffer, ulong Memory, nint Mapped);

    // types: memory property flags each type must have (Want) and must not (Avoid), best first. map: mapped for life.
    // null when made, else why not. file: whose memory it is in VkMemory's report.
    public string? MakeBuffer(ulong size, uint usage, ReadOnlySpan<(uint Want, uint Avoid)> types, bool map, out Made made,
        [CallerFilePath] string file = "")
    {
        made = default;
        if (!Assert(_device != IntPtr.Zero) || !Assert(size > 0) || !Assert(types.Length is > 0 and <= MaxTypeChoices))
            return "no device";
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = size, Usage = usage };
        ulong buffer;
        if (VkApi.CreateBuffer(_device, &info, null, &buffer) != Vk.Success) return "vkCreateBuffer failed";
        Vk.MemoryRequirements needs;
        VkApi.GetBufferRequirements(_device, buffer, &needs);
        var memory = Allocated(needs, types, file, out var why);
        void* mapped = null;
        if (memory != 0 && VkApi.BindBufferMemory(_device, buffer, memory, 0) != Vk.Success) why = "vkBindBufferMemory failed";
        else if (memory != 0 && map && VkApi.MapMemory(_device, memory, 0, needs.Size, 0, &mapped) != Vk.Success)
            why = "vkMapMemory failed";
        if (why.Length > 0)
        {
            VkApi.DestroyBuffer(_device, buffer, null);
            VkApi.FreeMemory(_device, memory, null); // nothing for 0
            return why;
        }

        made = new Made(buffer, memory, (nint)mapped);
        return null;
    }

    // The first type choice that allocates
    private ulong Allocated(Vk.MemoryRequirements needs, ReadOnlySpan<(uint Want, uint Avoid)> types, string file, out string why)
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
                result = Allocation(needs, MemoryType(needs.TypeBits, outside != 0 ? outside : Vk.HostVisible, avoid | Vk.DeviceLocal),
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
        var result = VkApi.AllocateMemoryFor(_device, &allocate, null, &made, file);
        if (result == Vk.Success) memory = made;
        return result;
    }
}

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
        if (VkApi.EnumeratePhysicalDevices(_instance, &count, devices) < 0) return "no Vulkan devices";
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
        (_physical, Name) = (devices[best], Marshal.PtrToStringUTF8((IntPtr)(properties + Vk.DeviceNameOffset)) ?? "");
        Picked = How(score);
        var api = *(uint*)(properties + ApiVersionOffset); // variant bits 0 for Vulkan itself: compares as a number
        if (api < Vk.ApiVersion13)
            return $"{Name} offers Vulkan {(api >> 22) & 0x7F}.{(api >> 12) & 0x3FF}, and Komet's renderer needs Vulkan 1.3 " +
                   "(a newer driver may offer it)";
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
            lower.Contains("ati ", StringComparison.Ordinal)) return 0x1002;
        return lower.Contains("intel", StringComparison.Ordinal) ? 0x8086u : 0u;
    }

    private string? Queues()
    {
        var families = stackalloc Vk.QueueFamily[MaxFamilies];
        var count = (uint)MaxFamilies;
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

        return "no queue family does graphics and compute";
    }
}

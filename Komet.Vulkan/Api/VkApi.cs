using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// Device functions via vkGetDeviceProcAddr (no loader dispatch). Nothing linked: without Vulkan Load returns false.
internal static unsafe partial class VkApi
{
    private const int MaxName = 128;
    private static readonly string[] Libraries =
        ["libvulkan.so.1", "libvulkan.so", "vulkan-1.dll", "libvulkan.1.dylib"];

    private static IntPtr _library;
    private static delegate* unmanaged<IntPtr, byte*, IntPtr> _instanceProc;
    private static delegate* unmanaged<IntPtr, byte*, IntPtr> _deviceProc;

    public static delegate* unmanaged<Vk.InstanceInfo*, void*, IntPtr*, int> CreateInstance { get; private set; }
    public static delegate* unmanaged<IntPtr, void*, void> DestroyInstance { get; private set; }
    public static delegate* unmanaged<IntPtr, uint*, IntPtr*, int> EnumeratePhysicalDevices { get; private set; }
    public static delegate* unmanaged<IntPtr, byte*, void> GetPhysicalDeviceProperties2 { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.Features2*, void> GetPhysicalDeviceFeatures2 { get; private set; }
    public static delegate* unmanaged<IntPtr, uint*, Vk.QueueFamily*, void> GetQueueFamilies { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.MemoryProperties*, void> GetMemoryProperties { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.DeviceInfo*, void*, IntPtr*, int> CreateDevice { get; private set; }
    public static delegate* unmanaged<IntPtr, byte*, uint*, Vk.ExtensionProperties*, int> EnumerateDeviceExtensions { get; private set; }

    public static delegate* unmanaged<IntPtr, void*, void> DestroyDevice { get; private set; }
    public static delegate* unmanaged<IntPtr, int> DeviceWaitIdle { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void> GetDeviceQueue { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.PoolInfo*, void*, ulong*, int> CreateCommandPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyCommandPool { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.CommandBufferInfo*, IntPtr*, int> AllocateCommandBuffers { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.BeginInfo*, int> BeginCommandBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, int> EndCommandBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, Vk.ImageBarrier*, void>
        CmdPipelineBarrier { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, float*, uint, Vk.ColorRange*, void> CmdClearColorImage { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, Vk.Submit*, ulong, int> QueueSubmit { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int> CreateFence { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyFence { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int> WaitForFences { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong*, int> ResetFences { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ImageInfo*, void*, ulong*, int> CreateImage { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyImage { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void> GetImageMemoryRequirements { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, int> BindImageMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int> CreateSemaphore { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySemaphore { get; private set; }
    private static delegate* unmanaged<IntPtr, Vk.AllocateInfo*, void*, ulong*, int> _allocateMemory;
    private static delegate* unmanaged<IntPtr, ulong, void*, void> _freeMemory;

    public static int AllocateMemory(IntPtr device, Vk.AllocateInfo* info, void* allocator, ulong* memory,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        AllocateMemoryFor(device, info, allocator, memory, file);

    // file: the source file whose memory it is (VkMemory's owners)
    public static int AllocateMemoryFor(IntPtr device, Vk.AllocateInfo* info, void* allocator, ulong* memory, string file)
    {
        if (!Assert(_allocateMemory != null) || !Assert(info != null && memory != null)) return -1;
        VulkanWatch.Mark($"vkAllocateMemory {info->Size >> 20} MB for {Path.GetFileNameWithoutExtension(file)}");
        var result = _allocateMemory(device, info, allocator, memory);
        if (result == Vk.Success) VkMemory.Allocated(*memory, info->Size, file);
        return result;
    }

    public static void FreeMemory(IntPtr device, ulong memory, void* allocator)
    {
        if (!Assert(_freeMemory != null) || memory == 0) return;
        VkMemory.Freed(memory);
        _freeMemory(device, memory, allocator);
    }

    public static bool Load()
    {
        if (_library != IntPtr.Zero) return true;
        foreach (var name in Libraries.Bounded(Libraries.Length))
            if (NativeLibrary.TryLoad(name, out _library)) break;
        if (_library == IntPtr.Zero || !NativeLibrary.TryGetExport(_library, "vkGetInstanceProcAddr", out var proc))
            return false;
        _instanceProc = (delegate* unmanaged<IntPtr, byte*, IntPtr>)proc;
        CreateInstance =
            (delegate* unmanaged<Vk.InstanceInfo*, void*, IntPtr*, int>)Instance(IntPtr.Zero, "vkCreateInstance");
        return CreateInstance != null;
    }

    public static bool LoadInstance(IntPtr instance)
    {
        DestroyInstance = (delegate* unmanaged<IntPtr, void*, void>)Instance(instance, "vkDestroyInstance");
        EnumeratePhysicalDevices =
            (delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Instance(instance, "vkEnumeratePhysicalDevices");
        GetPhysicalDeviceProperties2 =
            (delegate* unmanaged<IntPtr, byte*, void>)Instance(instance, "vkGetPhysicalDeviceProperties2");
        GetPhysicalDeviceFeatures2 =
            (delegate* unmanaged<IntPtr, Vk.Features2*, void>)Instance(instance, "vkGetPhysicalDeviceFeatures2");
        GetQueueFamilies = (delegate* unmanaged<IntPtr, uint*, Vk.QueueFamily*, void>)Instance(instance,
            "vkGetPhysicalDeviceQueueFamilyProperties");
        GetMemoryProperties =
            (delegate* unmanaged<IntPtr, Vk.MemoryProperties*, void>)Instance(instance,
                "vkGetPhysicalDeviceMemoryProperties");
        CreateDevice =
            (delegate* unmanaged<IntPtr, Vk.DeviceInfo*, void*, IntPtr*, int>)Instance(instance, "vkCreateDevice");
        _deviceProc = (delegate* unmanaged<IntPtr, byte*, IntPtr>)Instance(instance, "vkGetDeviceProcAddr");
        EnumerateDeviceExtensions = (delegate* unmanaged<IntPtr, byte*, uint*, Vk.ExtensionProperties*, int>)Instance(
            instance, "vkEnumerateDeviceExtensionProperties");
        LoadSurface(instance);
        LoadFormats(instance);
        return DestroyInstance != null && EnumeratePhysicalDevices != null && GetPhysicalDeviceProperties2 != null &&
               GetPhysicalDeviceFeatures2 != null &&
               GetQueueFamilies != null && GetMemoryProperties != null && CreateDevice != null && _deviceProc != null &&
               EnumerateDeviceExtensions != null;
    }

    public static bool LoadDevice(IntPtr device)
    {
        DestroyDevice = (delegate* unmanaged<IntPtr, void*, void>)Device(device, "vkDestroyDevice");
        DeviceWaitIdle = (delegate* unmanaged<IntPtr, int>)Device(device, "vkDeviceWaitIdle");
        GetDeviceQueue = (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Device(device, "vkGetDeviceQueue");
        CreateCommandPool =
            (delegate* unmanaged<IntPtr, Vk.PoolInfo*, void*, ulong*, int>)Device(device, "vkCreateCommandPool");
        DestroyCommandPool = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyCommandPool");
        AllocateCommandBuffers = (delegate* unmanaged<IntPtr, Vk.CommandBufferInfo*, IntPtr*, int>)Device(device,
            "vkAllocateCommandBuffers");
        BeginCommandBuffer = (delegate* unmanaged<IntPtr, Vk.BeginInfo*, int>)Device(device, "vkBeginCommandBuffer");
        EndCommandBuffer = (delegate* unmanaged<IntPtr, int>)Device(device, "vkEndCommandBuffer");
        CmdPipelineBarrier =
            (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, Vk.ImageBarrier*, void>)
            Device(device, "vkCmdPipelineBarrier");
        CmdClearColorImage = (delegate* unmanaged<IntPtr, ulong, int, float*, uint, Vk.ColorRange*, void>)Device(device,
            "vkCmdClearColorImage");
        QueueSubmit = (delegate* unmanaged<IntPtr, uint, Vk.Submit*, ulong, int>)Device(device, "vkQueueSubmit");
        LoadSwapchain(device);
        return LoadSync(device) && LoadMemory(device) && LoadCompute(device) && LoadGraphics(device) && LoadDraws(device) &&
               LoadQueries(device) && LoadOnly(device) &&
               DestroyDevice != null &&
               DeviceWaitIdle != null &&
               GetDeviceQueue != null && CreateCommandPool != null && DestroyCommandPool != null &&
               AllocateCommandBuffers != null && BeginCommandBuffer != null && EndCommandBuffer != null &&
               CmdPipelineBarrier != null && CmdClearColorImage != null && QueueSubmit != null;
    }

    private static bool LoadSync(IntPtr device)
    {
        CreateFence = (delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int>)Device(device, "vkCreateFence");
        DestroyFence = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyFence");
        WaitForFences = (delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int>)Device(device, "vkWaitForFences");
        ResetFences = (delegate* unmanaged<IntPtr, uint, ulong*, int>)Device(device, "vkResetFences");
        CreateSemaphore =
            (delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int>)Device(device, "vkCreateSemaphore");
        DestroySemaphore = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySemaphore");
        return CreateFence != null && DestroyFence != null && WaitForFences != null && ResetFences != null &&
               CreateSemaphore != null && DestroySemaphore != null;
    }

    private static bool LoadMemory(IntPtr device)
    {
        CreateImage = (delegate* unmanaged<IntPtr, Vk.ImageInfo*, void*, ulong*, int>)Device(device, "vkCreateImage");
        DestroyImage = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyImage");
        GetImageMemoryRequirements = (delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void>)Device(device,
            "vkGetImageMemoryRequirements");
        _allocateMemory =
            (delegate* unmanaged<IntPtr, Vk.AllocateInfo*, void*, ulong*, int>)Device(device, "vkAllocateMemory");
        _freeMemory = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkFreeMemory");
        BindImageMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)Device(device, "vkBindImageMemory");
        return CreateImage != null && DestroyImage != null && GetImageMemoryRequirements != null &&
               _allocateMemory != null && _freeMemory != null && BindImageMemory != null;
    }

    private static IntPtr Instance(IntPtr instance, string name) => Proc(_instanceProc, instance, name);

    private static IntPtr Device(IntPtr device, string name) => Proc(_deviceProc, device, name);

    private static IntPtr Proc(delegate* unmanaged<IntPtr, byte*, IntPtr> proc, IntPtr owner, string name)
    {
        if (proc == null) return IntPtr.Zero;
        Span<byte> text = stackalloc byte[MaxName];
        var length = Encoding.ASCII.GetBytes(name, text);
        if (!Assert(length < MaxName)) return IntPtr.Zero;
        text[length] = 0;
        fixed (byte* p = text) return proc(owner, p);
    }
}

// Graphics pipelines and dynamic rendering, draws, blits, clears and copies, queries, and Vulkan-only's commands for mods'
// raw OpenGL (Vk.Draws.cs): each core, the count draws only with drawIndirectCount (null without it: OcclusionCulling
// is suspended then)
internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, ulong, uint, Vk.GraphicsInfo*, void*, ulong*, int> CreateGraphicsPipelines { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.PipelineCacheInfo*, void*, ulong*, int> CreatePipelineCache { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, nuint*, void*, int> GetPipelineCacheData { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyPipelineCache { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.RenderingInfo*, void> CmdBeginRendering { get; private set; }
    public static delegate* unmanaged<IntPtr, void> CmdEndRendering { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, ulong*, ulong*, void> CmdBindVertexBuffers { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, void> CmdBindIndexBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void> CmdDrawIndexedIndirect { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, Vk.Viewport*, void> CmdSetViewport { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, Vk.Rect*, void> CmdSetScissor { get; private set; }

    // Draws, blits, clears and copies
    public static delegate* unmanaged<IntPtr, uint, uint, uint, uint, void> CmdDraw { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void> CmdDrawIndexed { get; private set; }
    public static delegate* unmanaged<IntPtr, float, void> CmdSetLineWidth { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, uint, void*, void> CmdPushConstants { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, ulong, int, uint, Vk.ImageBlit*, uint, void> CmdBlitImage { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, Vk.ClearAttachment*, uint, Vk.ClearRect*, void> CmdClearAttachments { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, ulong, int, uint, Vk.ImageCopy*, void> CmdCopyImage { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, ulong, uint, Vk.BufferImageCopy*, void> CmdCopyImageToBuffer { get; private set; }
    // Core in 1.2, needs the drawIndirectCount feature
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void> CmdDrawIndexedIndirectCount { get; private set; }

    // Occlusion, timestamp and statistics queries
    public static delegate* unmanaged<IntPtr, Vk.QueryPoolInfo*, void*, ulong*, int> CreateQueryPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyQueryPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, void> CmdResetQueryPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, void> CmdBeginQuery { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, void> CmdEndQuery { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong, uint, void> CmdWriteTimestamp { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, nuint, void*, ulong, uint, int> GetQueryPoolResults { get; private set; }

    // Vulkan-only: what mods' raw OpenGL needs
    public static delegate* unmanaged<IntPtr, uint, Vk.FormatProperties*, void> GetFormatProperties { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilCompareMask { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilWriteMask { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilReference { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void> CmdDrawIndirect { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void> CmdDrawIndirectCount { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, Vk.DepthStencilValue*, uint, Vk.ColorRange*, void>
        CmdClearDepthStencilImage { get; private set; }

    private static bool LoadGraphics(IntPtr device)
    {
        CreateGraphicsPipelines =
            (delegate* unmanaged<IntPtr, ulong, uint, Vk.GraphicsInfo*, void*, ulong*, int>)Device(device,
                "vkCreateGraphicsPipelines");
        CreatePipelineCache = (delegate* unmanaged<IntPtr, Vk.PipelineCacheInfo*, void*, ulong*, int>)Device(device,
            "vkCreatePipelineCache");
        GetPipelineCacheData =
            (delegate* unmanaged<IntPtr, ulong, nuint*, void*, int>)Device(device, "vkGetPipelineCacheData");
        DestroyPipelineCache = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyPipelineCache");
        CmdBeginRendering = (delegate* unmanaged<IntPtr, Vk.RenderingInfo*, void>)Device(device, "vkCmdBeginRendering");
        CmdEndRendering = (delegate* unmanaged<IntPtr, void>)Device(device, "vkCmdEndRendering");
        CmdBindVertexBuffers = (delegate* unmanaged<IntPtr, uint, uint, ulong*, ulong*, void>)Device(device,
            "vkCmdBindVertexBuffers");
        CmdBindIndexBuffer = (delegate* unmanaged<IntPtr, ulong, ulong, uint, void>)Device(device,
            "vkCmdBindIndexBuffer");
        CmdDrawIndexedIndirect = (delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void>)Device(device,
            "vkCmdDrawIndexedIndirect");
        CmdSetViewport = (delegate* unmanaged<IntPtr, uint, uint, Vk.Viewport*, void>)Device(device,
            "vkCmdSetViewport");
        CmdSetScissor = (delegate* unmanaged<IntPtr, uint, uint, Vk.Rect*, void>)Device(device, "vkCmdSetScissor");
        return CreateGraphicsPipelines != null && CreatePipelineCache != null && GetPipelineCacheData != null &&
               DestroyPipelineCache != null && CmdBeginRendering != null && CmdEndRendering != null &&
               CmdBindVertexBuffers != null && CmdBindIndexBuffer != null && CmdDrawIndexedIndirect != null &&
               CmdSetViewport != null && CmdSetScissor != null;
    }

    private static bool LoadDraws(IntPtr device)
    {
        CmdDrawIndexedIndirectCount = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void>)Device(
            device, "vkCmdDrawIndexedIndirectCount");
        CmdDraw = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)Device(device, "vkCmdDraw");
        CmdDrawIndexed = (delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)Device(device,
            "vkCmdDrawIndexed");
        CmdSetLineWidth = (delegate* unmanaged<IntPtr, float, void>)Device(device, "vkCmdSetLineWidth");
        CmdPushConstants = (delegate* unmanaged<IntPtr, ulong, uint, uint, uint, void*, void>)Device(device,
            "vkCmdPushConstants");
        CmdBlitImage = (delegate* unmanaged<IntPtr, ulong, int, ulong, int, uint, Vk.ImageBlit*, uint, void>)Device(
            device, "vkCmdBlitImage");
        CmdClearAttachments = (delegate* unmanaged<IntPtr, uint, Vk.ClearAttachment*, uint, Vk.ClearRect*, void>)Device(
            device, "vkCmdClearAttachments");
        CmdCopyImage = (delegate* unmanaged<IntPtr, ulong, int, ulong, int, uint, Vk.ImageCopy*, void>)Device(device,
            "vkCmdCopyImage");
        CmdCopyImageToBuffer = (delegate* unmanaged<IntPtr, ulong, int, ulong, uint, Vk.BufferImageCopy*, void>)Device(
            device, "vkCmdCopyImageToBuffer");
        return CmdDraw != null && CmdDrawIndexed != null && CmdSetLineWidth != null && CmdPushConstants != null &&
               CmdBlitImage != null && CmdClearAttachments != null && CmdCopyImage != null && CmdCopyImageToBuffer != null;
    }

    private static bool LoadQueries(IntPtr device)
    {
        CreateQueryPool = (delegate* unmanaged<IntPtr, Vk.QueryPoolInfo*, void*, ulong*, int>)Device(device,
            "vkCreateQueryPool");
        DestroyQueryPool = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyQueryPool");
        CmdResetQueryPool = (delegate* unmanaged<IntPtr, ulong, uint, uint, void>)Device(device, "vkCmdResetQueryPool");
        CmdBeginQuery = (delegate* unmanaged<IntPtr, ulong, uint, uint, void>)Device(device, "vkCmdBeginQuery");
        CmdEndQuery = (delegate* unmanaged<IntPtr, ulong, uint, void>)Device(device, "vkCmdEndQuery");
        CmdWriteTimestamp = (delegate* unmanaged<IntPtr, uint, ulong, uint, void>)Device(device, "vkCmdWriteTimestamp");
        GetQueryPoolResults = (delegate* unmanaged<IntPtr, ulong, uint, uint, nuint, void*, ulong, uint, int>)Device(
            device, "vkGetQueryPoolResults");
        return CreateQueryPool != null && DestroyQueryPool != null && CmdResetQueryPool != null &&
               CmdBeginQuery != null && CmdEndQuery != null && GetQueryPoolResults != null && CmdWriteTimestamp != null;
    }

    private static void LoadFormats(IntPtr instance)
    {
        GetFormatProperties = (delegate* unmanaged<IntPtr, uint, Vk.FormatProperties*, void>)Instance(instance,
            "vkGetPhysicalDeviceFormatProperties");
    }

    private static bool LoadOnly(IntPtr device)
    {
        CmdSetStencilCompareMask = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilCompareMask");
        CmdSetStencilWriteMask = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilWriteMask");
        CmdSetStencilReference = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilReference");
        CmdDrawIndirect = (delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void>)Device(device, "vkCmdDrawIndirect");
        CmdDrawIndirectCount = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void>)Device(device,
            "vkCmdDrawIndirectCount");
        CmdClearDepthStencilImage = (delegate* unmanaged<IntPtr, ulong, int, Vk.DepthStencilValue*, uint, Vk.ColorRange*, void>)
            Device(device, "vkCmdClearDepthStencilImage");
        return CmdDrawIndirect != null && CmdSetStencilCompareMask != null && CmdSetStencilWriteMask != null &&
               CmdSetStencilReference != null && CmdClearDepthStencilImage != null;
    }
}

// Buffers, memory maps, image views, samplers, shader modules, layouts, compute pipelines and dispatches, push
// descriptors, fills and copies
internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, Vk.BufferInfo*, void*, ulong*, int> CreateBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void> GetBufferRequirements { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, int> BindBufferMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int> MapMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ViewInfo*, void*, ulong*, int> CreateImageView { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyImageView { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.SamplerInfo*, void*, ulong*, int> CreateSampler { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySampler { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ModuleInfo*, void*, ulong*, int> CreateShaderModule { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyShaderModule { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.SetLayoutInfo*, void*, ulong*, int> CreateSetLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySetLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.PipelineLayoutInfo*, void*, ulong*, int> CreatePipelineLayout { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyPipelineLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, Vk.ComputeInfo*, void*, ulong*, int> CreateComputePipelines { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyPipeline { get; private set; }
    public static delegate* unmanaged<IntPtr, int, ulong, void> CmdBindPipeline { get; private set; }
    public static delegate* unmanaged<IntPtr, int, ulong, uint, uint, Vk.WriteInfo*, void> CmdPushDescriptors { get; private set; }

    public static delegate* unmanaged<IntPtr, uint, uint, uint, void> CmdDispatch { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void> CmdFillBuffer { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, Vk.BufferCopy*, void> CmdCopyBuffer { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, ulong, int, uint, Vk.BufferImageCopy*, void> CmdCopyBufferToImage { get; private set; }

    private static bool LoadCompute(IntPtr device)
    {
        CreateBuffer =
            (delegate* unmanaged<IntPtr, Vk.BufferInfo*, void*, ulong*, int>)Device(device, "vkCreateBuffer");
        DestroyBuffer = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyBuffer");
        GetBufferRequirements = (delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void>)Device(device,
            "vkGetBufferMemoryRequirements");
        BindBufferMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)Device(device, "vkBindBufferMemory");
        MapMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int>)Device(device, "vkMapMemory");
        CreateImageView =
            (delegate* unmanaged<IntPtr, Vk.ViewInfo*, void*, ulong*, int>)Device(device, "vkCreateImageView");
        DestroyImageView = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyImageView");
        CreateSampler =
            (delegate* unmanaged<IntPtr, Vk.SamplerInfo*, void*, ulong*, int>)Device(device, "vkCreateSampler");
        DestroySampler = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySampler");
        CreateShaderModule =
            (delegate* unmanaged<IntPtr, Vk.ModuleInfo*, void*, ulong*, int>)Device(device, "vkCreateShaderModule");
        DestroyShaderModule = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyShaderModule");
        return LoadPipelines(device) && CreateBuffer != null && DestroyBuffer != null &&
               GetBufferRequirements != null &&
               BindBufferMemory != null && MapMemory != null && CreateImageView != null && DestroyImageView != null &&
               CreateSampler != null && DestroySampler != null && CreateShaderModule != null &&
               DestroyShaderModule != null;
    }

    private static bool LoadPipelines(IntPtr device)
    {
        CreateSetLayout =
            (delegate* unmanaged<IntPtr, Vk.SetLayoutInfo*, void*, ulong*, int>)Device(device,
                "vkCreateDescriptorSetLayout");
        DestroySetLayout =
            (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyDescriptorSetLayout");
        CreatePipelineLayout = (delegate* unmanaged<IntPtr, Vk.PipelineLayoutInfo*, void*, ulong*, int>)Device(device,
            "vkCreatePipelineLayout");
        DestroyPipelineLayout =
            (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyPipelineLayout");
        CreateComputePipelines = (delegate* unmanaged<IntPtr, ulong, uint, Vk.ComputeInfo*, void*, ulong*, int>)Device(
            device, "vkCreateComputePipelines");
        DestroyPipeline = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyPipeline");
        CmdBindPipeline = (delegate* unmanaged<IntPtr, int, ulong, void>)Device(device, "vkCmdBindPipeline");
        CmdPushDescriptors = (delegate* unmanaged<IntPtr, int, ulong, uint, uint, Vk.WriteInfo*, void>)Device(device,
            "vkCmdPushDescriptorSetKHR");
        CmdDispatch = (delegate* unmanaged<IntPtr, uint, uint, uint, void>)Device(device, "vkCmdDispatch");
        CmdFillBuffer = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void>)Device(device, "vkCmdFillBuffer");
        CmdCopyBuffer = (delegate* unmanaged<IntPtr, ulong, ulong, uint, Vk.BufferCopy*, void>)Device(device,
            "vkCmdCopyBuffer");
        CmdCopyBufferToImage = (delegate* unmanaged<IntPtr, ulong, ulong, int, uint, Vk.BufferImageCopy*, void>)Device(
            device, "vkCmdCopyBufferToImage");
        return CreateSetLayout != null && DestroySetLayout != null && CreatePipelineLayout != null &&
               DestroyPipelineLayout != null && CreateComputePipelines != null && DestroyPipeline != null &&
               CmdBindPipeline != null && CmdPushDescriptors != null && CmdDispatch != null && CmdFillBuffer != null &&
               CmdCopyBufferToImage != null && CmdCopyBuffer != null;
    }
}

// Null where the extensions are off; VulkanDevice's Surfaces and Swapchains say whether they are on
internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<byte*, uint*, Vk.ExtensionProperties*, int> EnumerateInstanceExtensions { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.XlibSurfaceInfo*, void*, ulong*, int> CreateXlibSurface { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.WaylandSurfaceInfo*, void*, ulong*, int> CreateWaylandSurface { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.Win32SurfaceInfo*, void*, ulong*, int> CreateWin32Surface { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySurface { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong, uint*, int> GetSurfaceSupport { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, Vk.SurfaceCapabilities*, int> GetSurfaceCapabilities { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, uint*, Vk.SurfaceFormat*, int> GetSurfaceFormats { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint*, uint*, int> GetSurfacePresentModes { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.SwapchainInfo*, void*, ulong*, int> CreateSwapchain { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySwapchain { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint*, ulong*, int> GetSwapchainImages { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint*, int> AcquireNextImage { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.PresentInfo*, int> QueuePresent { get; private set; }

    public static bool InstanceOffers(string extension)
    {
        if (!Assert(extension.Length < Vk.ExtensionNameBytes) || _instanceProc == null) return false;
        if (EnumerateInstanceExtensions == null)
            EnumerateInstanceExtensions = (delegate* unmanaged<byte*, uint*, Vk.ExtensionProperties*, int>)Instance(
                IntPtr.Zero, "vkEnumerateInstanceExtensionProperties");
        if (EnumerateInstanceExtensions == null) return false;
        var count = 0u;
        _ = EnumerateInstanceExtensions(null, &count, null);
        var properties = new Vk.ExtensionProperties[Math.Min(count, 512)];
        count = (uint)properties.Length;
        fixed (Vk.ExtensionProperties* p = properties) _ = EnumerateInstanceExtensions(null, &count, p);
        return Lists(properties, count, extension);
    }

    private const int MaxListed = 1024;

    internal static bool Lists(Vk.ExtensionProperties[] properties, uint count, string extension)
    {
        if (!Assert(count <= properties.Length) || !Assert(extension.Length > 0)) return false;
        for (var i = 0; i < Math.Min(Math.Min((int)count, properties.Length), MaxListed); i++)
            fixed (byte* name = properties[i].Name)
                if (System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)name) == extension)
                    return true;
        return false;
    }

    private static void LoadSurface(IntPtr instance)
    {
        CreateXlibSurface = (delegate* unmanaged<IntPtr, Vk.XlibSurfaceInfo*, void*, ulong*, int>)Instance(instance,
            "vkCreateXlibSurfaceKHR");
        CreateWaylandSurface = (delegate* unmanaged<IntPtr, Vk.WaylandSurfaceInfo*, void*, ulong*, int>)Instance(instance,
            "vkCreateWaylandSurfaceKHR");
        CreateWin32Surface = (delegate* unmanaged<IntPtr, Vk.Win32SurfaceInfo*, void*, ulong*, int>)Instance(instance,
            "vkCreateWin32SurfaceKHR");
        DestroySurface = (delegate* unmanaged<IntPtr, ulong, void*, void>)Instance(instance, "vkDestroySurfaceKHR");
        GetSurfaceSupport = (delegate* unmanaged<IntPtr, uint, ulong, uint*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceSupportKHR");
        GetSurfaceCapabilities = (delegate* unmanaged<IntPtr, ulong, Vk.SurfaceCapabilities*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        GetSurfaceFormats = (delegate* unmanaged<IntPtr, ulong, uint*, Vk.SurfaceFormat*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceFormatsKHR");
        GetSurfacePresentModes = (delegate* unmanaged<IntPtr, ulong, uint*, uint*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfacePresentModesKHR");
    }

    private static void LoadSwapchain(IntPtr device)
    {
        CreateSwapchain = (delegate* unmanaged<IntPtr, Vk.SwapchainInfo*, void*, ulong*, int>)Device(device,
            "vkCreateSwapchainKHR");
        DestroySwapchain = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySwapchainKHR");
        GetSwapchainImages = (delegate* unmanaged<IntPtr, ulong, uint*, ulong*, int>)Device(device,
            "vkGetSwapchainImagesKHR");
        AcquireNextImage = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint*, int>)Device(device,
            "vkAcquireNextImageKHR");
        QueuePresent = (delegate* unmanaged<IntPtr, Vk.PresentInfo*, int>)Device(device, "vkQueuePresentKHR");
    }

    public static bool Presents =>
        (CreateXlibSurface != null || CreateWaylandSurface != null || CreateWin32Surface != null) && DestroySurface != null && GetSurfaceSupport != null &&
        GetSurfaceCapabilities != null && GetSurfaceFormats != null && GetSurfacePresentModes != null &&
        CreateSwapchain != null && DestroySwapchain != null && GetSwapchainImages != null && AcquireNextImage != null &&
        QueuePresent != null;
}

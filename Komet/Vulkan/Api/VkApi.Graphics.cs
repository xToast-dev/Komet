namespace Komet.Vulkan;

internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, byte*, uint*, Vk.ExtensionProperties*, int> EnumerateDeviceExtensions { get; private set; }

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

    private static bool LoadGraphics(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
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
        return Assert(CreateGraphicsPipelines != null) && CreatePipelineCache != null && GetPipelineCacheData != null &&
               DestroyPipelineCache != null && CmdBeginRendering != null && CmdEndRendering != null &&
               CmdBindVertexBuffers != null && CmdBindIndexBuffer != null && CmdDrawIndexedIndirect != null &&
               CmdSetViewport != null && CmdSetScissor != null;
    }
}

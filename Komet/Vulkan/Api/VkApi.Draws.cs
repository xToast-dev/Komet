namespace Komet.Vulkan;

internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, uint, uint, uint, uint, void> CmdDraw { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void> CmdDrawIndexed { get; private set; }
    public static delegate* unmanaged<IntPtr, float, void> CmdSetLineWidth { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, uint, void*, void> CmdPushConstants { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, int, ulong, int, uint, Vk.ImageBlit*, uint, void> CmdBlitImage { get; private set; }

    public static delegate* unmanaged<IntPtr, uint, Vk.ClearAttachment*, uint, Vk.ClearRect*, void> CmdClearAttachments { get; private set; }

    // Core in 1.2, and only with the device's drawIndirectCount feature; null where it is missing (OcclusionCulling stays
    // with OpenGL then)
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void> CmdDrawIndexedIndirectCount { get; private set; }

    private static bool LoadDraws(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
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
        return Assert(CmdDraw != null) && CmdDrawIndexed != null && CmdSetLineWidth != null && CmdPushConstants != null &&
               CmdBlitImage != null && CmdClearAttachments != null;
    }
}

namespace Komet.Vulkan;

internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, Vk.QueryPoolInfo*, void*, ulong*, int> CreateQueryPool { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyQueryPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, void> CmdResetQueryPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, uint, void> CmdBeginQuery { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, void> CmdEndQuery { get; private set; }

    public static delegate* unmanaged<IntPtr, uint, ulong, uint, void> CmdWriteTimestamp { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, uint, uint, nuint, void*, ulong, uint, int> GetQueryPoolResults { get; private set; }

    private static bool LoadQueries(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CreateQueryPool = (delegate* unmanaged<IntPtr, Vk.QueryPoolInfo*, void*, ulong*, int>)Device(device,
            "vkCreateQueryPool");
        DestroyQueryPool = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyQueryPool");
        CmdResetQueryPool = (delegate* unmanaged<IntPtr, ulong, uint, uint, void>)Device(device, "vkCmdResetQueryPool");
        CmdBeginQuery = (delegate* unmanaged<IntPtr, ulong, uint, uint, void>)Device(device, "vkCmdBeginQuery");
        CmdEndQuery = (delegate* unmanaged<IntPtr, ulong, uint, void>)Device(device, "vkCmdEndQuery");
        CmdWriteTimestamp = (delegate* unmanaged<IntPtr, uint, ulong, uint, void>)Device(device, "vkCmdWriteTimestamp");
        GetQueryPoolResults = (delegate* unmanaged<IntPtr, ulong, uint, uint, nuint, void*, ulong, uint, int>)Device(
            device, "vkGetQueryPoolResults");
        return Assert(CreateQueryPool != null) && DestroyQueryPool != null && CmdResetQueryPool != null &&
               CmdBeginQuery != null && CmdEndQuery != null && GetQueryPoolResults != null && CmdWriteTimestamp != null;
    }
}

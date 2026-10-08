namespace Komet.Vulkan;

// Null where the extensions are off; VulkanDevice's Surfaces and Swapchains say whether they are on
internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<byte*, uint*, Vk.ExtensionProperties*, int> EnumerateInstanceExtensions { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.XlibSurfaceInfo*, void*, ulong*, int> CreateXlibSurface { get; private set; }

    public static delegate* unmanaged<IntPtr, Vk.WaylandSurfaceInfo*, void*, ulong*, int> CreateWaylandSurface { get; private set; }

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
        _ = Assert(properties.Length <= 512);
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
        if (!Assert(instance != IntPtr.Zero)) return;
        CreateXlibSurface = (delegate* unmanaged<IntPtr, Vk.XlibSurfaceInfo*, void*, ulong*, int>)Instance(instance,
            "vkCreateXlibSurfaceKHR");
        CreateWaylandSurface = (delegate* unmanaged<IntPtr, Vk.WaylandSurfaceInfo*, void*, ulong*, int>)Instance(instance,
            "vkCreateWaylandSurfaceKHR");
        DestroySurface = (delegate* unmanaged<IntPtr, ulong, void*, void>)Instance(instance, "vkDestroySurfaceKHR");
        GetSurfaceSupport = (delegate* unmanaged<IntPtr, uint, ulong, uint*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceSupportKHR");
        GetSurfaceCapabilities = (delegate* unmanaged<IntPtr, ulong, Vk.SurfaceCapabilities*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        GetSurfaceFormats = (delegate* unmanaged<IntPtr, ulong, uint*, Vk.SurfaceFormat*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfaceFormatsKHR");
        GetSurfacePresentModes = (delegate* unmanaged<IntPtr, ulong, uint*, uint*, int>)Instance(instance,
            "vkGetPhysicalDeviceSurfacePresentModesKHR");
        _ = Assert(_instanceProc != null);
    }

    private static void LoadSwapchain(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return;
        CreateSwapchain = (delegate* unmanaged<IntPtr, Vk.SwapchainInfo*, void*, ulong*, int>)Device(device,
            "vkCreateSwapchainKHR");
        DestroySwapchain = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySwapchainKHR");
        GetSwapchainImages = (delegate* unmanaged<IntPtr, ulong, uint*, ulong*, int>)Device(device,
            "vkGetSwapchainImagesKHR");
        AcquireNextImage = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint*, int>)Device(device,
            "vkAcquireNextImageKHR");
        QueuePresent = (delegate* unmanaged<IntPtr, Vk.PresentInfo*, int>)Device(device, "vkQueuePresentKHR");
        _ = Assert(_deviceProc != null);
    }

    public static bool Presents =>
        (CreateXlibSurface != null || CreateWaylandSurface != null) && DestroySurface != null && GetSurfaceSupport != null &&
        GetSurfaceCapabilities != null && GetSurfaceFormats != null && GetSurfacePresentModes != null &&
        CreateSwapchain != null && DestroySwapchain != null && GetSwapchainImages != null && AcquireNextImage != null &&
        QueuePresent != null;
}

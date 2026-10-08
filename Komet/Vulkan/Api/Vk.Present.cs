
namespace Komet.Vulkan;

internal static partial class Vk
{
    public const int XlibSurfaceCreateInfo = 1000004000, SwapchainCreateInfo = 1000001000, PresentInfoType = 1000001001;
    public const int WaylandSurfaceCreateInfo = 1000006000;
    public const string WaylandSurfaceExtension = "VK_KHR_wayland_surface";
    public const int LayoutPresentSrc = 1000001002, Suboptimal = 1000001003, OutOfDate = -1000001004;
    public const uint PresentImmediate = 0, PresentMailbox = 1, PresentFifo = 2, PresentFifoRelaxed = 3;
    public const uint FormatBgra8 = 44, ColorSpaceSrgbNonlinear = 0, CompositeOpaque = 1;
    public const string SurfaceExtension = "VK_KHR_surface", XlibSurfaceExtension = "VK_KHR_xlib_surface";
    public const string SwapchainExtension = "VK_KHR_swapchain";

    // VkXlibSurfaceCreateInfoKHR: the X11 display and window (an XID, unsigned long)
    public unsafe struct XlibSurfaceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public IntPtr Display;
        public nuint Window;
    }

    // VkWaylandSurfaceCreateInfoKHR: the Wayland display and surface (wl_display*, wl_surface*)
    public unsafe struct WaylandSurfaceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public IntPtr Display;
        public IntPtr Surface;
    }

    public struct SurfaceCapabilities
    {
        public uint MinImageCount, MaxImageCount;
        public uint CurrentWidth, CurrentHeight, MinWidth, MinHeight, MaxWidth, MaxHeight;
        public uint MaxArrayLayers, SupportedTransforms, CurrentTransform, SupportedCompositeAlpha, SupportedUsage;
    }

    public struct SurfaceFormat { public uint Format, ColorSpace; }

    public unsafe struct SwapchainInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Surface;
        public uint MinImageCount, Format, ColorSpace, Width, Height, ArrayLayers, Usage, Sharing, FamilyCount;
        public uint* Families;
        public uint Transform, CompositeAlpha, PresentMode, Clipped;
        public ulong OldSwapchain;
    }

    public unsafe struct PresentInfo
    {
        public int SType; public void* Next;
        public uint WaitCount;
        public ulong* Waits;
        public uint SwapchainCount;
        public ulong* Swapchains;
        public uint* Indices;
        public int* Results;
    }
}

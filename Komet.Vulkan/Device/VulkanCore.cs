using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// The Vulkan device, started on the first frame while Vulkan is on (VulkanRenderer.Only). Nothing is shared with
// OpenGL: the device is OpenGL's GPU by GL_DEVICE_UUID_EXT where offered, else by GL's vendor and renderer names. Off:
// nothing loaded.
internal static class VulkanCore
{
    private static ILogger? _logger;
    private static bool _tried;

    public static bool Enabled => VulkanRenderer.Only;
    public static string Status { get; private set; } = "";
    public static VulkanDevice? Device { get; private set; }

    public static bool Failed => _tried && Device is null;

    public static void Install(ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        _logger = logger;
        api.Event.RegisterRenderer(new Hook(), EnumRenderStage.Before, "komet-vulkan");
    }

    // The world closes: the device goes, the switch stays
    public static void Stop()
    {
        var device = Device;
        (Device, _tried, Status) = (null, false, ""); // first: a throwing Dispose must not leave it for the next world
        device?.Dispose();
    }

    // On the render thread, with the GL context current
    internal static void Start()
    {
        if (_tried || !Assert(_logger is not null)) return;
        _tried = true;
        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        ReadOnlySpan<byte> named = GlInterop.Uuid(uuid) ? uuid : default; // names its GPU exactly
        var device = VulkanDevice.Create(named, (GL.GetString(StringName.Vendor), GL.GetString(StringName.Renderer)),
            out var why);
        if (device is null)
        {
            Report(why);
            return;
        }

        Device = device;
        Report($"{device.Name} (picked by {device.Picked}): Vulkan-only, nothing shared with OpenGL");
    }

    private static void Report(string status)
    {
        Status = status.Length > 0 ? status : "?";
        _logger?.Notification("Komet: Vulkan {0}", Status);
    }

    private sealed class Hook : IRenderer
    {
        public double RenderOrder => 0;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Enabled && !_tried) Start();
        }

        public void Dispose() { }
    }
}

// The one thing Vulkan asks OpenGL's sharing extensions: GL_DEVICE_UUID_EXT, the GPU the context runs on, via any
// extension defining it (GL_EXT_memory_object or GL_EXT_semaphore). Needs a current context.
internal static unsafe class GlInterop
{
    private const int DeviceUuid = 0x9597;

    private static readonly string[] Defining = ["GL_EXT_memory_object", "GL_EXT_semaphore"];

    private static delegate* unmanaged<uint, uint, byte*, void> _getUnsignedBytei;

    // The GPU the context runs on, as Vulkan's VkPhysicalDeviceIDProperties names it; false if no extension offers it
    public static bool Uuid(Span<byte> uuid)
    {
        if (!Assert(uuid.Length == VulkanDevice.UuidBytes)) return false;
        if (_getUnsignedBytei == null && Array.Exists(Defining, Diagnostics.GpuStats.Offered))
            _getUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)GLFW.GetProcAddress("glGetUnsignedBytei_vEXT");
        if (_getUnsignedBytei == null) return false;
        fixed (byte* bytes = uuid) _getUnsignedBytei(DeviceUuid, 0, bytes);
        return uuid.IndexOfAnyExcept((byte)0) >= 0;
    }
}

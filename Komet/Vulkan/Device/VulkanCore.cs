using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Proves that a Vulkan device on OpenGL's GPU shares with it on this machine: Vulkan clears an image both see and hands it over with
// a semaphore, OpenGL reads the color back. Off, nothing is loaded.
internal static unsafe class VulkanCore
{
    private const int Proof = 4;
    private static readonly (float R, float G, float B, float A) Color = (0.25f, 0.5f, 0.75f, 1f);

    private static ILogger? _logger;
    private static Hook? _hook;
    private static bool _tried;

    public static bool Enabled { get; set; }
    public static string Status { get; private set; } = "";
    public static VulkanDevice? Device { get; private set; }

    public static bool Failed => _tried && Device is null;

    public static void Install(ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        _logger = logger;
        _hook = new Hook();
        api.Event.RegisterRenderer(_hook, EnumRenderStage.Before, "komet-vulkan");
    }

    // The world closes: the device goes, the switch stays
    public static void Stop()
    {
        _ = Assert(Device is null || _tried); // a device only comes from a start
        Device?.Dispose();
        (Device, _tried, Status) = (null, false, "");
    }

    // On the render thread, with the GL context current
    internal static void Start()
    {
        if (_tried || !Assert(Device is null) || !Assert(_logger is not null)) return;
        _tried = true;
        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        if (!GlInterop.Load() || !GlInterop.Uuid(uuid))
        {
            Report("OpenGL cannot share memory and semaphores (GL_EXT_memory_object, GL_EXT_semaphore)");
            return;
        }

        var device = VulkanDevice.Create(uuid, out var why);
        if (device is null)
        {
            Report(why);
            return;
        }

        var keep = false;
        try
        {
            if (!Assert(device.Handle != IntPtr.Zero)) return;
            var proof = Prove(device, out _);
            Report(device.Name + ": " + (proof.Length == 0 ? "shares with OpenGL" : proof));
            keep = proof.Length == 0;
            if (keep) Device = device;
        }
        finally
        {
            if (!keep) device.Dispose();
        }
    }

    internal static string Prove(VulkanDevice device, out byte[] pixel)
    {
        pixel = new byte[4 * Proof * Proof];
        if (!NotNull(device) || !Assert(pixel.Length == 64)) return "no device";
        using var image = SharedImage.Create(device, Proof, Proof, Vk.TransferDst | Vk.Sampled | Vk.ColorAttachment,
            out var why);
        if (image is null) return why;
        using var ready = SharedSemaphore.Create(device);
        if (ready is null) return "no shared semaphore";
        var (target, family) = (image.Image, device.Family);
        if (!device.Run(commands => Clear(commands, target, family), [ready.Vulkan]))
            return "the Vulkan submission failed";
        GlInterop.Wait(ready.Gl, image.Texture, GlInterop.LayoutGeneral);
        GL.GetTextureImage(image.Texture, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.Length, pixel);
        return Near(pixel[0], Color.R) && Near(pixel[1], Color.G) && Near(pixel[2], Color.B) && Near(pixel[3], Color.A)
            ? ""
            : $"OpenGL read {pixel[0]} {pixel[1]} {pixel[2]} {pixel[3]}, not the color Vulkan cleared to";
    }

    private static void Clear(IntPtr commands, ulong image, uint family)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image != 0)) return;
        var range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = 1, Layers = 1 };
        var toTransfer = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, DstAccess = Vk.AccessTransferWrite, OldLayout = Vk.LayoutUndefined,
            NewLayout = Vk.LayoutTransferDst, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = image, Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.StageTransfer, 0, 0, null, 0, null, 1, &toTransfer);
        var color = stackalloc float[] { Color.R, Color.G, Color.B, Color.A };
        VkApi.CmdClearColorImage(commands, image, Vk.LayoutTransferDst, color, 1, &range);
        var release = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = Vk.AccessTransferWrite, OldLayout = Vk.LayoutTransferDst,
            NewLayout = Vk.LayoutGeneral, SrcFamily = family, DstFamily = Vk.QueueFamilyExternal, Image = image,
            Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageBottom, 0, 0, null, 0, null, 1, &release);
    }

    private static bool Near(byte value, float wanted) =>
        Finite(wanted) && Assert(wanted is >= 0 and <= 1) && Math.Abs(value - wanted * 255) <= 1.5;

    private static void Report(string status)
    {
        Status = NotNull(status) && Assert(status.Length > 0) ? status : "?";
        _logger?.Notification("Komet: Vulkan {0}", Status);
    }

    private sealed class Hook : IRenderer
    {
        public double RenderOrder => 0;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Enabled && !_tried && Assert(stage == EnumRenderStage.Before) && NotNull(_hook)) Start();
        }

        public void Dispose() =>
            _ = Assert(ReferenceEquals(_hook, this) || _hook is null) && Assert(Device is null || _tried);
    }
}

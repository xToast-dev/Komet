namespace Komet.Vulkan;

// The window as a framebuffer object both APIs draw into (Window, 0: none): framebuffer 0 and the window's colour buffers
// (GL_BACK, GL_FRONT...) name it, so the engine's last passes and GUI draw into shared images Vulkan draws into too; Present
// copies it into the real window when OpenGL shows the frame.
internal static unsafe partial class GlTap
{
    private const uint GlReadFramebuffer = 0x8CA8, GlBackLeft = 0x402, GlFront = 0x404, GlFrontLeft = 0x400;
    private const uint GlFrontAndBack = 0x408, GlBackBuffer = 0x405, ColorBit = 0x4000, Nearest = 0x2600;

    private static uint _readFramebuffer;

    // The stand-in framebuffer object; set and cleared by its owner (VulkanRenderer's terrain renderer)
    public static uint Window { get; set; }

    public static int ReadFramebuffer => (int)_readFramebuffer;

    private static uint Framed(uint framebuffer) =>
        Assert(framebuffer != uint.MaxValue) && framebuffer == 0 && Window != 0 ? Window : framebuffer;

    private static uint Windowed(uint buffer)
    {
        _ = Assert(Window != 0) && Assert(buffer != uint.MaxValue);
        return buffer is GlBackLeft or GlFront or GlBackBuffer or GlFrontLeft or GlFrontAndBack ? GlColor0 : buffer;
    }

    // The stand-in copied into the real window past the tap, bindings and scissor test restored (a scissored blit would copy part)
    public static void Present(int width, int height)
    {
        if (Window == 0 || !Assert(_table is not null) || !Assert(width > 0 && height > 0)) return;
        var bind = (delegate* unmanaged<uint, uint, void>)Original[BindFramebufferAt];
        var blit = (delegate* unmanaged<uint, uint, int, int, int, int, int, int, int, int, uint, uint, void>)
            Original[TouchAt + 25];
        var enable = (delegate* unmanaged<uint, void>)Original[EnableAt];
        var disable = (delegate* unmanaged<uint, void>)Original[DisableAt];
        if (!Assert(bind != null && blit != null) || !Assert(enable != null && disable != null)) return;
        disable(GlScissor);
        blit(Window, 0, 0, 0, width, height, 0, 0, width, height, ColorBit, Nearest);
        if ((_caps & Caps.Scissor) != 0) enable(GlScissor);
        bind(GlReadFramebuffer, _readFramebuffer);
        bind(GlDrawFramebuffer, _framebuffer);
    }
}

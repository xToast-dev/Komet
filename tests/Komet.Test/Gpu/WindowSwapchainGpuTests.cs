using Komet.Vulkan;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Test.Gpu;

// OPENTK_4_USE_WAYLAND=1 runs it on Wayland.
[NonParallelizable]
public sealed class WindowSwapchainGpuTests
{
    private const int Frames = 4;

    [Test]
    public void VulkanShowsFramesOnTheWindow()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var chain = Chain(rig);
        var (device, (width, height)) = (rig.Device!, rig.Window!.ClientSize);
        for (var frame = 0; frame < Frames; frame++)
        {
            var vsync = frame < Frames / 2 ? 0 : 1; // off, then on: made anew
            var index = chain.Acquire(width, height, vsync, out var why);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), why);
            var (image, rendered) = chain.Image(index);
            Assert.That(device.Run(commands => Cleared(commands, image, frame), [rendered]), Is.True);
            Assert.That(chain.Present(index), Is.True, $"frame {frame} presented");
        }

        Assert.That((chain.Width, chain.Height), Is.EqualTo((width, height)), "the window's size");
        Assert.That(chain.Format, Is.EqualTo(Vk.FormatBgra8).Or.EqualTo(Vk.FormatRgba8));
    }

    // An image acquired that no present showed (the frame went to OpenGL instead) is what the next acquire gives: acquiring
    // again never waits for an image the presentation engine cannot give back, and the frames after show as before
    [Test]
    public void AnImageAcquiredButNeverPresentedIsAcquiredAgain()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var chain = Chain(rig);
        var (device, (width, height)) = (rig.Device!, rig.Window!.ClientSize);
        var held = chain.Acquire(width, height, 0, out var why);
        Assert.That(held, Is.GreaterThanOrEqualTo(0), why);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var again = 0; again < 2 * Frames; again++)
            Assert.That(chain.Acquire(width, height, 0, out why), Is.EqualTo(held), $"acquired again ({again}): {why}");
        Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500), "no acquire waited for an image");
        for (var frame = 0; frame < Frames; frame++)
        {
            var index = chain.Acquire(width, height, 0, out why);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), why);
            var (image, rendered) = chain.Image(index);
            Assert.That(device.Run(commands => Cleared(commands, image, frame), [rendered]), Is.True);
            Assert.That(chain.Present(index), Is.True, $"frame {frame} presented");
        }
    }

    private static unsafe WindowSwapchain Chain(GpuRig rig)
    {
        var (window, platform) = (rig.Window!, GLFW.GetPlatform());
        if (platform is not (Platform.X11 or Platform.Wayland)) Assert.Ignore($"the test window is on {platform}");
        if (!rig.Device!.Swapchains) Assert.Ignore("this Vulkan has no swapchains for windows");
        rig.Backend!.Flush(); // the device's own command buffer is the test's from here on
        var native = platform == Platform.Wayland
            ? new WindowSwapchain.Native(true, GLFW.GetWaylandDisplay(), (nuint)GLFW.GetWaylandWindow(window.WindowPtr))
            : new WindowSwapchain.Native(false, GLFW.GetX11Display(), GLFW.GetX11Window(window.WindowPtr));
        var chain = WindowSwapchain.Create(rig.Device, native, out var why);
        TestContext.Out.WriteLine($"a swapchain on a {platform} window");
        Assert.That(chain, Is.Not.Null, why);
        return chain!;
    }

    private static unsafe void Cleared(IntPtr commands, ulong image, int frame)
    {
        var range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = 1, Layers = 1 };
        Move(commands, image, range, (Vk.LayoutUndefined, Vk.LayoutTransferDst));
        var color = stackalloc float[] { frame / (float)Frames, 0.5f, 1f - frame / (float)Frames, 1f };
        VkApi.CmdClearColorImage(commands, image, Vk.LayoutTransferDst, color, 1, &range);
        Move(commands, image, range, (Vk.LayoutTransferDst, Vk.LayoutPresentSrc));
    }

    private static unsafe void Move(IntPtr commands, ulong image, Vk.ColorRange range, (int From, int To) layouts)
    {
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = layouts.From,
            NewLayout = layouts.To, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = image, Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }
}

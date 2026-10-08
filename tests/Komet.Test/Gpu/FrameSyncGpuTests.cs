using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// With glthread the engine's GL calls reach the driver later than Komet's Vulkan calls: Vulkan may be handed a submission that
// waits for OpenGL's signal before OpenGL has sent that signal off. RADV takes it and holds the caller until the signal is on its
// way, then runs it. (The other way round is no option: a GL wait for a Vulkan signal not yet submitted fails in the kernel.)
[NonParallelizable]
public sealed class FrameSyncGpuTests
{
    [Test]
    public void ASubmissionWaitingForOpenGlsSignalWaitsUntilItIsSentAndThenRuns()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        rig.Backend!.Flush();
        using var shared = SharedBuffer.Create(device, 4096, out var why);
        using var ready = SharedSemaphore.Create(device);
        Assert.That(shared is not null && ready is not null, Is.True, why);
        var (commands, fence) = (device.Allocate(), device.Fence(false));
        Assert.That(VulkanDevice.Begin(commands), Is.True);
        shared!.Fill(commands, 0, 4096, 0xABCDu);

        var clock = Stopwatch.StartNew();
        var submitted = Task.Run(() => device.Submit(commands, [ready!.Vulkan], [], fence));
        var blocked = !submitted.Wait(200);
        GlInterop.Signal(ready!.Gl, [(uint)shared!.Gl], [], []);
        GL.Flush();
        Assert.That(submitted.Wait(5000) && submitted.Result, Is.True, "the submission went through");
        TestContext.Out.WriteLine($"submit {(blocked ? "blocked until OpenGL signalled" : "returned at once")}, " +
                                  $"{clock.ElapsedMilliseconds} ms");
        Assert.That(device.Wait(fence, 5_000_000_000), Is.True, "and ran once the signal came");
        Assert.That(blocked, Is.True, "RADV held the submission until OpenGL signalled");
        device.Destroy(fence);
    }
}

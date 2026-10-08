using Komet.Gpu;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;

namespace Komet.Test.Gpu;

// Disposing puts OpenGL back as the backend in use. Ports and pipelines are made on builders as in the game, but the engine's
// thread waits for each as it starts it (Builds.Waited): the frames draw as if all were made in place, which is what the parity
// tests compare; the tests of the builds themselves and the benchmarks switch the waiting off.
internal sealed class GpuRig : IDisposable
{
    public const string Gl = "gl", Vulkan = "vulkan";

    private readonly VulkanDevice? _device;
    private readonly VulkanBackend? _backend;
    private readonly bool _waited = Builds.Waited;

    private GpuRig(VulkanDevice? device, VulkanBackend? backend)
    {
        (_device, _backend) = (device, backend);
        GpuBackends.Current = backend is null ? GlBackend.Instance : backend;
        Builds.Waited = true;
    }

    // A GL context must be current
    public static GpuRig? Start(string backend, out string why)
    {
        why = "";
        if (backend == Gl) return new GpuRig(null, null);
        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        if (!GlInterop.Load() || !GlInterop.Uuid(uuid))
        {
            why = "OpenGL here cannot name its device";
            return null;
        }

        if (!Shaderc.Load())
        {
            why = "no shaderc";
            return null;
        }

        var device = VulkanDevice.Create(uuid, out why);
        return device is null ? null : new GpuRig(device, new VulkanBackend(device, null, null));
    }

    public static GpuRig Open(string backend)
    {
        var window = OcclusionGpuTests.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + OcclusionGpuTests.Why);
        var rig = Start(backend, out var why);
        if (rig is null)
        {
            window.Dispose();
            Assert.Ignore(backend + " cannot run here: " + why);
        }

        rig.Window = window;
        return rig;
    }

    public NativeWindow? Window { get; private set; }
    public VulkanDevice? Device => _device;
    public VulkanBackend? Backend => _backend;

    public int Depth(float depth, int size) =>
        _backend is null ? OcclusionGpuTests.Wall(depth) : _backend.Filled(size, size, depth);

    public uint[] Read(SharedBuffer buffer, ulong offset, int words)
    {
        using var ready = SharedSemaphore.Create(Device!);
        GlInterop.Signal(ready!.Gl, [(uint)buffer.Gl], [], []);
        GL.Flush();
        var backend = Backend!;
        var readback = backend.Buffer();
        backend.Upload<uint>(readback, [], 4 * words);
        backend.Flush(); // the device has one command buffer, which the backend may be recording into
        var (target, at) = backend.Native(readback);
        Assert.That(Device!.Run(c => buffer.Copy(c, offset, target, at, 4UL * (ulong)words), [ready.Vulkan], []), Is.True);
        var read = new uint[words];
        backend.Read(readback, 0, read);
        backend.DeleteBuffer(ref readback);
        return read;
    }

    public void Dispose()
    {
        GpuBackends.Current = GlBackend.Instance;
        _backend?.Dispose();
        _device?.Dispose();
        Window?.Dispose();
        Window = null;
        Builds.Waited = _waited;
    }
}

// The commands the GPU tests record on a shared buffer
internal static unsafe class SharedBufferCommands
{
    public static void Copy(this SharedBuffer buffer, IntPtr commands, ulong offset, ulong target, ulong targetOffset,
        ulong size)
    {
        Assert.That(offset + size, Is.LessThanOrEqualTo(buffer.Size));
        var copy = new Vk.BufferCopy { SourceOffset = offset, TargetOffset = targetOffset, Size = size };
        VkApi.CmdCopyBuffer(commands, buffer.Buffer, target, 1, &copy);
    }

    public static void Fill(this SharedBuffer buffer, IntPtr commands, ulong offset, ulong size, uint value)
    {
        Assert.That(offset + size, Is.LessThanOrEqualTo(buffer.Size));
        VkApi.CmdFillBuffer(commands, buffer.Buffer, offset, size, value);
    }
}

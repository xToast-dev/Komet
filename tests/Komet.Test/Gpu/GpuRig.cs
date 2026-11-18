using Komet.Rendering;
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

    private readonly bool _waited = Builds.Waited;

    private GpuRig(VulkanDevice? device, VulkanBackend? backend)
    {
        (Device, Backend) = (device, backend);
        GpuBackends.Current = backend is null ? GlBackend.Instance : backend;
        Builds.Waited = true;
    }

    public NativeWindow? Window { get; private set; }
    public VulkanDevice? Device { get; }

    public VulkanBackend? Backend { get; }

    public void Dispose()
    {
        GpuBackends.Current = GlBackend.Instance;
        Backend?.Dispose();
        Device?.Dispose();
        Window?.Dispose();
        Window = null;
        Builds.Waited = _waited;
    }

    // A GL context must be current
    public static GpuRig? Start(string backend, out string why)
    {
        why = "";
        if (backend == Gl) return new GpuRig(null, null);

        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        ReadOnlySpan<byte> named = GlInterop.Uuid(uuid) ? uuid : default; // else picked by OpenGL's renderer name
        if (!Shaderc.Load())
        {
            why = "no shaderc";
            return null;
        }

        var device = VulkanDevice.Create(named, (GL.GetString(StringName.Vendor), GL.GetString(StringName.Renderer)),
            out why);
        return device is null ? null : new GpuRig(device, new VulkanBackend(device, null, null));
    }

    public static GpuRig Open(string backend)
    {
        var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);

        var rig = Start(backend, out var why);
        if (rig is null)
        {
            window.Dispose();
            Assert.Ignore(backend + " cannot run here: " + why);
        }

        rig.Window = window;
        return rig;
    }

    public int Depth(float depth, int size) =>
        Backend?.Filled(size, size, depth) ?? GlContext.Wall(depth);
}

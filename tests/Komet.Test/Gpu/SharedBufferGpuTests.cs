using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class SharedBufferGpuTests
{
    private const int Bytes = 1 << 16, At = 4096;

    [Test]
    public void VulkanReadsWhatOpenGlWroteAndOpenGlWhatVulkanWrote()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var shared = SharedBuffer.Create(rig.Device!, Bytes, out var why);
        Assert.That(shared, Is.Not.Null, why);
        using var toGl = SharedSemaphore.Create(rig.Device!);
        Assert.That(toGl, Is.Not.Null);

        var written = Enumerable.Range(0, 256).Select(i => (uint)(i * 2654435761u)).ToArray();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, shared!.Gl);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, At, written.Length * 4, written);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        Assert.That(GL.GetError(), Is.EqualTo(OpenTK.Graphics.OpenGL.ErrorCode.NoError),
            "glBufferSubData on the import");
        Assert.That(rig.Read(shared, At, written.Length), Is.EqualTo(written), "Vulkan saw OpenGL's write");

        Assert.That(rig.Device!.Run(c => shared.Fill(c, 0, 1024, 0xC0FFEEu), [toGl!.Vulkan]), Is.True);
        GlInterop.Wait(toGl.Gl, [(uint)shared.Gl], [], []);
        var back = new uint[256];
        GL.GetNamedBufferSubData(shared.Gl, 0, 1024, back);
        Assert.That(back, Is.All.EqualTo(0xC0FFEEu), "OpenGL saw Vulkan's fill");
    }
}

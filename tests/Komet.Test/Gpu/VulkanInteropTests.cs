using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class VulkanInteropTests
{
    [Test]
    public void OpenGlReadsTheColorVulkanClearedTheSharedImageTo()
    {
        using var window = OcclusionGpuTests.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        if (!GlInterop.Load() || !GlInterop.Uuid(uuid)) Assert.Ignore("OpenGL here cannot share memory with Vulkan");
        using var device = VulkanDevice.Create(uuid, out var why);
        if (device is null && why == "no Vulkan loader") Assert.Ignore(why);
        Assert.That(device, Is.Not.Null, why);
        var proof = VulkanCore.Prove(device!, out var pixel);
        Assert.Multiple(() =>
        {
            Assert.That(proof, Is.Empty);
            Assert.That(pixel[..4], Is.EqualTo(new byte[] { 64, 128, 191, 255 }).Within(1), "0.25 0.5 0.75 1 as bytes");
            Assert.That(device!.Name, Does.Contain("Radeon").Or.Not.Empty);
        });
    }
}

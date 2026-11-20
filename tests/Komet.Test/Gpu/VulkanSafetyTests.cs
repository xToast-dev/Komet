using Komet.Core;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class VulkanSafetyTests
{
    // The Wayland/NVIDIA surface stays with EGL: no retry can make Vulkan present, so the game must stay on OpenGL
    [Test]
    public void PresentationIsRefusedOnlyOnWaylandWithNvidia()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VulkanRenderer.CannotPresentOn(true, "NVIDIA Corporation NVIDIA GeForce RTX 4080"), Is.True);
            Assert.That(VulkanRenderer.CannotPresentOn(true, "Mesa AMD Radeon RX 7900 XTX"), Is.False);
            Assert.That(VulkanRenderer.CannotPresentOn(false, "NVIDIA Corporation NVIDIA GeForce RTX 4080"), Is.False);
        });
    }

    [Test]
    public void StartupRejectsPreviousFailuresAndMemoryPressure()
    {
        const ulong gb = 1UL << 30;
        Assert.That(VulkanRenderer.StartupSafety(VulkanRecord.Run.Failed, 8 * gb, 16 * gb), Is.Not.Empty);
        Assert.That(VulkanRenderer.StartupSafety(VulkanRecord.Run.Pending, 8 * gb, 16 * gb), Is.Not.Empty);
        Assert.That(VulkanRenderer.StartupSafety(VulkanRecord.Run.Ran, gb, 16 * gb), Is.Not.Empty);
        Assert.That(VulkanRenderer.StartupSafety(VulkanRecord.Run.Ran, 3 * gb, 16 * gb), Is.Empty);
        Assert.That(VulkanRenderer.StartupSafety(VulkanRecord.Run.Unknown, 0, 0), Is.Empty);
    }
}

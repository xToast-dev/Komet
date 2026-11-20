using Komet.Core;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class VulkanSafetyTests
{
    // Only Wayland with NVIDIA needs Vulkan to leave EGL's wl_surface alone; other drivers keep presenting to the
    // window as before
    [Test]
    public void TheChildSurfaceIsUsedOnlyOnWaylandWithNvidia()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TerrainRenderer.OwnWaylandSurface(true, "NVIDIA Corporation NVIDIA GeForce RTX 4080"), Is.True);
            Assert.That(TerrainRenderer.OwnWaylandSurface(true, "Mesa AMD Radeon RX 7900 XTX"), Is.False);
            Assert.That(TerrainRenderer.OwnWaylandSurface(false, "NVIDIA Corporation NVIDIA GeForce RTX 4080"),
                Is.False);
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

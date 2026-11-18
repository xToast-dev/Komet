using Komet.Core;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class VulkanSafetyTests
{
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

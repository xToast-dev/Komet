using Komet.Core;
using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

[NonParallelizable]
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

    // Native Wayland: the device runs, OpenGL keeps the window and the GPU-culled draws stay on OpenGL's buffers
    [Test]
    public void ComputeModeStartsVulkanWithoutTakingTheWindow()
    {
        var (mode, backend, drawer, takes, suspended) =
            (VulkanRenderer.Mode, GpuBackends.Current, OcclusionCulling.Drawer, OcclusionCulling.Takes,
                OcclusionCulling.Suspended);
        try
        {
            VulkanRenderer.Mode = 2;
            Assert.Multiple(() =>
            {
                Assert.That(VulkanRenderer.Compute, Is.True);
                Assert.That(VulkanRenderer.Requested, Is.False, "no Vulkan presentation is asked for");
                Assert.That(VulkanRenderer.SafetyReason, Is.Empty);
                Assert.That(VulkanRenderer.Locked, Is.Empty, "compute needs no presentation retry");
                Assert.That(VulkanRenderer.Only, Is.False);
                Assert.That(VulkanCore.Enabled, Is.True);
                Assert.That(GpuBackends.Current, Is.SameAs(GlBackend.Instance));
                Assert.That(OcclusionCulling.Drawer, Is.SameAs(OcclusionCulling.GlCounted));
            });

            VulkanRenderer.Mode = 0;
            Assert.Multiple(() =>
            {
                Assert.That(VulkanRenderer.Compute, Is.False);
                Assert.That(VulkanCore.Enabled, Is.False);
                Assert.That(OcclusionCulling.Drawer, Is.Null);
            });
        }
        finally
        {
            VulkanRenderer.Mode = mode;
            (GpuBackends.Current, OcclusionCulling.Drawer, OcclusionCulling.Takes, OcclusionCulling.Suspended) =
                (backend, drawer, takes, suspended);
        }
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

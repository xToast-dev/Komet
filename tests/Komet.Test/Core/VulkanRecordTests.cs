using Komet.Core;
using Komet.Vulkan;
using Run = Komet.Core.VulkanRecord.Run;

namespace Komet.Test.Core;

// The renderer suggestion: Vulkan-only unless it failed on this GPU with this Komet version
public sealed class VulkanRecordTests
{
    [Test]
    public void VulkanOnlyIsSuggestedUnlessItFailed()
    {
        var file = Path.Join(Path.GetTempPath(), $"komet-gpu-{Guid.NewGuid():N}.txt");
        VulkanRecord.Path = file;
        try
        {
            static int? Suggested(Run run, int vram) =>
                VulkanRenderer.Suggested(new HardwareProfile.Facts(8, 16, vram, 60) { Vulkan = run });

            static Run Recorded() => VulkanRecord.Read("Vendor", "GPU (driver 1.2)");
            var unknown = Recorded();
            VulkanRecord.Began();
            var pending = Recorded();
            VulkanRecord.Ended();
            var ran = Recorded();
            VulkanRecord.Failed();
            VulkanRecord.Began();
            VulkanRecord.Ended();
            Assert.Multiple(() =>
            {
                Assert.That((unknown, pending, ran, Recorded()),
                    Is.EqualTo((Run.Unknown, Run.Pending, Run.Ran, Run.Failed)));
                Assert.That(VulkanRecord.Key("Vendor", "GPU (driver 2.0)"), Is.EqualTo(VulkanRecord.Gpu),
                    "a driver update is the same GPU");
                Assert.That(new[] { Run.Unknown, Run.Pending, Run.Ran }.Select(r => Suggested(r, 4)), Is.All.EqualTo(1),
                    "Vulkan-only for every player who never chose");
                Assert.That(Suggested(Run.Failed, 16), Is.Zero, "OpenGL where it failed with this version");
            });
        }
        finally
        {
            VulkanRecord.Path = null;
            File.Delete(file);
        }
    }

    // A failure holds for the Komet version it happened with: a newer one tries once more, and a line from before versions
    // were kept (an older Komet) holds for none
    [Test]
    public void AFailureHoldsForItsKometVersionOnly()
    {
        var file = Path.Join(Path.GetTempPath(), $"komet-gpu-{Guid.NewGuid():N}.txt");
        var build = VulkanRecord.Build;
        VulkanRecord.Path = file;
        try
        {
            var gpu = VulkanRecord.Key("NVIDIA Corporation", "GTX 1660");
            File.WriteAllLines(file, [gpu + "\tFailed"]);
            VulkanRecord.Build = "3.0.0";
            var before = VulkanRecord.Read("NVIDIA Corporation", "GTX 1660");
            VulkanRecord.Failed();
            var same = VulkanRecord.Read("NVIDIA Corporation", "GTX 1660");
            VulkanRecord.Build = "3.0.1";
            var newer = VulkanRecord.Read("NVIDIA Corporation", "GTX 1660");
            Assert.That((before, same, newer), Is.EqualTo((Run.Unknown, Run.Failed, Run.Unknown)));
        }
        finally
        {
            (VulkanRecord.Path, VulkanRecord.Build) = (null, build);
            File.Delete(file);
        }
    }
}

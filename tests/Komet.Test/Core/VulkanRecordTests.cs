using Komet.Core;
using Komet.Vulkan;
using Run = Komet.Core.VulkanRecord.Run;

namespace Komet.Test.Core;

// The renderer suggestion: Vulkan-only only after a clean run on this GPU with room in video memory; a failure is for good
public sealed class VulkanRecordTests
{
    [Test]
    public void VulkanOnlyIsSuggestedOnlyAfterACleanRun()
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
                Assert.That(new[] { Run.Unknown, Run.Pending }.Select(r => Suggested(r, 16)), Is.All.Null);
                Assert.That((Suggested(Run.Ran, 8), Suggested(Run.Ran, 4), Suggested(Run.Ran, 0)),
                    Is.EqualTo(((int?)1, (int?)null, (int?)null)));
                Assert.That(Suggested(Run.Failed, 16), Is.Zero);
            });
        }
        finally
        {
            VulkanRecord.Path = null;
            File.Delete(file);
        }
    }
}

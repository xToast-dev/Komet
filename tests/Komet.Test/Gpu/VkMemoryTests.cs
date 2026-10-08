using Komet.Vulkan;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class VkMemoryTests
{
    [Test]
    public void AnAllocationCountsForItsFileUntilFreed()
    {
        var before = VkMemory.Bytes;
        VkMemory.Allocated(0xABC001, 3UL << 20, "/src/Komet/Vulkan/Terrain/TerrainTextures.cs");
        VkMemory.Allocated(0xABC002, 5UL << 20, "/src/Komet/Vulkan/Terrain/TerrainTextures.cs");
        var held = VkMemory.Bytes - before;
        var report = VkMemory.Report();
        VkMemory.Freed(0xABC001);
        VkMemory.Freed(0xABC002);
        VkMemory.Freed(0xABC002); // twice: counted once
        Assert.Multiple(() =>
        {
            Assert.That(held, Is.EqualTo(8UL << 20));
            Assert.That(report, Does.Contain("TerrainTextures 8 MB"));
            Assert.That(VkMemory.Bytes, Is.EqualTo(before));
        });
    }

    // The managed figure counts garbage made since the last collection; what a full one left alive is told beside it, and grows by
    // what something keeps
    [Test]
    public void TheReportTellsWhatTheLastFullCollectionLeftAlive()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var before = VkMemory.Survived();
        var kept = new byte[64 << 20];
        kept[^1] = 1;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var after = VkMemory.Survived();
        GC.KeepAlive(kept);
        Assert.Multiple(() =>
        {
            Assert.That(VkMemory.Report(), Does.Contain(" MB alive after full collection "));
            Assert.That(Alive(after) - Alive(before), Is.InRange(60, 70), $"{before} / {after}");
        });
    }

    private static int Alive(string survived) => int.Parse(survived.Split(' ')[0], CultureInfo.InvariantCulture);
}

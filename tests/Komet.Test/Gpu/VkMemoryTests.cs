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
}

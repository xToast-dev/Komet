using Komet.Vulkan;

namespace Komet.Test.Gpu;

// A pool born in AddModel is adopted only for a manager of the chunk renderer's passes 0-8, found by reference
public sealed class PoolBirthsTests
{
    private static MeshDataPoolManager Manager() =>
        (MeshDataPoolManager)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPoolManager));

    [Test]
    public void ABornPoolsPassIsThatOfItsManager()
    {
        MeshDataPoolManager opaque = Manager(), liquid = Manager(), tenth = Manager(), other = Manager();
        var passes = new MeshDataPoolManager[10][];
        (passes[0], passes[4], passes[9]) = ([opaque], [Manager(), liquid], [tenth]);
        Assert.Multiple(() =>
        {
            Assert.That(VulkanRenderer.PassOf(passes, opaque), Is.Zero);
            Assert.That(VulkanRenderer.PassOf(passes, liquid), Is.EqualTo(4));
            Assert.That(VulkanRenderer.PassOf(passes, tenth), Is.EqualTo(-1), "past the chunk passes");
            Assert.That(VulkanRenderer.PassOf(passes, other), Is.EqualTo(-1), "another mod's pool");
            Assert.That(VulkanRenderer.PassOf(null, opaque), Is.EqualTo(-1));
        });
    }
}

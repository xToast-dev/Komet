using System.Collections.Concurrent;
using Komet.World;

namespace Komet.Test.World;

// The dirty-block queue keeps the first entry of each block and skip-player, in order; the patch finds its target
[NonParallelizable]
public sealed class DirtyBlocksTests
{
    [Test]
    public void DuplicatesGoFirstOfEachStays()
    {
        var queue = new ConcurrentQueue<Vec4i>();
        foreach (var (x, w) in new[] { (1, -1), (2, -1), (1, -1), (1, 5), (3, -1), (2, -1), (1, -1) })
            queue.Enqueue(new Vec4i(x, 10, 20, w));
        var dropped = DirtyBlocks.Squeeze(queue);
        (int, int)[] expected = [(1, -1), (2, -1), (1, 5), (3, -1)];
        Assert.Multiple(() =>
        {
            Assert.That(dropped, Is.EqualTo(3));
            Assert.That(queue.Select(v => (v.X, v.W)), Is.EqualTo(expected));
        });
    }

    [Test]
    public void TheBlockSimulationIsPatched()
    {
        using var harmony = new TestHarmony("komet-test-dirtyblocks");
        DirtyBlocks.Install(harmony);
        Assert.That(DirtyBlocks.Installed, Is.True);
    }
}

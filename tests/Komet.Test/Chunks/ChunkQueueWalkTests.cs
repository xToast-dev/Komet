using System.Collections;
using Komet.Chunks;

namespace Komet.Test.Chunks;

// The pooled walker yields what the engine's iterator yields, after removals at the front too, and nests
[NonParallelizable]
public sealed class ChunkQueueWalkTests
{
    private sealed class Item(long index) : ILongIndex
    {
        public long Index { get; } = index;

        public void FlagToDispose()
        {
        }
    }

    [Test]
    public void InstallFindsTheServerWalks()
    {
        using var harmony = new TestHarmony("komet-test-queuewalk");
        ChunkQueueWalk.Install(harmony);
        Assert.That(ChunkQueueWalk.Sites, Is.EqualTo(4), "the server's foreach over the load queue moved");
    }

    [Test]
    public void WalksAsTheEngine()
    {
        var queueType = AccessTools.TypeByName("Vintagestory.Common.ConcurrentIndexedFifoQueue`1").MakeGenericType(typeof(Item));
        var queue = Activator.CreateInstance(queueType, 16, 1)!;
        var enqueue = queueType.GetMethod("Enqueue")!;
        for (var i = 0; i < 6; i++) _ = enqueue.Invoke(queue, [new Item(i)]);

        _ = queueType.GetMethod("Dequeue")!.Invoke(queue, []);
        var walker = typeof(ChunkQueueWalk).GetNestedType("Walker`1", AccessTools.all)!.MakeGenericType(typeof(Item));
        Assert.That((bool)walker.GetMethod("Bind")!.Invoke(null, [queueType])!, Is.True);

        var engine = Indices(((IEnumerable)queue).GetEnumerator());
        ChunkQueueWalk.Enabled = true;
        try
        {
            var rent = walker.GetMethod("Rent")!;
            var outer = (IEnumerator)rent.Invoke(null, [queue])!;
            var nested = (IEnumerator)rent.Invoke(null, [queue])!;
            var inner = Indices(nested);
            ((IDisposable)nested).Dispose();
            var mine = Indices(outer);
            ((IDisposable)outer).Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(engine, Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
                Assert.That(mine, Is.EqualTo(engine));
                Assert.That(inner, Is.EqualTo(engine));
            });
        }
        finally
        {
            ChunkQueueWalk.Enabled = false;
        }
    }

    private static List<long> Indices(IEnumerator walk)
    {
        List<long> seen = [];
        while (walk.MoveNext() && seen.Count < 64) seen.Add(((ILongIndex)walk.Current!).Index);

        return seen;
    }
}

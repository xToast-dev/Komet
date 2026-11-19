using System.Collections.Concurrent;
using Komet.Tessellation;

namespace Komet.Test.Tessellation;

public sealed class TessQueueTests
{
    private const long MulX = 1024, MulZ = 1024;

    private static long Index(int x, int y, int z) => (y * MulZ + z) * MulX + x;

    private static TessQueue Queue(int px = 100, int py = 3, int pz = 100, double yaw = 0)
    {
        var queue = new TessQueue();
        queue.Map(MulX, MulZ);
        var (fx, fz) = TessQueue.Facing(yaw);
        queue.Aim(new TessView(px, py, pz, fx, fz));
        return queue;
    }

    private static List<long> TakeAll(TessQueue queue)
    {
        var taken = new List<long>();
        for (var i = 0; i < 100000 && queue.TryTake(out var raw); i++)
        {
            taken.Add(raw);
            queue.Done(raw & long.MaxValue);
        }

        return taken;
    }

    // Columns (x, z) at y 3 as they arrive, and the order they are taken in from (100, 3, 100) facing +x: nearest first; within the
    // near ring every chunk scores its squared distance and equal scores keep the engine's arrival order; ahead beats behind
    [TestCase(new[] { 140, 100, 101, 100, 110, 100 }, new[] { 1, 2, 0 }, TestName = "nearest comes first")]
    public void TheQueueTakesNearestFirst(int[] columns, int[] order)
    {
        var queue = Queue();
        long[] marks = [.. columns.Chunk(2).Select(c => Index(c[0], 3, c[1]))];
        queue.AddRange(marks);
        Assert.That(TakeAll(queue), Is.EqualTo(order.Select(i => marks[i])));
    }

    [Test]
    public void DrainHandsBackEverythingAsEngineMarks()
    {
        var queue = Queue();
        long a = Index(1, 1, 1), b = Index(2, 2, 2) | long.MinValue, c = Index(3, 3, 3);
        queue.AddRange([a, b]);
        queue.Defer(c);
        var back = new UniqueQueue<long>();
        queue.Drain(back);
        Assert.That(back, Is.EquivalentTo([a, b, c]));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void AChunkInFlightIsNotHandedOutTwice()
    {
        var queue = Queue();
        var index = Index(7, 2, 7);
        queue.AddRange([index]);
        Assert.That(queue.TryTake(out var first), Is.True);
        queue.AddRange([index]); // marked dirty again while the first pass runs
        Assert.That(queue.TryTake(out _), Is.False, "the second pass waits for the first");
        Assert.That(queue.Count, Is.EqualTo(1), "and still counts as waiting");
        queue.Done(first);
        Assert.That(TakeAll(queue), Is.EqualTo([index]));
    }

    [Test]
    public void ManyThreadsTakeEveryChunkExactlyOnce()
    {
        var queue = Queue();
        var marks = Enumerable.Range(0, 20000).Select(i => Index(i % 200, 3, i / 200 % 200)).Distinct().ToArray();
        queue.AddRange(marks);
        var taken = new ConcurrentBag<long>();
        var active = new ConcurrentDictionary<long, byte>();
        var readded = new ConcurrentDictionary<long, byte>();
        var overlap = 0;
        _ = Parallel.For(0, 4, (_, _) =>
        {
            for (var i = 0; i < 100000 && queue.TryTake(out var raw); i++)
            {
                if (!active.TryAdd(raw, 0)) _ = Interlocked.Increment(ref overlap);

                taken.Add(raw);
                if (i % 7 == 0 && readded.TryAdd(raw, 0))
                {
                    queue.AddRange([raw]); // dirty again while in flight: once more, later
                }

                _ = active.TryRemove(raw, out _);
                queue.Done(raw);
            }
        });
        var counts = taken.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Multiple(() =>
        {
            Assert.That(overlap, Is.Zero, "one chunk on two threads at once");
            Assert.That(counts.Keys, Is.EquivalentTo(marks));
            Assert.That(counts.Values.All(n => n is 1 or 2), Is.True);
            Assert.That(queue.Count, Is.Zero);
        });
    }

}

using System.Diagnostics;

namespace Komet.Test.Core;

// Komet's worker pool: frame batches run every item exactly once and never wait for a worker busy in a background job, background jobs
// go on between batches, exceptions stay inside the pool, and moving to another world or stopping ends the threads
public sealed class WorkerPoolTests
{
    private const int Items = 4096;
    private static readonly int[] Hits = new int[Items];
    private static readonly int[] Threads = new int[Items];

    private static readonly Action<int> Count = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        Threads[i] = WorkerPool.Current;
        Thread.SpinWait(200);
    };

    private static readonly ManualResetEventSlim Never = new(false); // a background job that takes a second
    private static int _throwAt = -1, _background;

    private static readonly Action<int> Throwing = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        if (i == _throwAt) throw new InvalidOperationException("item " + i);
    };

    private static int _inside, _most;
    private int _limit;
    private Func<bool>? _saved;

    [SetUp]
    public void Save()
    {
        (_saved, _limit) = (WorkerPool.Background, WorkerPool.BackgroundLimit);
        (WorkerPool.Background, WorkerPool.BackgroundLimit, _background, _inside, _most) =
            (null, WorkerPool.MaxThreads, 0, 0, 0);
        Array.Clear(Hits);
        Array.Clear(Threads);
    }

    [TearDown]
    public void Restore()
    {
        Assert.That(WorkerPool.Stop(), Is.True, "every thread ended");
        (WorkerPool.Background, WorkerPool.BackgroundLimit) = (_saved, _limit);
    }

    [Test]
    public void WithoutThreadsTheBatchIsDeclined()
    {
        WorkerPool.Resize(null, 0);
        Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Declined));
        Assert.That(Hits.Sum(), Is.Zero);
    }

    [Test]
    public void EveryItemRunsOnceAndTheWorkersHelp()
    {
        WorkerPool.Resize(null, 4);
        for (var batch = 0; batch < 50; batch++)
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 50), Is.True, "each item once per batch");
            Assert.That(Threads.Any(t => t >= 0), Is.True, "workers took items");
        });
    }

    // Every worker sits in a background job far longer than the batch takes: the caller runs the batch alone and does not wait for them,
    // and the background jobs go on
    [Test]
    public void ABatchCompletesWhileEveryWorkerIsInABackgroundJob()
    {
        WorkerPool.Background = static () =>
        {
            _ = Interlocked.Increment(ref _background);
            _ = Never.Wait(1000);
            return true;
        };
        WorkerPool.Resize(null, 3);
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 3, 1000);
        Assert.That(Volatile.Read(ref _background), Is.EqualTo(3), "every worker is inside a job");
        var watch = Stopwatch.StartNew();
        var result = WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads);
        watch.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(WorkerPool.FrameResult.Done));
            Assert.That(Hits.All(n => n == 1), Is.True);
            Assert.That(Threads.All(t => t < 0), Is.True, "no worker left its job for the batch");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500), "the caller did not wait for a background job");
        });
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 6, 4000);
        Assert.That(Volatile.Read(ref _background), Is.GreaterThanOrEqualTo(6), "the background jobs go on");
    }

    // However many threads are free, no more than BackgroundLimit are inside a background job at once; frame batches still get every
    // thread that rests
    [Test]
    public void BackgroundJobsStayUnderTheLimit()
    {
        WorkerPool.BackgroundLimit = 2;
        WorkerPool.Background = static () =>
        {
            var inside = Interlocked.Increment(ref _inside);
            _ = InterlockedMax(inside);
            _ = Interlocked.Increment(ref _background);
            _ = Never.Wait(2);
            _ = Interlocked.Decrement(ref _inside);
            return true;
        };
        WorkerPool.Resize(null, 6);
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 200, 5000);
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref _background), Is.GreaterThanOrEqualTo(200), "the jobs run");
            Assert.That(Volatile.Read(ref _most), Is.EqualTo(2), "never more than the limit at once");
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
            Assert.That(Threads.Any(t => t >= 0), Is.True, "the threads left out of background work help the frame");
        });
    }

    private static int InterlockedMax(int value)
    {
        var seen = Volatile.Read(ref _most);
        for (var i = 0; i < 64 && value > seen; i++)
            seen = Interlocked.CompareExchange(ref _most, value, seen) == seen ? value : Volatile.Read(ref _most);
        return seen;
    }

    // An item that throws fails its batch after every other item ran; a second failure turns frame batches off until Stop
    [Test]
    public void FailedBatchesTurnFrameJobsOff()
    {
        WorkerPool.Resize(null, 3);
        _throwAt = 17;
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 1), Is.True, "the other items still ran");
            Assert.That(WorkerPool.LastError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(WorkerPool.FrameOff, Is.False, "one failure only fails its batch");
        });
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.FrameOff, Is.True);
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Declined));
            Assert.That(WorkerPool.Running, Is.EqualTo(3), "the threads keep running for background jobs");
        });
        _throwAt = -1;
    }

    // A background job that throws past its own handler stops background jobs; the threads and frame batches go on
    [Test]
    public void AThrowingBackgroundJobStopsOnlyBackgroundJobs()
    {
        WorkerPool.Background = static () => throw new InvalidOperationException("background");
        WorkerPool.Resize(null, 2);
        _ = SpinWait.SpinUntil(() => WorkerPool.BackgroundOff, 1000);
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.BackgroundOff, Is.True);
            Assert.That(WorkerPool.Running, Is.EqualTo(2));
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        });
    }

    // Fewer threads park the rest; another world retires them all, and Stop joins every thread, retired ones included
    [Test]
    public void ParkingAndAnotherWorld()
    {
        var (first, second) = (new object(), new object());
        WorkerPool.Resize(first, 4);
        WorkerPool.Resize(first, 1);
        Assert.That(WorkerPool.Running, Is.EqualTo(1));
        WorkerPool.Resize(first, 3);
        Assert.That(WorkerPool.Running, Is.EqualTo(3), "parked threads are reused");
        WorkerPool.Resize(second, 2);
        Assert.That(WorkerPool.Running, Is.EqualTo(2));
        Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads), Is.EqualTo(WorkerPool.FrameResult.Done));
        Assert.That(WorkerPool.Stop(), Is.True);
        Assert.That(WorkerPool.Running, Is.Zero);
    }
}

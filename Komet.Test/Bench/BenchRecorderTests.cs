using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace Komet.Test.Bench;

// FrameClock hands the recorder frame N as N+1 starts, with N's own dt, GC pause and collections, and the recorder books it to the
// stamp the driver left during N
public sealed class BenchRecorderTests
{
    private const string Tesselate = "esr-tesseleateshape";
    private const int AllocationWindows = 5;
    private static readonly int[] TessPasses = [2, 0, 1], LaterFirst = [1, 0];
    private static readonly float[] TessMs = [3, 0, 4];

    private static long Ticks(double ms)
    {
        return (long)(ms * Stopwatch.Frequency / 1000);
    }

    // A frame tree the way FrameProfilerUtil builds it: FrameProfilerUtil adds every Mark of one code in a frame into one entry and
    // counts the calls, so a 23.7 ms esr-tesseleateshape may be one entity or nine; rendTransparent is a child range
    private static ProfileEntryRange Tree()
    {
        var root = new ProfileEntryRange { Code = "all", ElapsedTicks = Ticks(40), Marks = [] };
        root.Marks[Tesselate] = new ProfileEntry((int)Ticks(23.7), 9);
        root.Marks["rend3D-ret-op"] = new ProfileEntry((int)Ticks(5), 1);
        var transparent = new ProfileEntryRange
        { Code = "rendTransparent", ElapsedTicks = Ticks(3), CallCount = 2, Marks = [] };
        transparent.Marks["rendtransp-blocks"] = new ProfileEntry((int)Ticks(2), 4);
        root.ChildRanges = new Dictionary<string, ProfileEntryRange> { ["rendTransparent"] = transparent };
        return root;
    }

    private static BenchRecorder Started(int capacity = 1000, int segments = 4, int spikes = 3)
    {
        var recorder = new BenchRecorder(capacity, segments, spikes);
        recorder.Start();
        return recorder;
    }

    private static void Feed(BenchRecorder recorder, int segment, float ms, BenchFrameTags tags = BenchFrameTags.None,
        ProfileEntryRange? root = null)
    {
        recorder.Stamp(segment, tags, 0, 0, 0);
        recorder.Frame(new FrameRecord(0, ms, 0, 0, 0, 0, root), default);
    }

    [Test]
    public void AFrameIsBookedToTheStampLeftDuringIt()
    {
        var recorder = Started();
        recorder.Stamp(2, BenchFrameTags.Warmup, 10, 20, 1.5f);
        recorder.Frame(new FrameRecord(7, 16, 1.5, 0.2, 0.25, double.NaN, null),
            new FrameCounters(1, 0, 0, 4096, 2048));
        recorder.Frame(new FrameRecord(8, 20, 0, 0, 0, 0, null), default); // the driver did not run in between
        var frames = recorder.Frames.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Length.EqualTo(2));
            Assert.That((frames[0].Segment, frames[0].Flags, frames[0].DtMs, frames[0].GcMs),
                Is.EqualTo(((short)2, BenchFrameTags.Warmup, 16f, 1.5f)));
            Assert.That((frames[0].Gen0, frames[0].AllocKb, frames[0].MainAllocKb, frames[0].JitMs),
                Is.EqualTo(((byte)1, 4, 2, 0.25f)));
            Assert.That(frames[0].RunQueueMs, Is.NaN, "no schedstat, no run-queue time");
            Assert.That((frames[0].X, frames[0].Z, frames[0].Yaw), Is.EqualTo((10f, 20f, 1.5f)));
            Assert.That(frames[1].Flags & BenchFrameTags.NoStamp, Is.EqualTo(BenchFrameTags.NoStamp),
                "an unstamped frame says so");
            Assert.That(frames[1].Segment, Is.EqualTo(2));
        });
    }

    // The frame FrameClock sees the sink join in has no counters from its start, so it is skipped; the collection lands on its frame.
    // Stats is on (the HUD is looking) so frames close before the sink joins mid-frame, as in BenchDriver.Boot; without it _start is 0
    // and the join frame is never closed at all
    [Test]
    public void FrameClockHandsEveryLaterFrameToTheSinkWithItsOwnCollection()
    {
        var (stats, profiler) = (FrameClock.Stats, ScreenManager.FrameProfiler);
        (FrameClock.Stats, ScreenManager.FrameProfiler) = (true, null!);
        var recorder = Started();
        try
        {
            FrameClock.Begin();
            FrameClock.Sink = recorder;
            FrameClock.End();
            FrameClock.Begin();
            for (var frame = 0; frame < 3; frame++)
            {
                recorder.Stamp(0, BenchFrameTags.None, 0, 0, 0);
                if (frame == 1) _ = Busy.Collect();
                Busy.Spin(2);
                FrameClock.End();
                FrameClock.Begin();
            }
        }
        finally
        {
            FrameClock.Sink = null;
            (FrameClock.Stats, ScreenManager.FrameProfiler) = (stats, profiler);
            FrameClock.Begin();
        }

        var frames = recorder.Frames.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Length.EqualTo(3));
            Assert.That(frames[0].Flags & BenchFrameTags.NoStamp, Is.EqualTo(BenchFrameTags.None),
                "the join frame is not booked");
            Assert.That(frames.Select(f => f.DtMs), Is.All.GreaterThanOrEqualTo(2f));
            Assert.That(frames[1].Gen0, Is.GreaterThanOrEqualTo(1));
            Assert.That(frames[1].GcMs, Is.GreaterThan(0f));
            Assert.That(frames[1].MainAllocKb, Is.GreaterThan(0),
                "Busy.Collect allocated on this thread until gen0 filled");
        });
    }

    // Only a spike frame whose counters show a collection asks the runtime for the last one
    [Test]
    [SuppressMessage("Major Code Smell", "S1215", Justification = "the collection is the event under test")]
    public void ASpikeThatCollectedDescribesTheLastCollection()
    {
        var recorder = Started(spikes: 2);
        Feed(recorder, 0, 20);
        GC.Collect(2, GCCollectionMode.Forced, true);
        recorder.Stamp(0, BenchFrameTags.None, 0, 0, 0);
        recorder.Frame(new FrameRecord(1, 50, 3, 0, 0, 0, null), new FrameCounters(1, 1, 1, 0, 0));
        var spikes = recorder.Spikes(0).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(spikes.Select(s => s.Frame), Is.EqualTo(LaterFirst));
            Assert.That(spikes[0].Gc,
                Has.Property(nameof(BenchGc.Valid)).True.And.Property(nameof(BenchGc.Generation)).EqualTo(2));
            Assert.That(spikes[0].Gc.Index, Is.GreaterThan(0));
            Assert.That(spikes[1].Gc.Valid, Is.False, "a frame that collected nothing");
        });
    }

    // The slots stay sorted, dearest first; a tie keeps the earlier frame. Excluded frames never become spikes.
    [Test]
    public void SpikesKeepTheLargestCountedFramesOfTheirSegment()
    {
        var recorder = Started(spikes: 3);
        foreach (var (ms, tags) in new (float, BenchFrameTags)[]
                 {
                     (5, 0), (9, 0), (7, 0), (9, 0), (3, 0), (12, 0), (500, BenchFrameTags.Paused),
                     (400, BenchFrameTags.Discard)
                 })
            Feed(recorder, 1, ms, tags);
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Spikes(1).ToArray().Select(s => (s.Frame, s.Ms)),
                Is.EqualTo([(5, 12f), (1, 9f), (3, 9f)]));
            Assert.That(recorder.Spikes(0).ToArray().All(s => s.Frame < 0), "other segments stay empty");
        });
    }

    // A dearer frame moves the earlier spike down a rank, and its marks move with it
    [Test]
    public void ASpikeCarriesItsDearestMarksAndTheirCalls()
    {
        var recorder = Started(spikes: 2);
        Feed(recorder, 3, 40, root: Tree());
        Feed(recorder, 3, 60);
        var marks = recorder.Marks(3, 1).ToArray().Where(m => !m.Empty).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Spikes(3).ToArray().Select(s => s.Frame), Is.EqualTo(LaterFirst));
            Assert.That(recorder.Marks(3, 0).ToArray(), Is.All.Property(nameof(BenchSpikeMark.Empty)).True,
                "the 60 ms frame had no profile");
            Assert.That(marks.Select(m => m.Ms), Is.Ordered.Descending);
            Assert.That(marks[0], Is.EqualTo(new BenchSpikeMark(null, Tesselate, marks[0].Ms, 9)));
            Assert.That(marks.Single(m => m is { Range: "rendTransparent", Name: null }).Calls, Is.EqualTo(2),
                "a range as a whole");
            Assert.That(marks.Single(m => m.Name == "rendtransp-blocks").Calls, Is.EqualTo(4));
        });
    }

    // TessAccounting counts on the tessellation thread; a frame gets the passes that finished since the previous one, and a world
    // change (the counters start over) does not turn into a negative frame
    [Test]
    public void TessellationPassesAreBookedToTheFrameTheyFinishedIn()
    {
        TessAccounting.Clear();
        try
        {
            var recorder = Started();
            TessAccounting.Record(TessBucket.Full, Ticks(2), false);
            TessAccounting.Record(TessBucket.Edge, Ticks(1), false);
            TessAccounting.Record(TessBucket.Skipped, Ticks(5), false); // an Empty chunk: no pass
            Feed(recorder, 0, 16);
            Feed(recorder, 0, 16);
            TessAccounting.Clear();
            TessAccounting.Record(TessBucket.PriorityFull, Ticks(4), false);
            Feed(recorder, 0, 16);
            var frames = recorder.Frames.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(frames.Select(f => f.TessPasses), Is.EqualTo(TessPasses));
                Assert.That(frames.Select(f => f.TessMs), Is.EqualTo(TessMs).Within(1e-3));
            });
        }
        finally
        {
            TessAccounting.Clear();
        }
    }

    [Test]
    public void FramesPastTheCapacityAreCountedNotStored()
    {
        var recorder = Started(5);
        for (var i = 0; i < 8; i++) Feed(recorder, 0, 10);
        Assert.That((recorder.Count, recorder.Dropped), Is.EqualTo((5, 3)));
    }

    // The sink runs inside window_RenderFrame's prefix every frame: nothing allocates, spikes and their marks included, as long as
    // the spike frames collected nothing. Every window pushes new spikes, and one of them has to come out at zero: once a test
    // has patched anything, Harmony hooks the JIT, and a compile on this thread (the window's own on-stack replacement, a
    // callee's tier-up) then allocates once. A frame that allocates fails every window.
    [Test]
    public void RecordingAllocatesNothingPerFrame()
    {
        var recorder = Started(30_000, 2, 10);
        ProfileEntryRange[] trees = [Tree(), Tree()]; // FrameProfilerUtil.Begin builds a new root every frame
        for (var i = 0; i < 1000; i++) Feed(recorder, i < 500 ? 0 : 1, 10, root: trees[i % 2]);
        var allocated = new long[AllocationWindows];
        for (var window = 0; window < allocated.Length; window++)
            allocated[window] = Window(recorder, trees, 20 + 10 * window);
        Assert.That(allocated.Min(), Is.Zero, $"bytes per window: {string.Join(", ", allocated)}");
    }

    private static long Window(BenchRecorder recorder, ProfileEntryRange[] trees, int ms)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++) Feed(recorder, 1, ms + i % 7, root: trees[i % 2]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

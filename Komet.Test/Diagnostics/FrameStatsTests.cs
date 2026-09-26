using Vintagestory.Client;

namespace Komet.Test.Diagnostics;

// The lows are taken over the frames actually recorded, so an empty or short history must report nothing rather than a number
public sealed class FrameStatsTests
{
    private const int History = 2000;

    private static FrameStats Filled(int frames, float ms = 10f)
    {
        var stats = new FrameStats();
        for (var i = 0; i < Math.Min(frames, 4 * History); i++) stats.Record(ms / 1000f, 0, true);
        stats.SampleWindow();
        return stats;
    }

    [Test]
    public void FramesThatAreNotSteadyStayOutOfTheHistory()
    {
        var stats = new FrameStats();
        for (var i = 0; i < 500; i++) stats.Record(1f, 0, false); // a load stall
        for (var i = 0; i < 200; i++) stats.Record(0.01f, 0, true);
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(200));
            Assert.That(stats.WorstMs, Is.EqualTo(10f).Within(0.01f));
            Assert.That(stats.Low1Fps, Is.EqualTo(100f).Within(0.5f));
            Assert.That(stats.Frames, Is.EqualTo(700)); // the live fps still counts every frame
        });
    }

    [TestCase(0)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(999)]
    [TestCase(1000)]
    public void LowsAppearOnlyOnceTheirWindowHasFrames(int frames)
    {
        var stats = Filled(frames);
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(frames));
            Assert.That(stats.WorstMs, frames > 0 ? Is.Not.NaN : Is.NaN);
            Assert.That(stats.Low1Fps, frames >= 100 ? Is.Not.NaN : Is.NaN);
            Assert.That(stats.Low01Fps, frames >= 1000 ? Is.Not.NaN : Is.NaN);
        });
    }

    // The worst frame and the pause it was recorded with, out of the same window the lows come from
    [Test]
    public void TheWorstFrameIsTheOneTheLowsAreTakenFrom()
    {
        var stats = new FrameStats();
        for (var i = 0; i < 1998; i++) stats.Record(0.01f, 0.5f, true);
        stats.Record(0.2f, 12f, true); // one 200 ms hitch
        stats.Record(0.01f, 40f, true); // a big pause in a short frame is not the worst frame's
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(History));
            Assert.That(stats.WorstMs, Is.EqualTo(200f).Within(0.01f));
            Assert.That(stats.WorstGcMs, Is.EqualTo(12f).Within(0.001f));
            Assert.That(stats.Low01Fps,
                Is.EqualTo(1000f / 105f).Within(0.5f)); // the worst two frames: 200 ms and 10 ms
            Assert.That(stats.Low1Fps,
                Is.EqualTo(1000f / 19.5f).Within(0.5f)); // the worst twenty: one 200 ms and nineteen 10 ms
        });
    }

    [Test]
    public void TheRingKeepsOnlyTheLastHistoryFrames()
    {
        var stats = new FrameStats();
        stats.Record(0.5f, 0, true); // falls out again
        for (var i = 0; i < History; i++) stats.Record(0.01f, 0, true);
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(History));
            Assert.That(stats.WorstMs, Is.EqualTo(10f).Within(0.01f));
        });
    }

    [Test]
    public void ResetHistoryStartsTheWindowOver()
    {
        var stats = Filled(1500, 50f);
        stats.ResetHistory();
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.Zero);
            Assert.That(stats.WorstMs, Is.NaN);
            Assert.That(stats.Low01Fps, Is.NaN);
            Assert.That(stats.HistoryMs(History - 1), Is.Zero);
        });
    }

    // The engine's int counter wraps after hours of drawing and Alt+F3 resets it; neither may stop the lows or report a negative rate
    [TestCase(int.MaxValue - 500, int.MinValue + 999, 15)] // 1500 draws across the wrap
    [TestCase(5_000_000, 300, 3)] // reset, then 300 draws
    public void TheDrawCallCounterWrappingOrResetKeepsTheLows(int before, int after, int perFrame)
    {
        var saved = RuntimeStats.drawCallsCount;
        try
        {
            RuntimeStats.drawCallsCount = before;
            var stats = Filled(100);
            stats.Reset();
            stats.ResetHistory();
            for (var i = 0; i < 100; i++) stats.Record(0.02f, 0, true);
            RuntimeStats.drawCallsCount = after;
            stats.SampleWindow();
            Assert.Multiple(() =>
            {
                Assert.That(stats.DrawCallsPerFrame, Is.EqualTo(perFrame));
                Assert.That(stats.Low1Fps, Is.EqualTo(50f).Within(0.5f), "the window after the counter's jump");
            });
        }
        finally
        {
            RuntimeStats.drawCallsCount = saved;
        }
    }

    // Read in the Ortho stage, the dt that ended at this frame's start pairs with the GC pause since the previous Ortho, so a collection
    // early in a frame lands on the one before. FrameClock cuts both at frame starts.
    [Test]
    public void EachFrameCarriesItsOwnGcPause()
    {
        var (stats, profiler) = (FrameClock.Stats, ScreenManager.FrameProfiler);
        ScreenManager.FrameProfiler = null!;
        FrameClock.Stats = true;
        try
        {
            var records = new List<FrameRecord>();
            TimeSpan own = default;
            FrameClock.Begin();
            for (var frame = 0; frame < 4; frame++)
            {
                var completed = FrameClock.Completed;
                if (frame == 2) own = Busy.Collect(); // inside the third frame, early, well before any Ortho stage
                Busy.Spin(frame == 2 ? 30 : 5);
                FrameClock.End();
                FrameClock.Begin();
                Assert.That(FrameClock.Completed, Is.EqualTo(completed + 1));
                records.Add(FrameClock.Last);
            }

            var history = new FrameStats();
            foreach (var record in records) history.Record((float)(record.DtMs / 1000), (float)record.GcMs, true);
            history.SampleWindow();
            Assert.Multiple(() =>
            {
                Assert.That(records[2].GcMs, Is.GreaterThanOrEqualTo(own.TotalMilliseconds - 1e-6),
                    "the collection belongs to its own frame");
                Assert.That(records[1].GcMs, Is.LessThan(own.TotalMilliseconds), "not to the frame before it");
                Assert.That(history.WorstMs, Is.EqualTo(records[2].DtMs).Within(0.01),
                    "the long frame is the one with the collection");
                Assert.That(history.WorstGcMs, Is.EqualTo(records[2].GcMs).Within(0.001));
                Assert.That(records.Select(record => record.OutsideMs), Is.All.GreaterThanOrEqualTo(0),
                    "End() ran for every frame");
                Assert.That(records.Select(record => record.Root), Is.All.Null, "no profiler, no profile");
            });
        }
        finally
        {
            (FrameClock.Stats, ScreenManager.FrameProfiler) = (stats, profiler);
            FrameClock.Begin();
        }
    }

    // An exception inside the frame skips the postfix: the frame still counts, only the split between frames is unknown
    [Test]
    public void AFrameWithoutItsPostfixKeepsItsDt()
    {
        var stats = FrameClock.Stats;
        FrameClock.Stats = true;
        try
        {
            FrameClock.Begin();
            Busy.Spin(2);
            FrameClock.Begin(); // no End() in between
            Assert.Multiple(() =>
            {
                Assert.That(FrameClock.Last.DtMs, Is.GreaterThan(1));
                Assert.That(FrameClock.Last.OutsideMs, Is.NaN);
            });
        }
        finally
        {
            FrameClock.Stats = stats;
            FrameClock.Begin();
        }
    }

    [Test]
    public void TheClockStandsStillWhileNobodyLooks()
    {
        var stats = FrameClock.Stats;
        FrameClock.Stats = false;
        try
        {
            var completed = FrameClock.Completed;
            for (var i = 0; i < 3; i++)
            {
                FrameClock.Begin();
                FrameClock.End();
            }

            Assert.That(FrameClock.Completed, Is.EqualTo(completed));
        }
        finally
        {
            FrameClock.Stats = stats;
        }
    }
}

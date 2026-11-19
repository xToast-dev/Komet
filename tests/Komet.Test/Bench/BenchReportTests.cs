using System.Text.Json;
using Komet.Bench;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Tessellation;

namespace Komet.Test.Bench;

// The result is read by a script, not a person: it has to parse, carry its schema, write missing numbers as null, and pair the arms
// inside their mirrored blocks so the delta of two arms is the difference the switch made
public sealed class BenchReportTests
{
    private const int FramesPerSegment = 100;

    // Every segment gets FramesPerSegment frames of ms(segment), one 30 ms spike in the middle with a profile whose dearest mark
    // was called 9 times, and, with tessMs, one tessellation pass per frame taking that long
    private static (BenchRun Run, BenchRecorder Recorder) Run(string arms, System.Func<BenchSegment, float> ms,
        int skip = -1, System.Func<BenchSegment, double>? tessMs = null)
    {
        TessAccounting.Clear();
        var config = BenchConfig.Parse(BenchConfigTests.Json($"\"laps\": 4, \"warmupLaps\": 0, {arms}"));
        var segments = BenchScenario.Expand(config);
        var run = new BenchRun(config, segments) { Baseline = Knobs.Snapshot() };
        run.ArmValues = BenchDriver.ArmValues(run.Baseline, config.Arms, out _);
        var recorder = new BenchRecorder(segments.Length * FramesPerSegment, segments.Length, 3);
        var spike = new ProfileEntryRange { Code = "all", ElapsedTicks = Stopwatch.Frequency / 40, Marks = [] };
        spike.Marks["esr-tesseleateshape"] = new ProfileEntry((int)(Stopwatch.Frequency / 50), 9);
        recorder.Start();
        for (var s = 0; s < segments.Length; s++)
        {
            run.Logs[s] = new BenchSegmentLog(true, segments[s].Seconds, 20, 30, 6000, 5000);
            if (s == skip) continue;

            var tags = segments[s].Measured ? BenchFrameTags.None : BenchFrameTags.Unmeasured;
            for (var i = 0; i < FramesPerSegment; i++)
            {
                if (tessMs != null)
                {
                    TessAccounting.Record(TessBucket.Full, (long)(tessMs(segments[s]) * Stopwatch.Frequency / 1000),
                        false);
                }

                recorder.Stamp(s, i == 0 ? tags | BenchFrameTags.Discard : tags, s, i, 0.5f);
                var middle = i == FramesPerSegment / 2;
                recorder.Frame(
                    new FrameRecord(i, middle ? 30 : ms(segments[s]), middle ? 4 : 0, 0, 0, 0, middle ? spike : null),
                    new FrameCounters(middle ? 1 : 0, 0, 0, 2048, 1024));
            }
        }

        recorder.Stop();
        TessAccounting.Clear();
        (run.Complete, run.Finished) = (true, DateTime.UtcNow);
        return (run, recorder);
    }

    private static JsonElement Json(BenchRun run, BenchRecorder recorder)
    {
        using var stream = new MemoryStream();
        BenchReport.WriteJson(stream, run, recorder);
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static float Arms(BenchSegment segment)
    {
        if (!segment.Measured) return 20f;

        return segment.Arm == 0 ? 10f : 12f;
    }

    [Test]
    public void TheResultParsesAndStatesWhatItIs()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms);
        var json = Json(run, recorder);
        var still = json.GetProperty("segments").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "still");
        var spike = still.GetProperty("spikes")[0];
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("schema").GetString(), Is.EqualTo("komet-bench/2"));
            Assert.That(json.GetProperty("complete").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(json.GetProperty("segments").GetArrayLength(), Is.EqualTo(run.Segments.Length));
            Assert.That(json.GetProperty("recorder").GetProperty("frames").GetInt32(),
                Is.EqualTo(run.Segments.Length * FramesPerSegment));
            Assert.That(json.GetProperty("config").GetProperty("laps").GetInt32(), Is.EqualTo(4),
                "the config is echoed");
            Assert.That(json.GetProperty("baseline").EnumerateObject().Count(), Is.EqualTo(Knobs.Count));
            Assert.That(still.GetProperty("frametime").GetProperty("n").GetInt32(), Is.EqualTo(FramesPerSegment - 1),
                "the discarded frame is out");
            Assert.That(still.GetProperty("discardedFrames").GetInt32(), Is.EqualTo(1));
            Assert.That(still.GetProperty("frametime").GetProperty("maxMs").GetDouble(), Is.EqualTo(30));
            Assert.That(still.GetProperty("gc").GetProperty("gen0").GetInt32(), Is.EqualTo(1));
            Assert.That(still.GetProperty("gc").GetProperty("maxPauseMs").GetDouble(), Is.EqualTo(4));
            Assert.That(still.GetProperty("cpu").GetProperty("workingSetMb").GetInt64(), Is.EqualTo(6000));
            Assert.That(spike.GetProperty("ms").GetDouble(), Is.EqualTo(30), "the dearest spike first");
            Assert.That(spike.GetProperty("frame").GetInt32() % FramesPerSegment, Is.EqualTo(FramesPerSegment / 2));
            Assert.That(spike.GetProperty("marks")[0].GetProperty("calls").GetInt32(), Is.EqualTo(9));
            Assert.That(spike.GetProperty("gcCounts")[0].GetInt32(), Is.EqualTo(1));
            Assert.That(spike.GetProperty("gc").GetProperty("generation").GetInt32(), Is.InRange(0, 2),
                "it collected: the last collection");
            Assert.That(still.GetProperty("spikes")[1].GetProperty("gc").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "this one did not");
        });
    }

    // Nearest rank: the smallest frame with at least p of all frames at or below it
    [TestCase(1, 990, 1f)]
    [TestCase(99, 990, 99f)]
    public void PercentilesAreNearestRank(int n, int perMille, float expected)
    {
        var sorted = Enumerable.Range(1, n).Select(i => (float)i).ToArray();
        Assert.That(FrameStats.Percentile(sorted, perMille), Is.EqualTo(expected));
    }

}

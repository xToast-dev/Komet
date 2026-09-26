using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Komet.Test.Hud;

// What the HUD, its dump and its bench report print. The dump quotes the rows the panels show, measured at the end of their interval:
// read live, a window opened right after an interval's end saw the counters just reset, and printed "FPS: 0", raw counts as
// per-frame values and "Infinity %". No number that is not finite may reach any of them.
[NonParallelizable]
public sealed class HudReportTests
{
    private const string Build = "v1.2.3", Newer = "v1.3.0";
    private static readonly string Installed = new('a', 64), Other = new('b', 64);

    [TestCase(double.PositiveInfinity, "F1")]
    [TestCase(double.NegativeInfinity, "N0")]
    [TestCase(double.NaN, "F2")]
    public void NumbersThatAreNotFinitePrintAsNothing(double value, string format)
    {
        Assert.That(HudText.Format(value, format), Is.Empty);
    }

    [Test]
    public void FiniteNumbersPrintCultureInvariant()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HudText.Format(1234.4, "N0"), Is.EqualTo("1,234"));
            Assert.That(HudText.Format(9.94, "F2"), Is.EqualTo("9.94"));
            Assert.That(HudText.Format(0, "F1"), Is.EqualTo("0.0"), "a measured zero is a number");
        });
    }

    // The interval ends, the panel measures, EndInterval resets the counters; the window opens before the next frame is recorded
    [Test]
    public void TheShownRowKeepsTheIntervalItWasMeasuredIn()
    {
        double ms = 9.94, share = 12.5;
        var line = new HudLine
        { Label = () => "mod", Value = () => ms, Percent = () => share, Unit = "ms", Sub = true };
        line.Capture();
        (ms, share) = (0, double.PositiveInfinity); // the reset window: no time yet, and a share of no frame time
        Assert.That(line.ShownText(), Is.EqualTo("  mod: 12.5 % 9.94 ms"), "the report quotes the measured row");
    }

    [Test]
    public void HeadersAndTitlesAreQuotedAsShown()
    {
        var name = "passes";
        var header = new HudLine { Kind = HudLineKind.Header, Label = () => name };
        var title = new HudLine
        {
            Kind = HudLineKind.Title, Label = () => "Komet",
            Badges = [("Release", HudCanvas.Good), ("Build 1.2.3", HudCanvas.Neutral)]
        };
        header.Capture();
        title.Capture();
        name = "changed";
        Assert.Multiple(() =>
        {
            Assert.That(header.ShownText(), Is.EqualTo("[passes]"));
            Assert.That(title.ShownText(), Is.EqualTo("Komet – Release, Build 1.2.3"));
            Assert.That(header.MeanText(), Is.EqualTo("[changed]"), "the bench report reads the label as it is");
        });
    }

    // FrameStats of a window without frames: no rate at all rather than 0 fps, and the share of a mod over it is no number either
    [Test]
    public void AnEmptyWindowClaimsNoRate()
    {
        var frames = new FrameStats();
        var fps = new HudLine { Label = () => "FPS", Value = () => frames.Fps };
        var average = new HudLine { Label = () => "Frametime", Value = () => frames.AverageMs, Unit = "ms" };
        fps.Capture();
        average.Capture();
        Assert.Multiple(() =>
        {
            Assert.That(frames.Fps, Is.NaN);
            Assert.That(frames.AverageMs, Is.NaN);
            Assert.That(fps.ShownText(), Is.Empty, "the row is left out, as the panel leaves out its number");
            Assert.That(average.ShownText(), Is.Empty);
        });

        frames.Record(0.02f, 0, true);
        frames.Record(0.02f, 0, true);
        fps.Capture();
        average.Capture();
        Assert.Multiple(() =>
        {
            Assert.That(fps.ShownText(), Is.EqualTo("FPS: 50"));
            Assert.That(average.ShownText(), Is.EqualTo("Frametime: 20.00 ms"));
        });
    }

    [Test]
    public void AShareThatIsNotFiniteLeavesOnlyTheTime()
    {
        var line = new HudLine
        { Label = () => "mod", Value = () => 9.94, Percent = () => double.PositiveInfinity, Unit = "ms" };
        line.Capture();
        Assert.That(line.ShownText(), Is.EqualTo("mod: 9.94 ms"), "no bare ' %'");
    }

    [Test]
    public void ARowWithoutANumberIsLeftOut()
    {
        var bar = new HudLine { Label = () => "CPU", Percent = () => double.NaN };
        var value = new HudLine
        { Label = () => "VRAM", Value = () => double.NaN, Percent = () => double.NaN, Unit = "MB" };
        var text = new HudLine { Label = () => "update check failed", Sub = true };
        bar.Capture();
        value.Capture();
        text.Capture();
        Assert.Multiple(() =>
        {
            Assert.That(bar.ShownText(), Is.Empty);
            Assert.That(value.ShownText(), Is.Empty);
            Assert.That(text.ShownText(), Is.EqualTo("  update check failed"), "a line of text has no number to miss");
        });
    }

    [TestCase(null, null, "  label")]
    [TestCase("3.50", null, "  label: 3.50 ms")]
    [TestCase("3.50", "12.5", "  label: 12.5 % 3.50 ms")]
    [TestCase(null, "12.5", "  label: 12.5 %")]
    [TestCase("", "12.5", "  label: 12.5 %")]
    [TestCase("", "", "")]
    [TestCase(null, "", "")]
    [TestCase("3.50", "", "  label: 3.50 ms")]
    public void RowsFormatLikeThePanel(string? value, string? percent, string expected)
    {
        Assert.That(HudLine.RowText("label", true, value, percent, "ms"), Is.EqualTo(expected));
    }

    // Render() skips a detail row while detail is off; its text is from the last interval it was measured in, so the dump skips it too
    [Test]
    public void APanelDumpsTheRowsItDraws()
    {
        List<HudLine> lines =
        [
            new() { Kind = HudLineKind.Header, Label = () => "passes" },
            new() { Label = () => "opaque", Value = () => 3.5, Unit = "ms" },
            new() { Label = () => "stale", Value = () => 1.25, Unit = "ms", Sub = true, Detail = true },
            new() { Label = () => "", Value = () => 1, Unit = "ms" }, // an unused row of a dynamic list
            new() { Kind = HudLineKind.Rule },
            new() { Label = () => "unmeasured", Value = () => double.NaN, Unit = "ms" }
        ];
        foreach (var line in lines) line.Capture();
        var after = new StringBuilder("[title]");
        HudPanel.AppendShown(after, lines, false);
        var detailed = new StringBuilder();
        HudPanel.AppendShown(detailed, lines, true);
        Assert.Multiple(() =>
        {
            Assert.That(after.ToString(), Is.EqualTo("[title]\n\n[passes]\nopaque: 3.50 ms"),
                "a panel after another starts a paragraph");
            Assert.That(detailed.ToString(), Is.EqualTo("[passes]\nopaque: 3.50 ms\n  stale: 1.25 ms"));
        });
    }

    // A bench averages the windows that had a number; a statistic that never had one in the whole bench is left out without
    // tripping an assertion
    [Test]
    public void ABenchMeanSkipsWindowsWithoutANumber()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        try
        {
            var current = 0.0;
            var line = new HudLine { Label = () => "pause", Value = () => current, Unit = "ms" };
            var share = new HudLine { Label = () => "mod", Value = () => 2, Percent = () => current, Unit = "ms" };
            var never = new HudLine { Label = () => "never", Value = () => double.NaN, Unit = "ms" };
            // a share known in fewer windows than its value (no frame time to divide by): each mean is over its own windows
            var partly = new HudLine
            {
                Label = () => "partly", Value = () => current, Percent = () => current > 5 ? current : double.NaN,
                Unit = "MB"
            };
            var shareless = new HudLine
            { Label = () => "time", Value = () => current, Percent = () => double.NaN, Unit = "ms" };
            foreach (var sample in (double[])[double.NaN, 4, double.PositiveInfinity, 6])
            {
                current = sample;
                foreach (var row in (HudLine[])[line, share, never, partly, shareless]) row.Accumulate();
            }

            Assert.Multiple(() =>
            {
                Assert.That(line.MeanText(), Is.EqualTo("pause: 5.00 ms"));
                Assert.That(share.MeanText(), Is.EqualTo("mod: 5.0 % 2.00 ms"));
                Assert.That(never.MeanText(), Is.Empty);
                Assert.That(partly.MeanText(), Is.EqualTo("partly: 6.0 % 5 MB"),
                    "the time of a window without a share counts");
                Assert.That(shareless.MeanText(), Is.EqualTo("time: 5.00 ms"),
                    "a row whose share never came keeps its time");
                Assert.That(logger.Lines, Is.Empty, "no assertion failed");
            });
        }
        finally
        {
            Contracts.Attach(null);
        }
    }

    // The panel reads a counter row once per refresh and the bench accumulates every interval. On an interval with a panel reading the
    // bench takes that one, on the others it reads itself: every interval's growth is in exactly one bench sample.
    [Test]
    public void TheBenchSharesTheRowsReadingWithThePanel()
    {
        double total = 0, frames = 0;
        Growth grown = new(() => total), of = new(() => frames);
        var line = new HudLine { Label = () => "per frame", Value = () => grown.Next() / of.Next(), Unit = "ms" };
        line.Capture(); // the first read is no rate
        line.ResetBench();
        foreach (var (added, due) in ((double, bool)[])[(10, true), (20, false), (60, false), (10, true)])
        {
            (total, frames) = (total + added, frames + 10);
            if (due) line.Capture();
            line.Accumulate();
        }

        Assert.Multiple(() =>
        {
            Assert.That(line.MeanText(), Is.EqualTo("per frame: 2.50 ms"), "1, 2, 6 and 1 per frame");
            Assert.That(line.ShownText(), Is.EqualTo("per frame: 1.00 ms"), "the panel's last reading");
        });
    }

    // Counting starts (F7, the counters switch, the first showing, a bench with the HUD hidden) and the rows take a new baseline: the first
    // refresh shows the span since instead of nothing, and the prime is no bench sample, so the bench's first interval is its own sample
    [Test]
    public void APrimedRowCoversTheSpanSinceCountingStarted()
    {
        double total = 0, frames = 0;
        Growth grown = new(() => total), of = new(() => frames);
        var line = new HudLine { Label = () => "per frame", Value = () => grown.Next() / of.Next(), Unit = "ms" };
        Counting.Hud = false;
        Counting.Hud = true;
        line.Prime();
        (total, frames) = (total + 40, frames + 10);
        line.Capture();
        var shown = line.ShownText();
        Counting.Hud = false;
        line.ResetBench(); // StartBench's order: the reset, then counting starts
        Counting.Hud = true;
        line.Prime();
        foreach (var (added, count) in ((double, double)[])[(60, 20), (10, 10)])
        {
            (total, frames) = (total + added, frames + count);
            line.Accumulate();
        }

        Counting.Hud = false;
        Assert.Multiple(() =>
        {
            Assert.That(shown, Is.EqualTo("per frame: 4.00 ms"));
            Assert.That(line.MeanText(), Is.EqualTo("per frame: 2.00 ms"), "3 and 1 per frame");
        });
    }

    // A row divides the growth of a feature's total by the growth of the frames or seconds since its own last read. Neither the first
    // read nor one across a pause of the HUD's counting (the HUD-only totals stood still while its frames went on, or the HUD was hidden
    // while a scripted bench kept the tessellation totals going), nor one after the totals restarted with a new world is a rate.
    [Test]
    public void GrowthIsTheSpanSinceTheRowLastRead()
    {
        var total = 10.0;
        var growth = new Growth(() => total);
        var first = growth.Next();
        total = 25;
        var grown = growth.Next();
        Counting.Hud = false; // a pause
        Counting.Hud = true;
        total = 40;
        var paused = growth.Next();
        total = 45;
        var again = growth.Next();
        total = 3;
        var restarted = growth.Next();
        Counting.Bench = true;
        Counting.Hud = false; // hidden, the bench counts on
        total = 30;
        Counting.Hud = true;
        var hidden = growth.Next();
        (Counting.Hud, Counting.Bench) = (false, false);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.NaN);
            Assert.That(grown, Is.EqualTo(15));
            Assert.That(paused, Is.NaN);
            Assert.That(again, Is.EqualTo(5));
            Assert.That(restarted, Is.NaN);
            Assert.That(hidden, Is.NaN);
        });
    }

    // A failure is reported once per call site, and a call site is its file too: Install at line 25 of two features are two sites
    [Test]
    [SuppressMessage("Minor Code Smell", "S3236", Justification = "stands in for call sites in two files")]
    public void AFailureIsReportedOncePerFileMemberAndLine()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        try
        {
            foreach (var file in (string[])["/src/SunOcclusion.cs", "/src/WindowSizeCache.cs", "/src/SunOcclusion.cs"])
                _ = Contracts.Assert(false, "harmony is null", "Install", 25, file);
            Assert.That(logger.Lines, Has.Count.EqualTo(2).And.Some.Contains("WindowSizeCache.cs, Install (line 25)"));
        }
        finally
        {
            Contracts.Attach(null);
        }
    }

    [TestCase(true, Build, "a", "a", "Verified", "aaaaaaa")]
    [TestCase(true, Build, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(true, Build, "a", "", "Unverified", Build)]
    [TestCase(true, Build, "", "a", "Unverified", Build)]
    [TestCase(true, Newer, "a", "a", "Outdated", Newer)]
    [TestCase(true, Newer, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(false, Newer, "a", "", "Outdated", Newer)]
    [TestCase(false, "", "", "", "NoRelease", Build)]
    public void GitHubsAnswerMapsToOneState(bool listed, string newest, string installed, string published,
        string state,
        string detail)
    {
        var report = UpdateCheck.Judge(Build, listed, newest, Hash(installed), listed ? Hash(published) : "", "");
        Assert.Multiple(() =>
        {
            Assert.That(report.State, Is.EqualTo(Enum.Parse<UpdateState>(state)));
            Assert.That(report.Detail, Is.EqualTo(detail));
            Assert.That(report.Tag, Is.EqualTo(listed ? Build : ""));
        });
    }

    // The checksum window's verdict: a failed or running check says so, otherwise the hashes decide, then what is missing
    [TestCase("Checking", "v1", "a", "a", "verify-checking")]
    [TestCase("Failed", "v1", "a", "a", "verify-failed")]
    [TestCase("Verified", "v1", "a", "a", "verify-match")]
    [TestCase("Outdated", "v1", "a", "b", "verify-mismatch")]
    [TestCase("Unverified", "", "a", "", "verify-norelease")]
    [TestCase("Unverified", "v1", "", "b", "verify-nofile")]
    [TestCase("Unverified", "v1", "a", "", "verify-nochecksum")]
    public void TheChecksumWindowNamesWhatTheComparisonFound(string state, string tag, string installed,
        string published, string key)
    {
        var report = new UpdateReport(Enum.Parse<UpdateState>(state), "", tag, "", Hash(installed), Hash(published));
        Assert.That(HudVerifyDialog.Verdict(report).Key, Is.EqualTo(key));
    }

    private static string Hash(string digit)
    {
        return digit switch { "" => "", "a" => Installed, _ => Other };
    }
}

using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

[NonParallelizable]
public sealed class ProfilerPoolTests
{
    private static readonly string[] InOrder = ["beginrenderstage-Opaque", "a", "b", "beginrenderstage-OIT", "c", "end"];
    private FrameProfilerUtil? _before;

    [SetUp]
    public void Keep() => _before = ScreenManager.FrameProfiler;

    [TearDown]
    public void PutBack() => ScreenManager.FrameProfiler = _before!;

    private static FrameProfilerUtil Profiler() => new(static _ => { }) { Enabled = true };

    // One frame: its stage marks with the codes between them, in that order
    private static ProfileEntryRange Frame(FrameProfilerUtil profiler, params string[] codes)
    {
        profiler.Begin();
        foreach (var code in codes) profiler.Mark(code);
        profiler.End();
        return profiler.PrevRootEntry;
    }

    // A code first seen in a later frame stands between the stage marks it came between, not behind the codes the kept set
    // saw before; frame 3 reuses frame 1's set and its entries, counted from zero
    [Test]
    public void ACodeFirstMarkedLaterLandsInItsStage()
    {
        using var harmony = new TestHarmony("komet-test-profilerpool");
        ProfilerPool.Install(harmony);
        var profiler = ScreenManager.FrameProfiler = Profiler();
        var first = Frame(profiler, "beginrenderstage-Opaque", "a", "beginrenderstage-OIT", "c");
        var entries = first.Marks.Values.ToList();
        _ = Frame(profiler, "beginrenderstage-Opaque", "a", "beginrenderstage-OIT", "c");
        var third = Frame(profiler, "beginrenderstage-Opaque", "a", "b", "beginrenderstage-OIT", "c");
        Assert.Multiple(() =>
        {
            Assert.That(ProfilerPool.Patched && ProfilerPool.Recycling, Is.True);
            Assert.That(third.Marks.Values.Count(entries.Contains), Is.EqualTo(entries.Count), "frame 1's entries again");
            Assert.That(third.Marks, Is.SameAs(first.Marks), "the kept set");
            Assert.That(third.Marks.Keys, Is.EqualTo(InOrder));
            Assert.That(third.Marks.Values.Select(e => e.CallCount), Is.All.EqualTo(1));
        });
    }

    // The server's and other threads' profilers keep their own dictionaries every frame
    [Test]
    public void OnlyTheClientFrameProfilerIsPooled()
    {
        using var harmony = new TestHarmony("komet-test-profilerpool-other");
        ProfilerPool.Install(harmony);
        ScreenManager.FrameProfiler = Profiler();
        var other = Profiler();
        var first = Frame(other, "a");
        _ = Frame(other, "a");
        Assert.That(Frame(other, "a").Marks, Is.Not.SameAs(first.Marks));
    }
}

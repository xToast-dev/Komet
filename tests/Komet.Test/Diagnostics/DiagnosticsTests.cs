using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

public sealed class DiagnosticsTests
{
    [TearDown]
    public void SwitchModTimesOff() => ModTimes.Enabled = false;

    internal static bool PatchedBy(MethodBase method, Harmony harmony) =>
        Harmony.GetPatchInfo(method) is { } info && info.Owners.Contains(harmony.Id);

    [Test]
    public void TheFrameClockPatchesTheRenderFrameHandler()
    {
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        Assert.That(frame, Is.Not.Null,
            "ClientPlatformWindows.window_RenderFrame is gone, the HUD records the engine's dt alone");
        using var harmony = new TestHarmony("komet-test-frameclock");
        FrameClock.Install(harmony);
        var patches = Harmony.GetPatchInfo(frame);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(patches?.Prefixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
            Assert.That(patches?.Postfixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
        }
    }

    private static List<RenderHandler?>[] Stage(params Action<float>[] actions)
    {
        var stages = Enumerable.Range(0, Enum.GetValues<EnumRenderStage>().Length)
            .Select(_ => new List<RenderHandler?>()).ToArray();
        stages[(int)EnumRenderStage.Opaque].AddRange(actions.Select(action =>
            new RenderHandler { Renderer = new DummyRenderer { action = action }, ProfilingName = "dummy" }));
        return stages;
    }

    // A hang dump must name the last step where work stopped, not sixteen copies of a per-frame mark
    [Test]
    public void TheHangStepsKeepTheLastDistinctStepsNewestFirst()
    {
        HangWatch.Mark("pacing: begin");
        HangWatch.Mark("pacing: begin");
        HangWatch.Mark("rebuild: begin");
        HangWatch.Mark("rebuild: cleared");
        var steps = HangWatch.Steps().Split('\n');
        Assert.Multiple(() =>
        {
            Assert.That(steps[0], Does.EndWith("rebuild: cleared"));
            Assert.That(steps[1], Does.EndWith("rebuild: begin"));
            Assert.That(steps[2], Does.EndWith("pacing: begin"));
            Assert.That(steps, Has.Length.LessThanOrEqualTo(16));
        });
    }

    private static void Cheap(float _)
    {
        // no work: the last of the three
    }

    private static void Dear(float _) => Busy.Spin(12);

    private static void Middle(float _) => Busy.Spin(1);

    private static void Install(Harmony harmony) =>
        ModTimes.Install(harmony, DispatchProxy.Create<IModLoader, OneModLoader>(), new QuietLogger());

    // Off by default the engine runs untouched; after Ready the first switch-on patches it. The next session's install starts over:
    // switched on by the saved settings in StartClientSide, it waits for that session's Ready. Ends patched-and-removed, so later
    // switch-ons in this process stay inert.
    [Test]
    public void TheModTimesPatchTheEngineOnlyOnceSwitchedOnAndReady()
    {
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        using var harmony = new TestHarmony("komet-test-modtimes");
        ModTimes.Enabled = false;
        Install(harmony);
        ModTimes.Ready();
        var before = PatchedBy(stage, harmony);
        ModTimes.Enabled = true;
        var enabled = PatchedBy(stage, harmony);
        harmony.UnpatchAll(harmony.Id);
        Install(harmony);
        var early = PatchedBy(stage, harmony);
        ModTimes.Ready();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.False, "not patched by default");
            Assert.That(enabled, Is.True);
            Assert.That(early, Is.False, "switched on before the level is finalized, it waits");
            Assert.That(PatchedBy(stage, harmony), Is.True, "the new session's Ready");
        }
    }

}

// The lows are taken over the frames actually recorded, so an empty or short history must report nothing rather than a number
public sealed class FrameStatsTests
{
    private const int History = 2000;

    private static FrameStats Filled(int frames, float ms = 10f)
    {
        var stats = new FrameStats();
        for (var i = 0; i < Math.Min(frames, 4 * History); i++)
        {
            stats.Record(ms / 1000f, 0, true);
        }

        stats.SampleWindow();
        return stats;
    }

    [TestCase(0)]
    [TestCase(99)]
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

    [Test]
    public void TheRingKeepsOnlyTheLastHistoryFrames()
    {
        var stats = new FrameStats();
        stats.Record(0.5f, 0, true); // falls out again
        for (var i = 0; i < History; i++)
        {
            stats.Record(0.01f, 0, true);
        }

        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(History));
            Assert.That(stats.WorstMs, Is.EqualTo(10f).Within(0.01f));
        });
    }

}

public sealed class DrmMemoryTests
{
    // amdgpu's fdinfo: the client id, its VRAM and GTT; a file without a client id is no client
    [Test]
    public void ParsesAClient()
    {
        var client = DrmMemory.Parse([
            "pos:\t0", "drm-driver:\tamdgpu", "drm-client-id:\t42", "drm-memory-vram:\t6291456 KiB",
            "drm-memory-gtt:\t\t20480 KiB"
        ]);
        Assert.That(client, Is.EqualTo(new DrmMemory.Client(42, "amdgpu", 6291456, 20480)));
        Assert.That(DrmMemory.Parse(["pos:\t0", "flags:\t02"]), Is.Null);
        Assert.That(DrmMemory.Text([client!.Value, new DrmMemory.Client(7, "amdgpu", 1024 * 1024, 0)], 6100),
            Is.EqualTo("Vulkan 6144 MB (GTT 20 MB), OpenGL 1024 MB (GTT 0 MB)"));
    }
}

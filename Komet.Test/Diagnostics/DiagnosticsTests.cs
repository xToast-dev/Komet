using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Test.Diagnostics;

// The HUD's own machinery: the frame clock's seam, the panel queue, the mod times and the mod walk
public sealed class DiagnosticsTests
{
    private static readonly int[] RoundRobin = [1, 3, 5, 1];

    private static bool PatchedBy(MethodBase method, Harmony harmony)
    {
        return Harmony.GetPatchInfo(method) is { } info && info.Owners.Contains(harmony.Id);
    }

    [Test]
    public void TheFrameClockPatchesTheRenderFrameHandler()
    {
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        Assert.That(frame, Is.Not.Null,
            "ClientPlatformWindows.window_RenderFrame is gone, the HUD records the engine's dt alone");
        var harmony = new Harmony("komet-test-frameclock");
        try
        {
            FrameClock.Install(harmony);
            var patches = Harmony.GetPatchInfo(frame);
            Assert.Multiple(() =>
            {
                Assert.That(patches?.Prefixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
                Assert.That(patches?.Postfixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void TheQueueHandsOutEachPendingPanelOnceRoundRobin()
    {
        var queue = new PanelQueue(8);
        foreach (var panel in (int[])[5, 1, 3, 1]) queue.Add(panel);
        var first = queue.Next();
        queue.Add(1); // due again before the others were drawn
        var order = new List<int> { first };
        for (var i = 0; i < 8; i++) order.Add(queue.Next());
        Assert.Multiple(() =>
        {
            Assert.That(order.Take(4), Is.EqualTo(RoundRobin), "the cursor moves on, so 1 waits behind 3 and 5");
            Assert.That(order.Skip(4), Is.All.EqualTo(-1));
        });
    }

    [Test]
    public void ARemovedPanelIsNotDrawnAgain()
    {
        var queue = new PanelQueue(4);
        queue.Add(2);
        queue.Remove(2);
        Assert.That(queue.Next(), Is.EqualTo(-1));
    }

    private static List<RenderHandler>[] Stage(params Action<float>[] actions)
    {
        var stages = Enumerable.Range(0, Enum.GetValues<EnumRenderStage>().Length)
            .Select(_ => new List<RenderHandler>()).ToArray();
        stages[(int)EnumRenderStage.Opaque].AddRange(actions.Select(action =>
            new RenderHandler { Renderer = new DummyRenderer { action = action }, ProfilingName = "dummy" }));
        return stages;
    }

    // Every firepit registers a renderer of its own; past the bound the engine's loop runs them all, unprofiled
    [Test]
    public void AStageWithMoreRenderersThanTheBoundGoesBackToTheEngine()
    {
        var ran = new List<int>();
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));

        Action<float> Renderer(int index)
        {
            return _ => ran.Add(index);
        }

        ModTimes.Enabled = true;
        try
        {
            Assert.Multiple(() =>
            {
                var many = Stage([.. Enumerable.Range(0, ModTimes.MaxRenderers + 76).Select(Renderer)]);
                Assert.That(ModTimes.RenderStage(game, many, EnumRenderStage.Opaque, 0.016f), Is.True,
                    "the engine's own loop runs");
                Assert.That(ran, Is.Empty, "and nothing ran twice");
            });
            Assert.Multiple(() =>
            {
                Assert.That(
                    ModTimes.RenderStage(game, Stage([.. Enumerable.Range(0, 10).Select(Renderer)]),
                        EnumRenderStage.Opaque, 0.016f),
                    Is.False);
                Assert.That(ran, Is.EqualTo(Enumerable.Range(0, 10)), "within the bound every renderer runs, in order");
            });
        }
        finally
        {
            ModTimes.Enabled = false;
        }
    }

    // Per mod its dearest renderers and listeners, found in one pass over the entries; here every renderer is Komet.Test's
    [Test]
    public void TheModTimesRankEachModsDearestEntries()
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        ModTimes.Enabled = true;
        try
        {
            _ = ModTimes.RenderStage(game, Stage(Cheap, Dear, Middle), EnumRenderStage.Opaque, 0.016f);
            var times = new ModTimes();
            times.Update(1, 1f);
            Assert.Multiple(() =>
            {
                Assert.That(times.ModName(0), Is.EqualTo(typeof(DiagnosticsTests).Assembly.GetName().Name));
                Assert.That(times.DetailName(0, 0), Does.StartWith("DiagnosticsTests.Dear "));
                Assert.That(times.DetailName(0, 1), Does.StartWith("DiagnosticsTests.Middle "));
                Assert.That(times.DetailMs(0, 0), Is.GreaterThan(times.DetailMs(0, 1)));
                Assert.That(times.ModMs(0), Is.GreaterThanOrEqualTo(times.DetailMs(0, 0) + times.DetailMs(0, 1)));
            });
        }
        finally
        {
            ModTimes.Enabled = false;
        }
    }

    private static void Cheap(float dt)
    {
        // no work: the last of the three
    }

    private static void Dear(float dt)
    {
        Busy.Spin(12);
    }

    private static void Middle(float dt)
    {
        Busy.Spin(1);
    }

    // RenderStage replays ClientEventManager.TriggerRenderStage; on another body the install leaves the renderers to the engine
    [Test]
    public void TheReplayedRenderLoopIsTheInstalledEngines()
    {
        var found = EngineShape.Of([
            AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage))
        ]);
        Assert.That(found, Is.EqualTo(ModTimes.Shape), $"TriggerRenderStage changed: 0x{found:X16}");
    }

    private static void Install(Harmony harmony)
    {
        ModTimes.Install(harmony, DispatchProxy.Create<IModLoader, OneModLoader>(), new SilentLogger());
    }

    // Off by default the engine runs untouched; after Ready the first switch-on patches it. The next session's install starts over:
    // switched on by the saved settings in StartClientSide, it waits for that session's Ready. Ends patched-and-removed, so later
    // switch-ons in this process stay inert.
    [Test]
    public void TheModTimesPatchTheEngineOnlyOnceSwitchedOnAndReady()
    {
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        var harmony = new Harmony("komet-test-modtimes");
        try
        {
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
            Assert.Multiple(() =>
            {
                Assert.That(before, Is.False, "not patched by default");
                Assert.That(enabled, Is.True);
                Assert.That(early, Is.False, "switched on before the level is finalized, it waits");
                Assert.That(PatchedBy(stage, harmony), Is.True, "the new session's Ready");
            });
        }
        finally
        {
            ModTimes.Enabled = false;
            harmony.UnpatchAll(harmony.Id);
        }
    }

    // A mod that starts after Komet puts a transpiler on the loop RenderStage would skip; Ready sees it, leaves the renderers to that
    // loop and still times the listeners
    [Test]
    public void ALaterModsTranspilerOnTheRenderLoopLeavesOnlyTheRenderersOut()
    {
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        var entity = AccessTools.Method(typeof(GameTickListener), nameof(GameTickListener.OnTriggered));
        var block = AccessTools.Method(typeof(GameTickListenerBlock), nameof(GameTickListenerBlock.OnTriggered));
        var harmony = new Harmony("komet-test-modtimes");
        var other = new Harmony("komet-test-othermod");
        try
        {
            ModTimes.Enabled = false;
            Install(harmony);
            ModTimes.Enabled = true;
            _ = other.Patch(stage, transpiler: new HarmonyMethod(typeof(DiagnosticsTests), nameof(Same)));
            ModTimes.Ready();
            Assert.Multiple(() =>
            {
                Assert.That(PatchedBy(stage, harmony), Is.False, "the other mod's loop runs");
                Assert.That(PatchedBy(entity, harmony), Is.True, "the entity listeners are timed");
                Assert.That(PatchedBy(block, harmony), Is.True, "and the block listeners");
            });
        }
        finally
        {
            ModTimes.Enabled = false;
            harmony.UnpatchAll(harmony.Id);
            other.UnpatchAll(other.Id);
        }
    }

    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions)
    {
        return instructions;
    }

    // The walk is refused until Komet's own patches are registered. The settings load in StartClientSide, before Komet patches
    // anything and before the mods after it do, so the HUD asks for its walk only once the level is finalized.
    [Test]
    public void TheModWalkNeedsKometsOwnPatchesAndSeesThem()
    {
        var loader = DispatchProxy.Create<IModLoader, OneModLoader>();
        Assert.That(ModStats.Walk(loader, "komet"), Is.Null, "nothing is patched by komet yet");
        var harmony = new Harmony("komet");
        try
        {
            _ = harmony.Patch(AccessTools.Method(typeof(DiagnosticsTests), nameof(Patched)),
                new HarmonyMethod(typeof(DiagnosticsTests), nameof(Before)));
            var snapshot = ModStats.Walk(loader, "komet");
            Assert.That(snapshot, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot!.Mods, Is.EqualTo(1));
                Assert.That(snapshot.ModNames[0], Is.EqualTo("Komet 1.2.3"));
                Assert.That(snapshot.PatchedMethods, Is.GreaterThanOrEqualTo(1));
                Assert.That(snapshot.OwnerList, Does.Contain(("komet", 1)));
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Patched()
    {
        return 1;
    }

    private static void Before()
    {
        // a prefix that only has to be registered under the owner "komet"
    }
}

// IModLoader with one mod; only Mods is read by the walk
public class OneModLoader : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "get_Mods") throw new NotSupportedException(targetMethod?.Name);
        var mod = new TestMod();
        typeof(Mod).GetProperty(nameof(Mod.Info))!.SetValue(mod,
            new ModInfo { Name = "Komet", Version = "1.2.3", ModID = "komet" });
        return new Mod[] { mod };
    }

    private sealed class TestMod : Mod;
}

internal sealed class SilentLogger : LoggerBase
{
    protected override void LogImpl(EnumLogType logType, string format, params object[] args)
    {
        // the test asserts on the patches, not on the log
    }
}

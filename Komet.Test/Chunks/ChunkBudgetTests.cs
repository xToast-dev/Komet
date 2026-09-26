using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Chunks;

// Where the cap goes in OnBeforeFrame's IL, that any other shape keeps the engine's loop, and how the cap ends a frame's uploads
public sealed class ChunkBudgetTests
{
    private static readonly MethodInfo Limiter =
        AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.ChunkVerticesUploadRateLimiter));

    private static readonly MethodInfo Sort =
        AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<>.Sort));

    private static readonly MethodInfo Cap = AccessTools.Method(typeof(ChunkBudget), nameof(ChunkBudget.Cap));

    [TearDown]
    public void Reset()
    {
        (ChunkBudget.CapMillis, Counting.Hud, RuntimeStats.chunksAwaitingPooling) =
            (ChunkBudget.DefaultCapMillis, false, 0);
        ChunkBudget.Begin();
    }

    // A world-join backlog of finished meshes raises the cap until the queue is nearly empty again; flight-sized queues never do
    [TestCase(ChunkBudget.DefaultCapMillis, ExpectedResult = "False True True False")]
    [TestCase(ChunkBudget.Uncapped, ExpectedResult = "False True True False")]
    public string ABacklogBoostsTheCapWithHysteresis(int cap)
    {
        ChunkBudget.CapMillis = cap;
        var seen = new List<bool>();
        foreach (var waiting in (int[])[300, 2000, 150, 50])
        {
            RuntimeStats.chunksAwaitingPooling = waiting;
            ChunkBudget.Begin();
            seen.Add(ChunkBudget.Boosted);
        }

        return string.Join(' ', seen);
    }

    [Test]
    public void InstallCapsTheUploadLoop()
    {
        var harmony = new Harmony("komet-test-chunkbudget");
        try
        {
            ChunkBudget.Install(harmony);
            Assert.That(ChunkBudget.Capped, Is.True, "the transpiler found no single loop condition to cap");
            var frame = AccessTools.Method(typeof(ChunkTesselatorManager),
                nameof(ChunkTesselatorManager.OnBeforeFrame));
            Assert.That(Harmony.GetPatchInfo(frame)?.Prefixes.Select(p => p.PatchMethod.Name),
                Does.Contain(nameof(ChunkBudget.Begin)),
                "without the prefix no frame ever gets a deadline and the cap never ends the loop");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    // The shape of OnBeforeFrame around num3: stored after the limiter read, read by the early return (a forward blt before the lock) and
    // by the loop condition after Sort() (a blt back into the body). Labels need a generator, a default Label would equal every other one.
    private static List<CodeInstruction> LoopBody(int loopSites = 1, bool sort = true, int loopLocal = 4, int reads = 1)
    {
        var il = new DynamicMethod("labels", null, null).GetILGenerator();
        var (resume, body) = (il.DefineLabel(), il.DefineLabel());
        var code = new List<CodeInstruction>();
        for (var i = 0; i < reads; i++)
            code.AddRange([new CodeInstruction(OpCodes.Call, Limiter), new CodeInstruction(OpCodes.Stloc_S, 4)]);
        code.AddRange([
            new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, 4),
            new CodeInstruction(OpCodes.Blt_S, resume),
            new CodeInstruction(OpCodes.Ret), new CodeInstruction(OpCodes.Ldnull) { labels = [resume] }
        ]);
        if (sort) code.Add(new CodeInstruction(OpCodes.Callvirt, Sort));
        code.Add(new CodeInstruction(OpCodes.Nop) { labels = [body] });
        for (var i = 0; i < loopSites; i++)
            code.AddRange([
                new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, loopLocal),
                new CodeInstruction(OpCodes.Blt, body)
            ]);
        code.Add(new CodeInstruction(OpCodes.Ret));
        return code;
    }

    [Test]
    public void TheCapGoesIntoTheLoopConditionOnly()
    {
        var result = ChunkBudget.Rewrite(LoopBody());
        var at = result.FindIndex(code => code.Calls(Cap));
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.True);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.EqualTo(1));
            Assert.That(result.Count(code => code.Calls(Limiter)), Is.EqualTo(1), "the setting is still the engine's");
            Assert.That(result[3].opcode, Is.EqualTo(OpCodes.Ldloc_S), "the early return reads num3 untouched");
            Assert.That(result[4].opcode, Is.EqualTo(OpCodes.Blt_S));
            Assert.That(result[at - 1].opcode, Is.EqualTo(OpCodes.Ldloc_S), "the cap consumes num3");
            Assert.That(result[at + 1].opcode, Is.EqualTo(OpCodes.Blt), "and feeds the loop's own branch");
            Assert.That(result[at].labels, Is.Empty);
        });
    }

    // Two loops on num3, none, no Sort(), a loop on another local, no read of the limiter or two: the method changed, the loop keeps the
    // engine's IL
    [TestCase(2, true, 4, 1)]
    [TestCase(0, true, 4, 1)]
    [TestCase(1, false, 4, 1)]
    [TestCase(1, true, 5, 1)]
    [TestCase(1, true, 4, 0)]
    [TestCase(1, true, 4, 2)]
    public void AnyOtherShapeKeepsTheEnginesLoop(int loopSites, bool sort, int loopLocal, int reads)
    {
        var body = LoopBody(loopSites, sort, loopLocal, reads);
        var result = ChunkBudget.Rewrite(body);
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.False);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.Zero);
            Assert.That(result, Has.Count.EqualTo(body.Count));
        });
    }

    // On the game's own OnBeforeFrame: one cap, right between the loop condition's ldloc of num3 and its backward blt, after Sort()
    [Test]
    public void TheCapLandsInOnBeforeFramesLoopCondition()
    {
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        var result = ChunkBudget.Rewrite(PatchProcessor.GetOriginalInstructions(frame));
        var (at, sorted) = (result.FindIndex(code => code.Calls(Cap)), result.FindIndex(code => code.Calls(Sort)));
        var stored = result.FindIndex(result.FindIndex(code => code.Calls(Limiter)), code => code.IsStloc());
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.True);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.EqualTo(1));
            Assert.That(at, Is.GreaterThan(sorted));
            Assert.That(result[at - 1].IsLdloc() && result[at - 1].LocalIndex() == result[stored].LocalIndex(), Is.True,
                "the cap takes num3, the local the limiter expression is stored into");
            Assert.That(result[at + 1].opcode == OpCodes.Blt || result[at + 1].opcode == OpCodes.Blt_S, Is.True);
            var target = (Label)result[at + 1].operand;
            var into = result.FindIndex(code => code.labels.Contains(target));
            Assert.That(into, Is.InRange(sorted + 1, at - 1), "the branch goes back into the loop body");
        });
    }

    private static void Spin(int millis)
    {
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < millis) Thread.SpinWait(64);
    }

    [Test]
    public void UncappedKeepsTheEnginesBudget()
    {
        ChunkBudget.CapMillis = ChunkBudget.Uncapped;
        ChunkBudget.Begin();
        Spin(3);
        Assert.That((ChunkBudget.Cap(1000), ChunkBudget.Cap(1000), ChunkBudget.Cap(1000)),
            Is.EqualTo((1000, 1000, 1000)));
    }

    // The deadline can be gone before the loop starts – the priority queue counts against it – and one chunk still goes through
    [Test]
    public void TheFirstChunkAlwaysGoesAndThenTheCapEndsTheLoop()
    {
        (ChunkBudget.CapMillis, Counting.Hud) = (1, true);
        var hits = ChunkBudget.CapHits;
        ChunkBudget.Begin();
        Spin(3);
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(1000), "the test in front of the first chunk");
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(int.MinValue),
                "uploaded < int.MinValue is false: the loop ends");
            Assert.That(ChunkBudget.CapHits - hits, Is.EqualTo(1));
        });
        ChunkBudget.Begin();
        Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(1000), "the next frame starts over");
    }

    [Test]
    public void ACapThatHasNotRunOutKeepsTheBudget()
    {
        ChunkBudget.CapMillis = ChunkBudget.MaxCapMillis;
        ChunkBudget.Begin();
        Assert.That((ChunkBudget.Cap(1000), ChunkBudget.Cap(1000), ChunkBudget.Cap(1000)),
            Is.EqualTo((1000, 1000, 1000)));
    }
}

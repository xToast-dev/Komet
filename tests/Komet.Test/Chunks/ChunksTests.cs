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
        using var harmony = new TestHarmony("komet-test-chunkbudget");
        ChunkBudget.Install(harmony);
        Assert.That(ChunkBudget.Capped, Is.True, "the transpiler found no single loop condition to cap");
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        Assert.That(Harmony.GetPatchInfo(frame)?.Prefixes.Select(p => p.PatchMethod.Name),
            Does.Contain(nameof(ChunkBudget.Begin)),
            "without the prefix no frame ever gets a deadline and the cap never ends the loop");
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
        code.AddRange([new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, 4),
            new CodeInstruction(OpCodes.Blt_S, resume),
            new CodeInstruction(OpCodes.Ret), new CodeInstruction(OpCodes.Ldnull) { labels = [resume] }]);
        if (sort) code.Add(new CodeInstruction(OpCodes.Callvirt, Sort));
        code.Add(new CodeInstruction(OpCodes.Nop) { labels = [body] });
        for (var i = 0; i < loopSites; i++)
            code.AddRange([new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, loopLocal),
                new CodeInstruction(OpCodes.Blt, body)]);
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

    [Test]
    public void UncappedKeepsTheEnginesBudget()
    {
        ChunkBudget.CapMillis = ChunkBudget.Uncapped;
        ChunkBudget.Begin();
        Busy.Spin(3);
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
        Busy.Spin(3);
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

// The mirror behind the patched ClientWorldMap.GetChunk follows the engine's dictionary through loads and unloads
public sealed class ChunkLookupTests
{
    // More chunks in one unload packet than the old bound of 8192: the server sends a tick's unloads in one packet, however many
    [Test]
    public void AnUnloadPacketLeavesNoChunkBehind()
    {
        const int n = 10000;
        using var rig = new ChunkRig();
        var chunks = (Dictionary<long, ClientChunk>)ChunkRig.Get(rig.Map, "chunks");
        var chunk = ClientChunk.CreateNew(rig.Pool);
        var (loaded, unloading) = (AccessTools.Method(typeof(ChunkLookup), "Loaded"),
            AccessTools.Method(typeof(ChunkLookup), "Unloading"));
        int[] xs = new int[n], ys = new int[n], zs = new int[n];
        var harmony = new Harmony("komet-test-chunklookup");
        try
        {
            ChunkLookup.Install(harmony);
            for (var i = 0; i < n; i++)
            {
                (xs[i], zs[i]) = (i % 100, i / 100);
                chunks[ChunkRig.Key(xs[i], 0, zs[i])] = chunk; // what loadChunkMT inserts, then its postfix stores
                _ = loaded.Invoke(null, [rig.Map, new Packet_ServerChunk { X = xs[i], Z = zs[i] }, chunk]);
            }

            Assert.That(rig.Map.GetChunk(ChunkRig.Key(99, 0, 99)), Is.SameAs(chunk));
            var packet = new Packet_UnloadServerChunk();
            (packet.X, packet.XCount, packet.Y, packet.YCount, packet.Z, packet.ZCount) = (xs, n, ys, n, zs, n);
            // the prefix, then the engine
            _ = unloading.Invoke(null, [rig.Game, new Packet_Server { Id = 11, UnloadChunk = packet }]);
            chunks.Clear();
            var left = Enumerable.Range(0, n).Count(i => rig.Map.GetChunk(ChunkRig.Key(xs[i], 0, zs[i])) is not null);
            Assert.That(left, Is.Zero, "an unloaded chunk was still answered from the mirror");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ChunkLookup.Clear();
        }
    }
}

// The rewrite runs against the game's real IL: installing it makes Harmony compile the replacement, so a malformed rewrite fails here
// instead of in a world, and a game update that changes the method's shape turns the feature off here first.
public sealed class ChunkThreadClosureTests
{
    [Test]
    public void ChunkThreadClosureInstalls()
    {
        using var harmony = new TestHarmony("komet-test-chunkthreadclosure");
        ChunkThreadClosure.Install(harmony);
        Assert.That(ChunkThreadClosure.Rewritten, Is.True, "the closure prologue or its uses no longer match");
    }

    // The closure is built only where the delegate needs it, and nothing reads chunkRequest back out of it any more
    [Test]
    public void ChunkThreadClosureIsBuiltOnlyForTheDelegate()
    {
        var target = ChunkThreadClosure.Target();
        Assert.That(target, Is.Not.Null);
        var code = ChunkThreadClosure.Rewrite(PatchProcessor.GetOriginalInstructions(target));
        var builds = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.opcode == OpCodes.Newobj &&
                           pair.instruction.operand is ConstructorInfo { DeclaringType.Name: var name } &&
                           name.Contains("DisplayClass", StringComparison.Ordinal))
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(ChunkThreadClosure.Rewritten, Is.True);
            Assert.That(builds, Has.Count.EqualTo(1));
            Assert.That(builds[0].index, Is.GreaterThan(100), "the closure is still built in the prologue");
            Assert.That(code[builds[0].index + 7].opcode, Is.EqualTo(OpCodes.Ldftn),
                "the fresh closure must go straight into the delegate");
            Assert.That(code.Count(instruction => instruction.operand is FieldInfo { Name: "chunkRequest" } &&
                instruction.opcode == OpCodes.Ldfld), Is.Zero);
        });
    }
}

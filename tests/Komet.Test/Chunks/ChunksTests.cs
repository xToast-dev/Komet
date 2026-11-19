using Komet.Chunks;
using Komet.Core;

namespace Komet.Test.Chunks;

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
        ChunkBudget.Yield = true;
        ChunkBudget.Begin();
    }

    [Test]
    public void InstallCapsTheUploadLoop()
    {
        using var harmony = new TestHarmony("komet-test-chunkbudget");
        ChunkBudget.Install(harmony, null);
        Assert.That(ChunkBudget.Capped, Is.True, "the transpiler found no single loop condition to cap");
        Assert.That(ChunkBudget.Ceiled, Is.True, "the transpiler found no single priority loop condition to gate");
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
        {
            code.AddRange([new CodeInstruction(OpCodes.Call, Limiter), new CodeInstruction(OpCodes.Stloc_S, 4)]);
        }

        code.AddRange([
            new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, 4),
            new CodeInstruction(OpCodes.Blt_S, resume),
            new CodeInstruction(OpCodes.Ret), new CodeInstruction(OpCodes.Ldnull) { labels = [resume] }
        ]);
        if (sort) code.Add(new CodeInstruction(OpCodes.Callvirt, Sort));

        code.Add(new CodeInstruction(OpCodes.Nop) { labels = [body] });
        for (var i = 0; i < loopSites; i++)
        {
            code.AddRange([
                new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, loopLocal),
                new CodeInstruction(OpCodes.Blt, body)
            ]);
        }

        code.Add(new CodeInstruction(OpCodes.Ret));
        return code;
    }

    // Two loops on num3, none, no Sort(), a loop on another local, no read of the limiter or two: the method changed, the loop keeps the
    // engine's IL
    [TestCase(2, true, 4, 1)]
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
    public void TheFingerprintGuardsTheInstall()
    {
        using var harmony = new TestHarmony("komet-test-chunkbudget-shape");
        ChunkBudget.Install(harmony, null, ChunkBudget.Fingerprint ^ 1);
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        Assert.Multiple(() =>
        {
            Assert.That((ChunkBudget.Capped, ChunkBudget.Ceiled), Is.EqualTo((false, false)));
            Assert.That(Harmony.GetPatchInfo(frame)?.Owners ?? [], Does.Not.Contain("komet-test-chunkbudget-shape"));
        });
    }
}

// The mirror behind the patched ClientWorldMap.GetChunk follows the engine's dictionary through loads and unloads. A chunk that is not
// loaded is answered on the main thread without chunksLock, but only while the mirror holds every chunk the dictionary does: a chunk
// the mirror never saw sends the lookup back to the engine, and so does any other thread.
public sealed class ChunkLookupTests
{
    private static readonly MethodInfo Loaded = AccessTools.Method(typeof(ChunkLookup), "Loaded");
    private static readonly MethodInfo Unloading = AccessTools.Method(typeof(ChunkLookup), "Unloading");
    private ClientChunk _chunk = null!;
    private Dictionary<long, ClientChunk> _chunks = null!;
    private TestHarmony _harmony = null!;
    private int _mainThread;
    private ChunkRig _rig = null!;

    [SetUp]
    public void Install()
    {
        (_mainThread, _rig) = (RuntimeEnv.MainThreadId, new ChunkRig());
        (_chunks, _chunk) = ((Dictionary<long, ClientChunk>)ChunkRig.Get(_rig.Map, "chunks"),
            ClientChunk.CreateNew(_rig.Pool));
        _harmony = new TestHarmony("komet-test-chunklookup");
        ChunkLookup.Install(_harmony);
    }

    [TearDown]
    public void Uninstall()
    {
        _harmony.Dispose();
        ChunkLookup.Clear();
        _rig.Dispose();
        RuntimeEnv.MainThreadId = _mainThread;
    }

    // What loadChunkMT inserts, then its postfix stores
    private void Load(int x, int z)
    {
        _chunks[ChunkRig.Key(x, 0, z)] = _chunk;
        _ = Loaded.Invoke(null, [_rig.Map, new Packet_ServerChunk { X = x, Z = z }, _chunk]);
    }

    // More chunks in one unload packet than the old bound of 8192: the server sends a tick's unloads in one packet, however many
    [Test]
    public void AnUnloadPacketLeavesNoChunkBehind()
    {
        const int n = 10000;
        int[] xs = [.. Enumerable.Range(0, n).Select(i => i % 100)];
        int[] zs = [.. Enumerable.Range(0, n).Select(i => i / 100)];
        for (var i = 0; i < n; i++)
        {
            Load(xs[i], zs[i]);
        }

        Assert.That(_rig.Map.GetChunk(ChunkRig.Key(99, 0, 99)), Is.SameAs(_chunk));
        var packet = new Packet_UnloadServerChunk();
        (packet.X, packet.XCount, packet.Y, packet.YCount, packet.Z, packet.ZCount) = (xs, n, new int[n], n, zs, n);
        // the prefix, then the engine
        _ = Unloading.Invoke(null, [_rig.Game, new Packet_Server { Id = 11, UnloadChunk = packet }]);
        _chunks.Clear();
        var left = Enumerable.Range(0, n).Count(i => _rig.Map.GetChunk(ChunkRig.Key(xs[i], 0, zs[i])) is not null);
        Assert.That(left, Is.Zero, "an unloaded chunk was still answered from the mirror");
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

}

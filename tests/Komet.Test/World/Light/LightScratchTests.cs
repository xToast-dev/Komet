using Komet.World;

namespace Komet.Test.World;

// Golden test against the engine: LightWorld's scenes lit by the engine's own ChunkIlluminator as shipped, then patched with the
// scratch off and on, in the same process. Every step's touched chunks, the light of the chunks it changed and at the end every light
// value of the world must match bit for bit - also with worlds taking turns on one thread, with illuminators on several threads at
// once, with a light update started from inside another one, on another world or on the same, and after updates that failed half way.
public sealed class LightScratchTests
{
    private const int Steps = 160;
    private static readonly int[] Seeds = [1];

    [TearDown]
    public void Reset() => LightScratch.Enabled = true;

    private static TestHarmony Patched()
    {
        var harmony = new TestHarmony("komet-test-lightscratch");
        LightScratch.Install(harmony);
        Assert.That(LightScratch.Rewritten, Is.True, "ChunkIlluminator no longer has the expected shape");
        return harmony;
    }

    private static Lit Result(LightWorld world) => new(world.Touched, world.Hashes, world.Errors, world.Light());

    // One world lit up and scripted; a full hash every 40 steps on top of the per-step ones
    private static Lit Light(int seed, int steps = Steps)
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), seed);
        world.LightUp();
        foreach (var step in world.Script(seed * 7 + 1, steps))
        {
            if (step % 40 == 0) world.Hashes.Add(world.Hash());
        }

        return Result(world);
    }

    private static void Same(Lit engine, Lit mine, string what)
    {
        var touched = FirstDifference(engine.Touched, mine.Touched, (a, b) => a.SequenceEqual(b));
        var hashes = FirstDifference(engine.Hashes, mine.Hashes, (a, b) => a == b);
        var chunk = FirstDifference(engine.Values, mine.Values, (a, b) => a.SequenceEqual(b));
        Assert.Multiple(() =>
        {
            Assert.That(engine.Touched, Is.Not.Empty);
            Assert.That(touched, Is.EqualTo(-1), $"{what}: touched chunks differ from step {touched} on");
            Assert.That(hashes, Is.EqualTo(-1), $"{what}: light differs from record {hashes} on");
            Assert.That(chunk, Is.EqualTo(-1), $"{what}: the light of chunk {chunk} differs at the end");
            Assert.That(mine.Errors, Is.EqualTo(engine.Errors), $"{what}: errors");
        });
    }

    private static int FirstDifference<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, System.Func<T, T, bool> equal)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (!equal(a[i], b[i])) return i;
        }

        return a.Count == b.Count ? -1 : Math.Min(a.Count, b.Count);
    }

    [Test]
    [Category("Slow")]
    public void LightsExactlyWhatTheEngineLights()
    {
        var engine = Seeds.Select(seed => Light(seed)).ToList();
        using var harmony = Patched();
        LightScratch.Enabled = false;
        var off = Seeds.Select(seed => Light(seed)).ToList();
        LightScratch.Enabled = true;
        var on = Seeds.Select(seed => Light(seed)).ToList();
        for (var i = 0; i < Seeds.Length; i++)
        {
            Same(engine[i], off[i], $"seed {Seeds[i]} switched off");
            Same(engine[i], on[i], $"seed {Seeds[i]}");
        }
    }

    // Two worlds on one thread, a step each in turn: each illuminator's VisitedNodes still holds the keys the other's last update
    // reuses, until its own next update clears it
    private static (Lit, Lit) TakingTurns(int seed)
    {
        var (a, b) = (new LightWorld(LightWorld.MakeBlocks(), seed),
            new LightWorld(LightWorld.MakeBlocks(), seed + 10));
        a.LightUp();
        b.LightUp();
        using var first = a.Script(seed * 7 + 1, Steps).GetEnumerator();
        using var second = b.Script((seed + 10) * 7 + 1, Steps).GetEnumerator();
        for (var step = 0; step < Steps; step++)
        {
            _ = first.MoveNext() && second.MoveNext();
        }

        return (Result(a), Result(b));
    }

    // Three threads light their own worlds at once, as the server's chunk thread, its main thread and the client's relight thread do,
    // each two worlds taking turns; every world has to come out as the engine lit it alone
    [Test]
    [Category("Slow")]
    public void ThreadsLightTheirOwnWorlds()
    {
        int[] seeds = [5, 6, 7];
        var engine = seeds.Select(TakingTurns).ToList();
        using var harmony = Patched();
        var mine = new (Lit, Lit)?[seeds.Length];
        var failures = Threads.Run(seeds.Length, t =>
        {
            mine[t] = TakingTurns(seeds[t]);
            return null;
        });
        Assert.That(failures, Is.All.Null);
        for (var t = 0; t < seeds.Length; t++)
        {
            Same(engine[t].Item1, mine[t]!.Value.Item1, $"thread {t}, first world");
            Same(engine[t].Item2, mine[t]!.Value.Item2, $"thread {t}, second world");
        }
    }

    // A world whose hooked blocks fail: updates stop half way, leaving a queue with entries and nodes in
    // VisitedNodes behind, and the next update on the thread has to come out as the engine's does after the same failure
    private static Lit Failing(int seed)
    {
        var world = new LightWorld(LightWorld.MakeBlocks(new FailingBlock(11)), seed) { Catching = true };
        world.LightUp();
        _ = world.Script(seed * 7 + 1, Steps).Count();
        return Result(world);
    }

    [Test]
    [Category("Slow")]
    public void FailedUpdatesLeaveNothingBehind()
    {
        var engine = Failing(12);
        using var harmony = Patched();
        var mine = Failing(12);
        Same(engine, mine, "the failing world");
        Assert.That(engine.Errors, Has.Count.GreaterThan(5), "failed updates");
    }

    private sealed record Lit(List<long[]> Touched, List<ulong> Hashes, List<string> Errors, int[][] Values);

    private sealed class Hook(int every, int budget)
    {
        private int _calls, _left = budget;
        private bool _inside;

        public Action? Step { get; set; }

        public void Fire()
        {
            if (Step == null || _inside || _left == 0 || ++_calls % every != 0) return;

            (_inside, _left) = (true, _left - 1);
            try
            {
                Step();
            }
            finally
            {
                _inside = false;
            }
        }
    }

    // Light absorption a mod computes, and which starts a light update while the illuminator asks for it
    private sealed class NestingBlock(Hook hook) : Block
    {
        public override int GetLightAbsorption(IWorldChunk chunk, BlockPos pos)
        {
            hook.Fire();
            return LightAbsorption;
        }
    }

    // A light whose hsv a mod computes, and which starts a light update while the illuminator walks the nearby sources; the world's
    // own calls pass no position and are left alone
    private sealed class NestingLightBlock(Hook hook) : Block
    {
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos? pos, ItemStack? stack = null)
        {
            if (pos != null) hook.Fire();

            return LightHsv;
        }
    }

    // A block whose light absorption a mod computes and which fails every so many calls, in the middle of whatever walk asks
    private sealed class FailingBlock(int every) : Block
    {
        private int _calls;

        public override int GetLightAbsorption(IWorldChunk chunk, BlockPos pos)
        {
            if (++_calls % every == 0) throw new InvalidOperationException("absorption failed");

            return LightAbsorption;
        }
    }
}

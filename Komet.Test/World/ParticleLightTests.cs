using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace Komet.Test.World;

// Particle light on the render thread: it never waits for a chunk lock another thread holds and never unpacks a packed chunk,
// and a lock it takes is released once, also when the engine's read throws
public sealed class ParticleLightTests
{
    private const int Light = 0x123456;
    private long _reads, _busy, _packed; // the totals when the test began

    [SetUp]
    public void Clear()
    {
        ParticleLight.Forget();
        Counting.Hud = true;
        (_reads, _busy, _packed) = (ParticleLight.Reads, ParticleLight.Busy, ParticleLight.Packed);
    }

    [TearDown]
    public void Stop()
    {
        Counting.Hud = false;
    }

    [Test]
    public void Installs()
    {
        var harmony = new Harmony("komet-test-particlelight");
        try
        {
            ParticleLight.Install(harmony);
            Assert.That(ParticleLight.Installed, Is.True);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void AFreeLockLetsTheEngineReadWhileHoldingIt()
    {
        var chunk = new TestChunk();
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.SameAs(chunk));
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.True, "held across the original");
        });
        Assert.That(ParticleLight.Finalizer(null, state, Light), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.False, "released by the finalizer");
            Assert.That(OtherThreadCanEnter(chunk.Gate), Is.True);
            Assert.That(ParticleLight.Reads - _reads, Is.EqualTo(1));
        });
    }

    [Test]
    public void ABusyLockServesTheLastLightWithoutWaiting()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        using var held = HoldElsewhere(chunk);
        var result = 0;
        var clock = Stopwatch.StartNew();
        var engine = ParticleLight.Enter(chunk, ref result, out var state);
        Assert.Multiple(() =>
        {
            Assert.That(engine, Is.False);
            Assert.That(result, Is.EqualTo(Light));
            Assert.That(state, Is.Null);
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(100), "never waits");
            Assert.That(ParticleLight.Busy - _busy, Is.EqualTo(1));
        });
    }

    // TryEnter spins before it gives up, so a chunk found busy is left alone for the backoff, even once its lock is free again
    [Test]
    public void ABusyChunkIsNotTriedAgainWithinTheBackoff()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        var result = 0;
        using (HoldElsewhere(chunk))
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out _), Is.False);
        }

        result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.False, "still backing off");
            Assert.That((result, state), Is.EqualTo((Light, (WorldChunk?)null)));
            Assert.That(ParticleLight.Busy - _busy, Is.EqualTo(2));
        });
        _ = SpinWait.SpinUntil(() => false, ParticleLight.BackoffMs + 30); // let the backoff run out
        Assert.That(ParticleLight.Enter(chunk, ref result, out var taken), Is.True, "tried again after the backoff");
        _ = ParticleLight.Finalizer(null, taken, Light);
    }

    [Test]
    public void EachChunkServesItsOwnLight()
    {
        TestChunk shaded = new(), sunlit = new();
        Remember(shaded, 1);
        Remember(sunlit, Light);
        var result = 0;
        using var held = HoldElsewhere(shaded);
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(shaded, ref result, out _), Is.False);
            Assert.That(result, Is.EqualTo(1), "the chunk's own light, not the last one read anywhere");
        });
    }

    [Test]
    public void WithoutALastLightTheEngineWaitsAsBefore()
    {
        var chunk = new TestChunk();
        using var held = HoldElsewhere(chunk);
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
            Assert.That(state, Is.Null, "nothing taken, nothing to release");
        });
    }

    [Test]
    public void APackedChunkStaysPacked()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        chunk.Unpack(); // marks potential changes, so Pack compresses rather than reuse data it never had
        chunk.TryPackAndCommit(0);
        Assert.That(chunk.IsPacked(), Is.True, "precondition");
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.False);
            Assert.That(result, Is.EqualTo(Light));
            Assert.That(state, Is.Null);
            Assert.That(chunk.IsPacked(), Is.True, "not unpacked on the render thread");
            Assert.That(ParticleLight.Packed - _packed, Is.EqualTo(1));
        });
    }

    [Test]
    public void TheLockIsReleasedWhenTheEngineThrows()
    {
        var chunk = new TestChunk();
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        var thrown = new InvalidOperationException("engine");
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Finalizer(thrown, state, 0), Is.SameAs(thrown));
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.False);
        });
        using var held = HoldElsewhere(chunk); // the failed read left no last light behind
        Assert.That(ParticleLight.Enter(chunk, ref result, out _), Is.True);
    }

    // Leaving the world forgets every chunk and the last light: the next read of a busy chunk waits as the first one did
    [Test]
    public void ForgetDropsEveryChunk()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        ParticleLight.Forget();
        using var held = HoldElsewhere(chunk);
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
            Assert.That((result, state), Is.EqualTo((0, (WorldChunk?)null)));
        });
    }

    private static void Remember(TestChunk chunk, int light)
    {
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        _ = ParticleLight.Finalizer(null, state, light);
    }

    private static bool OtherThreadCanEnter(object gate)
    {
        return Task.Run(() =>
        {
            if (!Monitor.TryEnter(gate)) return false;
            Monitor.Exit(gate);
            return true;
        }).Result;
    }

    // The chunk's lock held by another thread until disposed, the way the visibility calculation or the compressor holds it
    private static Holder HoldElsewhere(TestChunk chunk)
    {
        return new Holder(chunk.Gate);
    }

    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private readonly Thread _thread;

        public Holder(object gate)
        {
            _thread = new Thread(() =>
            {
                lock (gate)
                {
                    _entered.Set();
                    _release.Wait();
                }
            });
            _thread.IsBackground = true;
            _thread.Start();
            _entered.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class TestChunk : WorldChunk
    {
        private static readonly ChunkDataPool Pool = new(GlobalConstants.ChunkSize, null!);

        public TestChunk()
        {
            datapool = Pool;
            chunkdata = ChunkData.CreateNew(GlobalConstants.ChunkSize, Pool);
            MaybeBlocks = chunkdata;
        }

        public object Gate => PackUnpackLock(this);
        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
        private static extern ref object PackUnpackLock(WorldChunk chunk);
    }
}

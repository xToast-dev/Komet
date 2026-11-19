using Komet.Core;
using Komet.World;

namespace Komet.Test.World;

// Particle light on the render thread: it never waits for a chunk lock another thread holds and never unpacks a packed chunk,
// and a lock it takes is released once, also when the engine's read throws
public sealed class ParticleLightTests
{
    private const int Light = 0x123456;
    private long _busy; // the total when the test began

    [SetUp]
    public void Clear()
    {
        ParticleLight.Forget();
        Counting.Hud = true;
        _busy = ParticleLight.Busy;
    }

    [TearDown]
    public void Stop() => Counting.Hud = false;

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
    public void TheLockIsReleasedWhenTheEngineThrows()
    {
        var chunk = new TestChunk();
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        var thrown = new InvalidOperationException("engine");
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Finalizer(thrown, state, 0, 0, 0, 0), Is.SameAs(thrown));
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.False);
        });
        using var held = HoldElsewhere(chunk); // the failed read left no last light behind
        Assert.That(ParticleLight.Enter(chunk, ref result, out _), Is.True);
    }

    private static void Remember(TestChunk chunk, int light)
    {
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        _ = ParticleLight.Finalizer(null, state, light, 0, 0, 0);
    }

    // The chunk's lock held by another thread until disposed, the way the visibility calculation or the compressor holds it
    private static Holder HoldElsewhere(TestChunk chunk) => new(chunk.Gate);

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
            }) { IsBackground = true };
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

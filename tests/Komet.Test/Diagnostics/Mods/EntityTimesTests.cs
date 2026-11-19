using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// The timed calls are the engine's calls: each found once in the shipped loops and made on the same object with the same argument,
// HUD or not. A GC pause inside an entity's tick is not booked to it.
[NonParallelizable]
public sealed class EntityTimesTests
{
    [SetUp]
    public void Reset()
    {
        EntityTimes.Clear();
        Counting.Hud = true;
    }

    [TearDown]
    public void Restore() => Counting.Hud = false;

    [Test]
    public void BothLoopsAreRewritten()
    {
        using var harmony = new TestHarmony("komet-test-entitytimes");
        EntityTimes.Install(harmony);
        Assert.That(EntityTimes.Installed, Is.True,
            "ClientSystemEntities or SystemRenderEntities is not the loop measured");
    }

    [Test]
    public void AnyOtherBodyIsLeftAsItWas()
    {
        List<CodeInstruction> body = [new(OpCodes.Ldarg_0), new(OpCodes.Pop), new(OpCodes.Ret)];
        var (tick, frame) = (EntityTimes.RewriteTick(body), EntityTimes.RewriteFrame(body));
        Assert.Multiple(() =>
        {
            Assert.That(tick.Select(c => c.opcode), Is.EqualTo(body.Select(c => c.opcode)));
            Assert.That(frame.Select(c => c.opcode), Is.EqualTo(body.Select(c => c.opcode)));
            Assert.That(EntityTimes.Installed, Is.False);
        });
    }

    private sealed class Ticking : EntityAgent
    {
        private bool _collect;
        public int Ticks { get; private set; }
        public float Dt { get; private set; }
        public long Paused { get; private set; } // Stopwatch ticks the forced collection took

        public static Ticking Made(bool collect = false)
        {
            var entity = (Ticking)RuntimeHelpers.GetUninitializedObject(typeof(Ticking));
            (entity.Code, entity._collect) = (new AssetLocation("game", "deer-1"), collect);
            return entity;
        }

        public override void OnGameTick(float dt)
        {
            (Ticks, Dt) = (Ticks + 1, dt);
            if (!_collect) return;

            var pause = GC.GetTotalPauseDuration();
            GC.Collect(2, GCCollectionMode.Forced, true);
            Paused = (long)((GC.GetTotalPauseDuration() - pause).TotalSeconds * Stopwatch.Frequency);
        }
    }

    private sealed class Preparing() : EntityRenderer(null, null)
    {
        public int Calls { get; private set; }
        public float Dt { get; private set; }

        public override void BeforeRender(float dt) => (Calls, Dt) = (Calls + 1, dt);

        public override void Dispose()
        {
            // nothing held
        }
    }
}

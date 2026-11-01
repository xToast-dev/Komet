using Komet.Rendering;

namespace Komet.Test.Rendering;

// The climate and wind triggers and the particles' collision walk are rewritten; the triggers still call every handler in
// order, and a handler added later is called too
[NonParallelizable]
public sealed class HandlerScratchTests
{
    private static readonly int[] Called = [1, 1, 1, 2];

    [Test]
    public void TriggersCallEveryHandlerWithAKeptList()
    {
        using var harmony = new TestHarmony("komet-test-handlerscratch");
        HandlerScratch.Install(harmony);
        Assert.That(HandlerScratch.Rewritten, Is.EqualTo(3));

        var events = (EventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        var calls = new List<int>();
        events.OnGetWindSpeed += (Vec3d _, ref Vec3d _) => calls.Add(1);
        var wind = new Vec3d();
        events.TriggerOnGetWindSpeed(new Vec3d(), ref wind);
        events.TriggerOnGetWindSpeed(new Vec3d(), ref wind);
        events.OnGetWindSpeed += (Vec3d _, ref Vec3d _) => calls.Add(2);
        events.TriggerOnGetWindSpeed(new Vec3d(), ref wind);
        Assert.That(calls, Is.EqualTo(Called));

        Action first = () => { }, both = first + (() => { });
        var kept = HandlerScratch.Invocations(first);
        Assert.That(HandlerScratch.Invocations(first), Is.SameAs(kept));
        Assert.That(HandlerScratch.Invocations(both), Has.Length.EqualTo(2));
    }
}

using Komet.Core;
using Komet.World;

namespace Komet.Test.World;

// The engine's own ClientEventAPI and BlockEntity bodies on a ClientMain without a game: listeners registered the way
// BlockEntityDisplay and BEBehaviorDisplay register theirs, block entities unloaded and removed through the base methods.
[NonParallelizable]
public sealed class EventBusSweepTests
{
    private ICoreClientAPI _capi = null!;
    private ClientMain _game = null!;
    private TestHarmony? _harmony;
    private int _mainThread;
    private FrameProfilerUtil? _profiler;

    private List<EventBusListener> Listeners => _game.eventManager.EventBusListeners;

    [SetUp]
    public void Install()
    {
        (_mainThread, _profiler) = (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler);
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static _ => { });
        _harmony = new TestHarmony("komet-test-eventbussweep");
        EventBusSweep.Install(_harmony, new QuietLogger());
        (EventBusSweep.Enabled, Counting.Hud) = (true, true);
        _game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        (_game.GameLaunchTasks, _game.MainThreadTasks, _game.MainThreadTasksLock) =
            (new Queue<ClientTask>(), new Queue<ClientTask>(), new object());
        AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue").SetValue(_game, new Queue<ClientTask>());
        _game.eventManager = (ClientEventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        _game.eventManager.EventBusListeners = [];
        var events = (ClientEventAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventAPI));
        AccessTools.Field(typeof(ClientEventAPI), "game").SetValue(events, _game);
        _capi = Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_Event"] = _ => events, ["get_World"] = _ => Answers.Of<IClientWorldAccessor>([]),
            ["get_Side"] = _ => EnumAppSide.Client
        });
    }

    [TearDown]
    public void Restore()
    {
        _harmony?.Dispose();
        (EventBusSweep.Enabled, Counting.Hud) = (true, false);
        EventBusSweep.Clear();
        (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler) = (_mainThread, _profiler!);
    }

    [Test]
    public void InstallFindsTheVerifiedBodies() =>
        Assert.Multiple(() =>
        {
            Assert.That(EventBusSweep.Installed, Is.True);
            Assert.That(EngineShape.Of(EventBusSweep.Seams()), Is.EqualTo(EventBusSweep.Fingerprint),
                $"not the bodies verified: 0x{EngineShape.Of(EventBusSweep.Seams()):X16}UL");
        });

    // Unloaded, removed, a behaviour of an unloaded one: their listeners go at the end of the frame's tasks. A live block entity's, a
    // live one's behaviour's and anything else's stay, in their order.
    [Test]
    public void TheDeadBlockEntitiesListenersGoAfterTheFrame()
    {
        var (unloaded, removed, live, owner) = (Display(), Display(), Display(), Display());
        var (_, alive) = (Behavior(owner), Behavior(live));
        var other = new Block();
        _capi.Event.RegisterEventBusListener((_, ref _, _) => _ = other.Id);
        var closure = Listeners[^1].handler.Target;
        unloaded.OnBlockUnloaded();
        removed.OnBlockRemoved();
        owner.OnBlockUnloaded();
        Assert.That(Listeners, Has.Count.EqualTo(7), "nothing goes before the frame's tasks are done");
        _game.ExecuteMainThreadTasks(0);
        Assert.Multiple(() =>
        {
            Assert.That(Listeners.Select(l => l.handler.Target), Is.EqualTo([live, alive, closure]));
            Assert.That((EventBusSweep.Swept, EventBusSweep.Listeners), Is.EqualTo((4L, 3)));
        });
        _game.ExecuteMainThreadTasks(0);
        Assert.That(EventBusSweep.Swept, Is.EqualTo(4L), "a frame without deaths sweeps nothing");
    }

    // What BlockEntityDisplay.Initialize does on the client after base.Initialize
    private Shelf Display()
    {
        var entity = new Shelf { Api = _capi };
        _capi.Event.RegisterEventBusListener(entity.OnEvent);
        return entity;
    }

    // What BEBehaviorDisplay.Initialize does
    private Behaviour Behavior(BlockEntity owner)
    {
        var behavior = new Behaviour(owner);
        owner.Behaviors.Add(behavior);
        _capi.Event.RegisterEventBusListener(behavior.OnEvent, 0.5, "onsettransform");
        return behavior;
    }

    private sealed class Shelf : BlockEntity
    {
        private int Events { get; set; }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            (api as ICoreClientAPI)?.Event.RegisterEventBusListener(OnEvent);
        }

        public void OnEvent(string eventName, ref EnumHandling handling, IAttribute? data) =>
            (Events, handling) = (Events + eventName.Length + (data is null ? 0 : 1), EnumHandling.PassThrough);
    }

    private sealed class Behaviour(BlockEntity owner) : BlockEntityBehavior(owner)
    {
        private int Events { get; set; }

        public void OnEvent(string eventName, ref EnumHandling handling, IAttribute? data) =>
            (Events, handling) = (Events + eventName.Length + (data is null ? 0 : 1), EnumHandling.PassThrough);
    }
}

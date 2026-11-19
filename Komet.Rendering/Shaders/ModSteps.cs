using System.Diagnostics.CodeAnalysis;
using Komet.Api;

namespace Komet.Rendering;

// KAPI render steps (KapiRendering.AddStep): each runs as a game renderer at its stage, so it draws where the game's own
// renderers of that stage draw, on OpenGL and under Vulkan alike
internal sealed class ModSteps : IRenderStepHost
{
    public static readonly ModSteps Instance = new();

    private readonly Dictionary<StepEntry, Runner> _running = [];
    private ICoreClientAPI? _api;

    public void Add(StepEntry entry)
    {
        if (_api?.Event is not { } events || _running.ContainsKey(entry)) return;

        var runner = new Runner(entry);
        _running[entry] = runner;
        events.RegisterRenderer(runner, (EnumRenderStage)entry.Stage, "kapi-" + entry.ModId + ":" + entry.Step.Id);
    }

    [SuppressMessage("Reliability", "CA2000",
        Justification = "the runner is disposed by the main-thread task it is handed to")]
    public void Remove(StepEntry entry)
    {
        if (!_running.Remove(entry, out var runner)) return;

        if (_api?.Event is not { } events)
        {
            runner.Dispose();
            return;
        }

        // a step that disposes itself does so inside the game's loop over this stage's renderers: taking it out there
        // skips the next one, so it goes after the frame (it runs no more: its entry is gone)
        var stage = (EnumRenderStage)entry.Stage;
        events.EnqueueMainThreadTask(() =>
        {
            events.UnregisterRenderer(runner, stage);
            runner.Dispose();
        }, "kapi-step-remove");
    }

    public static void Install(ICoreClientAPI api)
    {
        if (!NotNull(api.Event)) return;

        Instance._api = api;
        RenderSteps.Attach(Instance);
    }

    // The world closed: the game drops its renderers itself
    public static void Clear()
    {
        Instance._running.Clear();
        Instance._api = null;
    }

    private sealed class Runner(StepEntry entry) : IRenderer
    {
        public double RenderOrder => entry.Step.Order;
        public int RenderRange => entry.Step.Range;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => entry.Run(deltaTime);

        public void Dispose()
        {
        }
    }
}

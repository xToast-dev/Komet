using Komet.Core;
using Komet.Api;
using Komet.Rendering;

namespace Komet.Test.Api;

// KAPI render steps (KapiRendering.AddStep), the connection's counts and refusals, and the Connected mods page
[NonParallelizable]
public sealed class KapiRenderingTests
{
    private FakeHost _host = null!;
    private IRenderStepHost? _before;
    private CapturingLogger _log = null!;
    private int _mainThread;

    [SetUp]
    public void Open()
    {
        _log = new CapturingLogger();
        ModEvents.Logger = _log;
        (_mainThread, RuntimeEnv.MainThreadId) = (RuntimeEnv.MainThreadId, Environment.CurrentManagedThreadId);
        _host = new FakeHost();
        _before = Services.Swap<IRenderStepHost>(_host);
    }

    [TearDown]
    public void Close()
    {
        WorldEnd.Run();
        FeatureLifecycle.Close();
        ModShaders.Clear();
        foreach (var id in (string[])["mymod", "other"]) Kapi.Silence(id, false);
        DebugTimings.Capturing = false;
        _ = Services.Swap(_before);
        ModEvents.Logger = null;
        RuntimeEnv.MainThreadId = _mainThread;
    }

    private static KapiMod Connect(string id = "mymod") => Kapi.Connect(id, "1.0")!;

    private static KapiRenderStep Step(string id, Action<float> render, Func<bool>? enabled = null) =>
        new(id, KapiRenderStage.AfterTransparent, render) { Enabled = enabled };

    private static StepEntry Only() => RenderSteps.All.Single();

    [TestCase(KapiRenderStage.Transparent, EnumRenderStage.OIT)]
    public void AStepRunsAtTheGameStageOfItsName(KapiRenderStage stage, EnumRenderStage game) =>
        Assert.That(StepEntry.StageOf(stage), Is.EqualTo(game));

    [Test]
    public void AThrowingStepFailsLogsAndStaysOff()
    {
        using var komet = Connect();
        var calls = 0;
        _ = komet.Rendering.AddStep(Step("outline", _ =>
        {
            calls++;
            throw new InvalidOperationException("broken");
        }));
        Only().Run(0.1f);
        Only().Run(0.1f);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(Only().Failure, Is.EqualTo("InvalidOperationException: broken"));
            Assert.That(_log.Lines, Has.Count.EqualTo(1).And.All.StartWith("Error Komet: render step mymod:outline threw"));
        }
    }

    [Test]
    public void DisposingTheRegistrationOrTheConnectionRemovesTheStep()
    {
        var komet = Connect();
        var outline = komet.Rendering.AddStep(Step("outline", _ => { }));
        _ = komet.Rendering.AddStep(Step("glow", _ => { }));
        var entries = RenderSteps.All.ToArray();
        Assert.That((_host.Added.Count, komet.Count(KapiKind.Step)), Is.EqualTo((2, 2)));

        outline.Dispose();
        Assert.That((RenderSteps.All.Single().Step.Id, komet.Count(KapiKind.Step)), Is.EqualTo(("glow", 1)));

        komet.Dispose();
        Assert.That((RenderSteps.All.Count, komet.Count(KapiKind.Step)), Is.EqualTo((0, 0)));
        Assert.That(_host.Removed, Is.EqualTo(entries));
    }

    private sealed class FakeHost : IRenderStepHost
    {
        public List<StepEntry> Added { get; } = [];
        public List<StepEntry> Removed { get; } = [];

        public void Add(StepEntry entry) => Added.Add(entry);
        public void Remove(StepEntry entry) => Removed.Add(entry);
    }
}

using Komet.Core;
using Komet.Api;
using Komet.Options;
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

    [Test]
    public void AStepNeedsAnId()
    {
        using var komet = Connect();
        var step = komet.Rendering.AddStep(Step("", _ => { }));
        Assert.That((step.Active, step.Error, RenderSteps.All.Count), Is.EqualTo((false, "a step with an id is needed", 0)));
    }

    [Test]
    public void TheSameIdReplacesTheStepBefore()
    {
        using var komet = Connect();
        var first = komet.Rendering.AddStep(Step("outline", _ => { }));
        var old = Only();
        var second = komet.Rendering.AddStep(Step("outline", _ => { }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.Active, second.Active), Is.EqualTo((false, true)));
            Assert.That(RenderSteps.All, Has.Count.EqualTo(1));
            Assert.That(Only(), Is.Not.SameAs(old));
            Assert.That(_host.Removed.Single(), Is.SameAs(old));
            Assert.That(komet.Count(KapiKind.Step), Is.EqualTo(1));
        }
    }

    [Test]
    public void TheTableHoldsMaxStepsOfAllModsTogether()
    {
        using var one = Connect();
        using var two = Connect("other");
        for (var i = 0; i < KapiRendering.MaxSteps; i++)
        {
            Assert.That((i % 2 == 0 ? one : two).Rendering.AddStep(Step("s" + i, _ => { })).Active, Is.True);
        }

        var full = one.Rendering.AddStep(Step("more", _ => { }));
        Assert.That((full.Active, full.Error), Is.EqualTo((false, $"more than {KapiRendering.MaxSteps} render steps")));
        Assert.That(RenderSteps.All, Has.Count.EqualTo(KapiRendering.MaxSteps));
    }

    [Test]
    public void RunCallsRenderTimesItAndAsksEnabled()
    {
        using var komet = Connect();
        var (calls, on, seen) = (0, true, 0f);
        // ReSharper disable once AccessToModifiedClosure the test switches the step off between runs
        _ = komet.Rendering.AddStep(Step("outline", s => (calls, seen) = (calls + 1, s), () => on));
        var entry = Only();
        DebugTimings.Capturing = true;
        entry.Run(0.25f);
        entry.Run(0.25f);
        on = false;
        entry.Run(0.25f);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((calls, seen, entry.Calls), Is.EqualTo((2, 0.25f, 2L)));
            Assert.That(entry.AverageMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(string.Join(" ", DebugTimings.TakeTimings().Select(t => t.Name + "×" + t.Calls)), Is.EqualTo("mymod:outline×2"));
            Assert.That(entry.Failure, Is.Null);
        }
    }

    [Test]
    public void ASilencedModsStepsDoNotRun()
    {
        using var komet = Connect();
        var calls = 0;
        _ = komet.Rendering.AddStep(Step("outline", _ => calls++));
        Kapi.Silence("mymod", true);
        Only().Run(0.1f);
        Assert.That((Kapi.IsSilenced("mymod"), calls), Is.EqualTo((true, 0)));
        Kapi.Silence("mymod", false);
        Only().Run(0.1f);
        Assert.That(calls, Is.EqualTo(1));
    }

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

    [Test]
    public void StepsAddedBeforeTheHostAreAttachedLater()
    {
        _ = Services.Swap<IRenderStepHost>(null);
        using var komet = Connect();
        _ = komet.Rendering.AddStep(Step("outline", _ => { }));
        Assert.That(_host.Added, Is.Empty);
        _ = Services.Swap<IRenderStepHost>(_host);
        RenderSteps.Attach(_host);
        Assert.That(_host.Added.Single(), Is.SameAs(Only()));
    }

    [Test]
    public void TheConnectionCountsItsLiveRegistrationsByKind()
    {
        using var komet = Connect();
        _ = komet.Rendering.AddStep(Step("outline", _ => { }));
        _ = komet.Options.Page("My mod");
        var uniform = komet.Shaders.Uniform("mymodWarmth", () => 1f);
        _ = komet.Shaders.Define("MYMOD_WARM", () => "1");
        Assert.That((komet.Count(KapiKind.Step), komet.Count(KapiKind.Page), komet.Count(KapiKind.Shader)),
            Is.EqualTo((1, 1, 2)));
        uniform.Dispose();
        Assert.That((komet.Count(KapiKind.Step), komet.Count(KapiKind.Page), komet.Count(KapiKind.Shader)),
            Is.EqualTo((1, 1, 1)));
    }

    [Test]
    public void ARefusedVersionIsRecordedUntilTheWorldEnds()
    {
        Assert.That(Kapi.Connect("oldmod", "2.0"), Is.Null);
        Assert.That(Kapi.Refusals.Single(), Is.EqualTo(("oldmod", "2.0",
            "it was written for KAPI 2.0, this Komet offers KAPI 1.0")));
        WorldEnd.Run();
        Assert.That(Kapi.Refusals, Is.Empty);
    }

    [Test]
    public void TheConnectedModsPageHasASwitchPerModAndARowPerRefusal()
    {
        GameLang.EnsureLoaded();
        using var one = Connect();
        using var two = Connect("other");
        _ = one.Rendering.AddStep(Step("outline", _ => { }));
        _ = Kapi.Connect("oldmod", "2.0");
        var page = ConnectedModsPage.Build(null, "section", _ => { });
        var rows = Enumerable.Range(0, page.Count).Select(i => page[i]).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Id, Is.EqualTo(ConnectedModsPage.Id));
            Assert.That(string.Join(" ", rows.Where(r => r.Kind == OptionKind.Switch).Select(r => r.Label)),
                Is.EqualTo("mymod other"));
            Assert.That(rows.Count(r => r is { Kind: OptionKind.Button, Label: "oldmod" }), Is.EqualTo(1));
        }
    }

    private sealed class FakeHost : IRenderStepHost
    {
        public List<StepEntry> Added { get; } = [];
        public List<StepEntry> Removed { get; } = [];

        public void Add(StepEntry entry) => Added.Add(entry);
        public void Remove(StepEntry entry) => Removed.Add(entry);
    }
}

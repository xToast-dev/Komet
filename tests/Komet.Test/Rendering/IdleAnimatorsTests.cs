namespace Komet.Test.Rendering;

// A render call the loops leave out is one the engine's OnRenderFrame would have returned from without doing anything: golden against
// the engine's own AnimationUtil.OnRenderFrame in every state that decides it, and AnimatableRenderer's ShouldRender
public sealed class IdleAnimatorsTests
{
    [TearDown]
    public void Restore()
    {
        IdleAnimators.Enabled = true;
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-idleanimators");
        IdleAnimators.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(IdleAnimators.Matched, Is.True, "the OnRenderFrame bodies are not the ones verified");
            Assert.That(IdleAnimators.Rewritten, Is.True,
                "TriggerRenderStage no longer calls OnRenderFrame in two loops");
            Assert.That(IdleAnimators.Blocked, Is.False);
        });
    }

    // Every combination of what the util's OnRenderFrame reads: an idle one does nothing there, any other one does something
    [Test]
    public void AnIdleUtilIsOneTheEngineWouldHaveReturnedFrom([Values] bool animator, [Values] bool renderer,
        [Values] bool active, [Values] bool running, [Values] bool shouldRender, [Values] bool stopped)
    {
        var util = new RecordingUtil();
        var recorder = (RecordingAnimator)RuntimeHelpers.GetUninitializedObject(typeof(RecordingAnimator));
        AccessTools.Field(typeof(AnimatorBase), "activeAnimCount").SetValue(recorder, running ? 1 : 0);
        (util.animator, util.renderer) = (animator ? recorder : null, renderer ? Renderer(shouldRender) : null);
        if (active) util.activeAnimationsByAnimCode["walk"] = new AnimationMetaData { Code = "walk" };
        AccessTools.Field(typeof(AnimationUtil), "stopRenderTriggered").SetValue(util, stopped);
        var idle = IdleAnimators.Idle(util);
        util.OnRenderFrame(0.016f, EnumRenderStage.Opaque); // the engine's body, unpatched
        Assert.That(recorder.Frames + util.Changes > 0, Is.EqualTo(!idle));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void AnAnimatableRendererIsIdleWhileItDoesNotRender(bool shouldRender, bool idle)
    {
        Assert.That(IdleAnimators.Idle(Renderer(shouldRender)), Is.EqualTo(idle));
    }

    // A renderer of any other kind, or one implementing IRenderer again, is always called
    [Test]
    public void OtherRenderersAreNeverIdle()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IdleAnimators.Idle(new Reimplemented()), Is.False);
            Assert.That(IdleAnimators.Idle(Answers.Of<IRenderer>([])), Is.False);
            Assert.That(IdleAnimators.Idle(null), Is.False);
        });
    }

    private static AnimatableRenderer Renderer(bool shouldRender)
    {
        var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        renderer.ShouldRender = shouldRender;
        return renderer;
    }

    private sealed class RecordingUtil() : AnimationUtil(null, new Vec3d())
    {
        public int Changes { get; private set; }

        protected override void OnAnimationsStateChange(bool animsNowActive)
        {
            Changes++;
        }
    }

    private sealed class RecordingAnimator() : AnimatorBase(null, [])
    {
        public int Frames { get; private set; }
        public override int MaxJointId => 0;

        public override void OnFrame(Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode, float dt)
        {
            Frames++;
        }

        protected override void calculateMatrices(float dt)
        {
            // never stepped here
        }
    }

    // Implements IRenderer.OnRenderFrame itself: its calls are not the engine body's
    private sealed class Reimplemented() : AnimationUtil(null, new Vec3d()), IRenderer
    {
        void IRenderer.OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            // another body
        }
    }
}

using Komet.Rendering;

namespace Komet.Test.Rendering;

// A render call the loops leave out is one the engine's OnRenderFrame would have returned from without doing anything: golden against
// the engine's own AnimationUtil.OnRenderFrame in every state that decides it, and AnimatableRenderer's ShouldRender
public sealed class IdleAnimatorsTests
{
    [TearDown]
    public void Restore() => IdleAnimators.Enabled = true;

    // Every combination of what the util's OnRenderFrame reads: an idle one does nothing there, any other one does something
    [Test]
    public void AnIdleUtilIsOneTheEngineWouldHaveReturnedFrom() =>
        Assert.Multiple(() =>
        {
            for (var bits = 0; bits < 64; bits++)
            {
                Compared((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0,
                    (bits & 32) != 0);
            }
        });

    private static void Compared(bool animator, bool renderer, bool active, bool running, bool shouldRender,
        bool stopped)
    {
        var util = new RecordingUtil();
        var recorder = (RecordingAnimator)RuntimeHelpers.GetUninitializedObject(typeof(RecordingAnimator));
        AccessTools.Field(typeof(AnimatorBase), "activeAnimCount").SetValue(recorder, running ? 1 : 0);
        (util.animator, util.renderer) = (animator ? recorder : null, renderer ? Renderer(shouldRender) : null);
        if (active) util.activeAnimationsByAnimCode["walk"] = new AnimationMetaData { Code = "walk" };

        AccessTools.Field(typeof(AnimationUtil), "stopRenderTriggered").SetValue(util, stopped);
        var idle = IdleAnimators.Idle(util);
        util.OnRenderFrame(0.016f, EnumRenderStage.Opaque); // the engine's body, unpatched
        Assert.That(recorder.Frames + util.Changes > 0, Is.EqualTo(!idle),
            $"animator {animator}, renderer {renderer}, active {active}, running {running}, " +
            $"shouldRender {shouldRender}, stopped {stopped}");
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void AnAnimatableRendererIsIdleWhileItDoesNotRender(bool shouldRender, bool idle) =>
        Assert.That(IdleAnimators.Idle(Renderer(shouldRender)), Is.EqualTo(idle));

    private static AnimatableRenderer Renderer(bool shouldRender)
    {
        var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        renderer.ShouldRender = shouldRender;
        return renderer;
    }

    private sealed class RecordingUtil() : AnimationUtil(null, new Vec3d())
    {
        public int Changes { get; private set; }

        protected override void OnAnimationsStateChange(bool animsNowActive) => Changes++;
    }

    private sealed class RecordingAnimator() : AnimatorBase(null, [])
    {
        public int Frames { get; private set; }
        public override int MaxJointId => 0;

        public override void OnFrame(Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode, float dt) =>
            Frames++;

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

using Komet.Core;

namespace Komet.Test.Synth;

// Shapes and entities: AnimationFrames, ShapeInitMemo
[Category(Synth.Category)]
public sealed class ShapesSynth
{
    // An armored player's re-tesselation (gear or skin changed, or a seraph coming into view): its composed shape lacks the
    // compiled frames, so every animation of it compiles again; an op is the whole set
    [Test]
    public void AnimationFrames()
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Player("armored");
        var animations = shape.Animations.Where(a => a.KeyFrames is { Length: > 0 }).ToArray();
        var frames = 0;
        void Compile()
        {
            foreach (var animation in animations)
            {
                animation.PrevNextKeyFrameByFrame = null;
                SynthCalls.Compile(animation, shape);
                frames += animation.PrevNextKeyFrameByFrame?.Length ?? -1;
            }
        }

        var result = Synth.Run("AnimationFrames", $"armored-player-{animations.Length}", ops =>
        {
            frames = 0;
            for (var i = 0; i < ops; i++) Compile();
        }, 2, () => frames);
        using (new FeatureRig("AnimationFrames"))
        {
            Counting.Hud = true;
            var hits = Komet.Shapes.AnimationFrames.Hits;
            Compile();
            Compile();
            Counting.Hud = false;
            Assert.That(Komet.Shapes.AnimationFrames.Hits - hits, Is.EqualTo(animations.Length), "the second set is the cache's");
        }

        Assert.That(result.Feature, Is.EqualTo("AnimationFrames"));
    }

    // A gearless animal's tesselation: AnimationCache.InitManager inits the type's shared shape again (a wolf)
    [Test]
    public void ShapeInitMemo()
    {
        GameInstall.RequireAssets();
        const string path = "entity/animal/mammal/wolf/eurasian-adult";
        var mainThread = RuntimeEnv.MainThreadId;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        try
        {
            var shape = AnimationShapes.Load("survival", path);
            shape.ResolveReferences(AnimationShapes.Log, path);
            Shape.CacheInvTransforms(shape.Elements);
            string[] joints = ["head"];
            var result = Synth.Run("ShapeInitMemo", "wolf-reinit", ops =>
            {
                for (var i = 0; i < ops; i++) SynthCalls.Init(shape, AnimationShapes.Log, path, joints);
            }, 50, () => shape.JointsById.Count);
            using (new FeatureRig("ShapeInitMemo")) Assert.That(Komet.Shapes.ShapeInitMemo.Matched, Is.True);
            Assert.That(result.Feature, Is.EqualTo("ShapeInitMemo"));
        }
        finally
        {
            RuntimeEnv.MainThreadId = mainThread;
        }
    }
}

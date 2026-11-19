using Komet.Shapes;

namespace Komet.Test.Shapes;

public sealed class ElementWalkTests
{
    private static readonly string[] Visited = ["a@0<-1", "b@1<0", "null@1<0", "c@1<0", "d@2<3", "e@0<-1"];

    private static ShapeElement E(string name, params ShapeElement[] children) =>
        new() { Name = name, Children = children.Length == 0 ? null : children };

    private static List<string> Walk(ShapeElement[] roots, string? skip = null)
    {
        var visits = new List<string>();
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element)) break;

            visits.Add($"{element?.Name ?? "null"}@{walk.Depth}<{walk.Parent}");
            if (element?.Name == skip) walk.Skip();
        }

        if (walk.Failed) visits.Add("failed");

        return visits;
    }

    [Test]
    public void PreOrderWithDepthAndParentNullsIncluded()
    {
        ShapeElement[] roots = [E("a", E("b"), null!, E("c", E("d"))), E("e")];
        Assert.That(Walk(roots), Is.EqualTo(Visited));
    }

}

// The descriptor is only worth having if it implies the thing the cache trades on: two shapes that describe the same compile to the
// same poses, checked with the engine's own Animation.GenerateAllFrames over separately built shapes. And the compile replaces the
// engine's on every miss, while the animator indexes what it returns by position without checking anything: a pose one bit off is a
// wrong pose on screen. So "bit-identical" is checked against the engine over every animation the game ships and over the player's
// shape, step-parented the way the game builds it.
public sealed class AnimationFramesGoldenTests
{

    // Engine first, then Komet's compile on the same Animation. Where the engine throws, the compile must decline or throw the same
    // exception type out of the engine method they share (CacheInverseTransformMatrixRecursive); where it does not, it must not
    // decline and must match it bit for bit.
    private static int Check(Shape shape, string name, List<string> failures)
    {
        var rejected = 0;
        foreach (var animation in shape.Animations ?? [])
        {
            Exception? thrown = null;
            try
            {
                animation.GenerateAllFrames(shape.Elements, shape.JointsById);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            var engine = animation.PrevNextKeyFrameByFrame;
            animation.PrevNextKeyFrameByFrame = null;
            bool compiled;
            try
            {
                compiled = AnimationFrames.Compile(animation, shape.Elements, shape.JointsById);
            }
            catch (Exception e) when (thrown != null && e.GetType() == thrown.GetType())
            {
                compiled = false;
            }

            if (thrown != null)
            {
                rejected++;
                if (compiled)
                {
                    failures.Add($"{name}/{animation.Code}: the engine threw {thrown.GetType().Name}, Komet compiled");
                }

                continue;
            }

            if (!compiled)
            {
                failures.Add($"{name}/{animation.Code}: declined");
            }
            else if (AnimationShapes.Diff(engine, animation.PrevNextKeyFrameByFrame) is { } diff)
            {
                failures.Add($"{name}/{animation.Code}: {diff}");
            }
            // what the game ships holds no object twice, so the cache answers it
            if (AnimationFramesTests.Descriptor(animation, shape.Elements) is null)
            {
                failures.Add($"{name}/{animation.Code}: not described");
            }
        }

        return rejected;
    }

    [Test]
    [Category("Slow")]
    public void EveryAnimatedVanillaShapeCompilesBitIdentical()
    {
        GameInstall.RequireAssets();
        var failures = new List<string>();
        int shapes = 0, animations = 0, rejected = 0;
        foreach (var (domain, path, text) in AnimationShapes.Animated())
        {
            Shape shape;
            try
            {
                shape = AnimationShapes.Resolve(AnimationShapes.Parse(domain, path, text), path);
            }
            catch (Exception e) when (e is not AssertionException)
            {
                continue; // a file the game would not load either
            }

            if (shape.Animations is not { Length: > 0 } || shape.Elements is null) continue;

            rejected += Check(shape, $"{domain}:{path}", failures);
            shapes++;
            animations += shape.Animations.Length;
        }

        TestContext.Out.WriteLine($"{shapes} shapes, {animations} animations, {rejected} the engine rejects");
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
            Assert.That(shapes, Is.GreaterThan(600),
                "the shape walk found far fewer animated shapes than the game ships");
            Assert.That(animations, Is.GreaterThan(5000));
        });
    }

    // Seraph-faceless plus four skin parts and seven or eight pieces of gear, reparented by Shape.StepParentShape with the gear's
    // keyframes merged into the player's animations: the shape the cache and the compile exist for
    [TestCase("clothed")]
    [TestCase("armored")]
    [Category("Slow")]
    public void TheComposedPlayerCompilesBitIdentical(string outfit)
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Player(outfit);
        var failures = new List<string>();
        var rejected = Check(shape, "player-" + outfit, failures);
        var elements = AnimationShapes.Elements(shape.Elements).Count;
        TestContext.Out.WriteLine(
            $"{outfit}: {elements} elements, {shape.JointsById.Count} joints, {shape.Animations.Length} animations");
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
            Assert.That(rejected, Is.Zero, "every player animation compiles in the engine");
            Assert.That(shape.Animations, Has.Length.GreaterThan(200));
            Assert.That(elements, Is.GreaterThan(120),
                "the gear did not step-parent");
        });
    }

}

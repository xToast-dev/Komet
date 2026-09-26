using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace Komet.Test.Shapes;

// ShapeInitMemo skips Shape.InitForAnimations on a shape whose last init it remembers and which has not changed since. That is only
// sound if the skip leaves every shape exactly as the engine's init would: checked here against the engine itself over every animated
// vanilla shape, with clones inited in between (they share the keyframe element objects), on the composed player, and for every kind
// of change the memo has to notice.
public sealed class ShapeInitMemoTests
{
    private static readonly string[] Head = ["head"], BothMissing = ["gone", "cape"], OneMissing = ["gone"];

    private Harmony? _harmony;
    private int _mainThread;
    private long _skipped; // ShapeInitMemo.Skipped when the test patched

    private long Skipped => ShapeInitMemo.Skipped - _skipped;

    [SetUp]
    public void Remember()
    {
        _mainThread = RuntimeEnv.MainThreadId;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
    }

    [TearDown]
    public void Restore()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        RuntimeEnv.MainThreadId = _mainThread;
        (ShapeInitMemo.Enabled, Counting.Hud) = (true, false);
    }

    private void Patch(ILogger? logger = null, ulong fingerprint = ShapeInitMemo.Fingerprint)
    {
        _harmony = new Harmony("komet-test-shapeinitmemo");
        ShapeInitMemo.Install(_harmony, logger, fingerprint);
        (Counting.Hud, _skipped) = (true, ShapeInitMemo.Skipped);
    }

    private static void Init(Shape shape, string[]? disable = null, string[]? joints = null)
    {
        shape.InitForAnimations(AnimationShapes.Log, "test", disable, joints ?? Head);
    }

    private static object? Resolved(Shape shape)
    {
        return shape.Animations
            .SelectMany(a => a.KeyFrames).Select(AnimationShapes.Table.GetValue).FirstOrDefault(t => t is not null);
    }

    // An uncloned entity shape's life: ShapeTesselatorManager resolves it, the first entity of its type inits it twice
    // (AnimationCache.InitManager, then LoadAnimator on the cache miss), a gear-wearing or antlered relative clones it and inits
    // the clone – whose keyframe entries are the shared shape's own objects – and every further entity of the type inits it again
    [SuppressMessage("Design", "CA1031",
        Justification = "whatever the engine throws on a shape is data here, for both sides alike")]
    private static (List<string> State, string? Thrown) Life(string domain, string path, string text)
    {
        var shape = AnimationShapes.Parse(domain, path, text);
        try
        {
            shape.ResolveReferences(AnimationShapes.Log, path);
            Shape.CacheInvTransforms(shape.Elements);
            Init(shape);
            Init(shape);
            var clone = shape.Clone();
            var gone = shape.Animations?.SelectMany(a => a.KeyFrames)
                .SelectMany(k => k.Elements?.Keys.AsEnumerable() ?? [])
                .FirstOrDefault(name => name != "head");
            if (gone != null) clone.RemoveElements([gone]);
            Init(clone, gone == null ? null : [gone]);
            Init(shape);
            var second = shape.Clone();
            Init(second);
            Init(shape);
            return (AnimationShapes.State(shape, clone, second), null);
        }
        catch (Exception e) when (e is not AssertionException)
        {
            return (AnimationShapes.State(shape), e.GetType().Name);
        }
    }

    [Test]
    public void EveryAnimatedVanillaShapeEndsAsTheEngineLeavesIt()
    {
        AnimationShapes.RequireAssets();
        Patch();
        var failures = new List<string>();
        int shapes = 0, skipped = 0, thrown = 0;
        foreach (var (domain, path, text) in AnimationShapes.Animated())
        {
            ShapeInitMemo.Enabled = false;
            var engine = Life(domain, path, text);
            ShapeInitMemo.Enabled = true;
            var before = ShapeInitMemo.Skipped;
            var memo = Life(domain, path, text);
            shapes++;
            thrown += engine.Thrown is null ? 0 : 1;
            if (ShapeInitMemo.Skipped - before == 2) skipped++;
            if (memo.Thrown != engine.Thrown)
            {
                failures.Add($"{domain}:{path}: engine threw {engine.Thrown}, memo {memo.Thrown}");
            }
            else if (!memo.State.SequenceEqual(engine.State))
            {
                var at = memo.State.Zip(engine.State).TakeWhile(p => p.First == p.Second).Count();
                failures.Add(
                    $"{domain}:{path}: line {at}: memo '{memo.State.ElementAtOrDefault(at)}' engine '{engine.State.ElementAtOrDefault(at)}'");
            }
        }

        TestContext.Out.WriteLine(
            $"{shapes} shapes, {skipped} answered from the memo twice, {thrown} the engine throws on");
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty);
            Assert.That(shapes, Is.GreaterThan(100));
            Assert.That(skipped, Is.GreaterThanOrEqualTo(shapes - thrown),
                "shapes the memo never answered for prove nothing");
        });
    }

    // The player the way the game builds it on every re-tesselation: a clone of the shared seraph shape with skin parts and clothing
    // step-parented in, whose keyframes carry the shared entry objects next to the gear's own
    private static List<string> Players(string outfit)
    {
        var seraph = AnimationShapes.Seraph();
        Init(seraph);
        Init(seraph);
        var player = AnimationShapes.Compose(seraph, outfit);
        Init(player, AnimationShapes.PlayerDisable);
        Init(seraph);
        var other = AnimationShapes.Compose(seraph, "naked");
        Init(other, AnimationShapes.PlayerDisable);
        Init(seraph);
        return AnimationShapes.State(seraph, player, other);
    }

    [TestCase("clothed")]
    [TestCase("armored")]
    public void TheSharedPlayerShapeEndsAsTheEngineLeavesItWithComposedClonesInBetween(string outfit)
    {
        AnimationShapes.RequireAssets();
        Patch();
        ShapeInitMemo.Enabled = false;
        var engine = Players(outfit);
        ShapeInitMemo.Enabled = true;
        var memo = Players(outfit);
        Assert.Multiple(() =>
        {
            Assert.That(memo, Is.EqualTo(engine));
            Assert.That(Skipped, Is.EqualTo(2));
        });
    }

    // Twice through the engine, then a third init that the memo answers or not; the same on a twin with the memo off
    private static void AssertThirdInit(Action<Shape> change, bool skipped, string[]? disable = null,
        string[]? joints = null)
    {
        var (engine, memo) = (AnimationShapes.Synthetic(), AnimationShapes.Synthetic());
        ShapeInitMemo.Enabled = false;
        Init(engine);
        Init(engine);
        change(engine);
        Init(engine, disable, joints);
        ShapeInitMemo.Enabled = true;
        Init(memo);
        Init(memo);
        change(memo);
        var (before, count) = (Resolved(memo), ShapeInitMemo.Skipped);
        Init(memo, disable, joints);
        Assert.Multiple(() =>
        {
            Assert.That(AnimationShapes.State(memo), Is.EqualTo(AnimationShapes.State(engine)));
            Assert.That(ShapeInitMemo.Skipped - count, Is.EqualTo(skipped ? 1 : 0));
            Assert.That(ReferenceEquals(Resolved(memo), before), Is.EqualTo(skipped),
                "whether the engine made new tables");
        });
    }

    [Test]
    public void AnUnchangedShapeIsAnsweredFromTheMemo()
    {
        Patch();
        AssertThirdInit(_ => { }, true);
    }

    [Test]
    public void TheFirstTwoInitsRunTheEngine()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        Init(shape);
        var first = Resolved(shape);
        Init(shape);
        Assert.Multiple(() =>
        {
            Assert.That(Resolved(shape), Is.Not.SameAs(first));
            Assert.That(Skipped, Is.Zero);
        });
    }

    private static IEnumerable<TestCaseData> Changes()
    {
        yield return new TestCaseData((Action<Shape>)(s => s.Elements[0].Children![0].inverseModelTransform = null),
                true)
            .SetName("ADroppedInverseTransformIsCachedAgainAsTheEngineWould");
        yield return new TestCaseData(
                (Action<Shape>)(s => s.Elements[0].Children![1].Children =
                [
                    .. s.Elements[0].Children![1].Children!,
                    new ShapeElement { Name = "finger", From = [0, 0, 0], To = [1, 1, 1], RotationOrigin = [0, 0, 0] }
                ]), false)
            .SetName("AGrownTreeRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s =>
                    (s.Elements[0].Children![0], s.Elements[0].Children![1]) =
                    (s.Elements[0].Children![1], s.Elements[0].Children![0])), false)
            .SetName("ReorderedChildrenRunTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Elements[0].Children![1].Name = "leg"), false).SetName(
            "ARenamedElementRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Animations[0].Code = "Wave"), false).SetName(
            "ARecasedCodeRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Animations[1].Version = 2), false).SetName(
            "AnotherVersionRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Elements[0].Children![1].JointId = 7), false).SetName(
            "AJointIdSetElsewhereRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Elements[0].Children![1].ParentElement = null), false)
            .SetName("ALostParentRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => AnimationShapes.Table.SetValue(s.Animations[0].KeyFrames[1],
                new FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>(0))), false)
            .SetName("AReplacedTableRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s =>
                    s.Animations[0].KeyFrames[1].Elements["hand"] = new AnimationKeyFrameElement { RotationX = 3 }),
                false)
            .SetName("AReplacedEntryRunsTheEngine");
        yield return new TestCaseData((Action<Shape>)(s => s.Animations[0].KeyFrames[1].Elements.Remove("hand")), false)
            .SetName("ARemovedEntryRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s => s.Animations[0].KeyFrames[1] = new AnimationKeyFrame { Frame = 5, Elements = [] }),
                false)
            .SetName("AReplacedKeyFrameRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s => s.AnimationsByCrc32[AnimationMetaData.GetCrc32("idle")] = s.Animations[0]), false)
            .SetName("AReplacedCrcEntryRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s =>
                    s.JointsById[1] = new AnimationJoint { JointId = 1, Element = s.JointsById[1].Element }), false)
            .SetName("AReplacedJointRunsTheEngine");
        yield return new TestCaseData(
                (Action<Shape>)(s => s.JointsById[99] = new AnimationJoint { JointId = 99, Element = s.Elements[0] }),
                false)
            .SetName("AnAddedJointRunsTheEngine");
    }

    [TestCaseSource(nameof(Changes))]
    public void AnythingTheInitReadsOrWritesBeingDifferentRunsTheEngine(Action<Shape> change, bool skipped)
    {
        ArgumentNullException.ThrowIfNull(change);
        Patch();
        AssertThirdInit(change, skipped);
    }

    // Keys with no element: under no disable list, or one naming them, the engine leaves their ForElement alone; under any other it
    // makes each a new placeholder element, so the memo must not answer
    private static IEnumerable<TestCaseData> DisableLists()
    {
        yield return new TestCaseData(null, true).SetName("NoDisableListLeavesMissingKeysAlone");
        yield return new TestCaseData(BothMissing, true).SetName("ADisableListNamingTheMissingKeysLeavesThemAlone");
        yield return new TestCaseData(OneMissing, false).SetName("ADisableListMissingOneMakesAPlaceholder");
        yield return new TestCaseData(Array.Empty<string>(), false).SetName("AnEmptyDisableListMakesPlaceholders");
    }

    [TestCaseSource(nameof(DisableLists))]
    public void ADisableListMattersOnlyForKeysWithoutAnElement(string[]? disable, bool skipped)
    {
        Patch();
        AssertThirdInit(_ => { }, skipped, disable);
    }

    [Test]
    public void OtherJointsRunTheEngine()
    {
        Patch();
        AssertThirdInit(_ => { }, false, joints: ["head", "hand"]);
    }

    // A clone shares the entry objects and its init points them at the clone's elements; the skip must point them back
    [Test]
    public void AClonesInitInBetweenIsUndone()
    {
        Patch();
        AssertThirdInit(s =>
        {
            var clone = s.Clone();
            Init(clone, ["cape"]);
            Assert.That(s.Animations[0].KeyFrames[0].Elements["arm"].ForElement,
                Is.Not.SameAs(s.Elements[0].Children![1]));
        }, true);
    }

    [Test]
    public void AnInitThatThrewIsNotSkippedOver()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        Init(shape);
        Init(shape);
        var arm = shape.Elements[0].Children![1];
        Assert.That(() => shape.InitForAnimations(AnimationShapes.Log, "test", (string[]?)null, null!),
            Throws.TypeOf<NullReferenceException>());
        Assert.That(arm.JointId, Is.Zero, "the failed init did not get as far as the test needs");
        Init(shape);
        Assert.Multiple(() =>
        {
            Assert.That(arm.JointId, Is.Not.Zero, "the engine ran over what the failed init left");
            Assert.That(Skipped, Is.Zero);
        });
        Init(shape);
        Assert.That(Skipped, Is.EqualTo(1), "and the init after it is remembered again");
    }

    [Test]
    public void OffTheMainThreadTheEngineRuns()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        Init(shape);
        Init(shape);
        var before = Resolved(shape);
        var thread = new Thread(() => Init(shape));
        thread.Start();
        thread.Join();
        Assert.Multiple(() =>
        {
            Assert.That(Resolved(shape), Is.Not.SameAs(before));
            Assert.That(Skipped, Is.Zero);
        });
        Init(shape);
        Assert.That(Skipped, Is.Zero, "the other thread's init replaced the tables the memo knew");
    }

    [Test]
    public void DisabledTheEngineRuns()
    {
        Patch();
        AssertThirdInit(_ => ShapeInitMemo.Enabled = false, false);
    }

    private static void ForeignPostfix()
    {
        // another mod's patch: what it does does not matter, only that it is there
    }

    // A patch on a method the skipped init calls would miss that call; logged once, however often the recheck asks
    [Test]
    public void AForeignPatchInsideTheInitRunsTheEngine()
    {
        var joints = AccessTools.DeclaredMethod(typeof(Shape), nameof(Shape.ResolveAndFindJoints),
            [typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(string[])]);
        var foreign = new Harmony("komet-test-shapeinitmemo-foreign");
        var logger = new CapturingLogger();
        try
        {
            _ = foreign.Patch(joints, postfix: new HarmonyMethod(typeof(ShapeInitMemoTests), nameof(ForeignPostfix)));
            Patch(logger);
            ShapeInitMemo.Recheck();
            AssertThirdInit(_ => { }, false);
            Assert.That(ShapeInitMemo.Blocked, Is.True);
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("another mod"));
        }
        finally
        {
            foreign.UnpatchAll(foreign.Id);
        }
    }

    private static void Elsewhere(AnimationKeyFrame kf)
    {
        foreach (var entry in kf.Elements?.Values.AsEnumerable() ?? []) entry.ForElement = null;
    }

    private static void Regenerated()
    {
        // a no-op: patching the caller only makes the JIT compile it again, now calling the patched method
    }

    // The write-back resolves ForElement by the memo's own name lookup. A patch that changes what the engine writes there, applied
    // after the recheck for foreign patches, must not be replayed wrongly: the memo proves every init it remembers left exactly what
    // it would write, and here none did, so it never answers. The next recheck then stands the memo down.
    [Test]
    public void AResolveTheMemoCannotReproduceIsNeverAnswered()
    {
        Patch();
        var resolve = AccessTools.DeclaredMethod(typeof(Shape), "ResolveReferences",
        [
            typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(AnimationKeyFrame),
            typeof(string[])
        ]);
        var late = new Harmony("komet-test-shapeinitmemo-late");
        try
        {
            _ = late.Patch(resolve, postfix: new HarmonyMethod(typeof(ShapeInitMemoTests), nameof(Elsewhere)));
            // Earlier tests in the same process tier up CollectAndResolveReferences with the private resolve inlined (PGO lets a
            // hot call site take its 166 bytes of IL), and a patch cannot reach an inlined copy: regenerate the caller too
            var collect = AccessTools.DeclaredMethod(typeof(Shape), nameof(Shape.CollectAndResolveReferences),
                [typeof(ILogger), typeof(string), typeof(string[])]);
            _ = late.Patch(collect, postfix: new HarmonyMethod(typeof(ShapeInitMemoTests), nameof(Regenerated)));
            Assert.That(ShapeInitMemo.Blocked, Is.False, "the patch came after the recheck");
            AssertThirdInit(_ => { }, false);
            Assert.That(Skipped, Is.Zero);
            ShapeInitMemo.Recheck();
            Assert.That(ShapeInitMemo.Blocked, Is.True);
        }
        finally
        {
            late.UnpatchAll(late.Id);
        }
    }

    // InitOnce skips the local player's second init through the memo's patch; its own patches are not foreign to the memo, nor the
    // memo's to InitOnce
    [Test]
    public void NextToInitOnceTheMemoStillAnswers()
    {
        _harmony = new Harmony("komet-test-shapeinitmemo");
        InitOnce.Install(_harmony, null);
        ShapeInitMemo.Install(_harmony, null);
        (Counting.Hud, _skipped) = (true, ShapeInitMemo.Skipped);
        Assert.Multiple(() =>
        {
            Assert.That(InitOnce.Blocked, Is.False);
            Assert.That(ShapeInitMemo.Blocked, Is.False);
        });
        AssertThirdInit(_ => { }, true);
    }

    // The fingerprints of the bodies the memo's proof rests on, in Vintage Story 1.22.7: a game update that fails here needs the golden
    // tests re-run and the constant renewed
    [Test]
    public void TheBodiesTheMemoReliesOnAreThoseOfTheInstalledEngine()
    {
        var found = EngineShape.Of(ShapeInitMemo.Shaped());
        Assert.That(found, Is.EqualTo(ShapeInitMemo.Fingerprint),
            $"an engine body the memo relies on changed: 0x{found:X16}UL");
    }

    // A changed engine: every init runs, with one warning, and the patch InitOnce rides on stays
    [Test]
    public void AChangedEngineRunsEveryInit()
    {
        var logger = new CapturingLogger();
        Patch(logger, ShapeInitMemo.Fingerprint ^ 1);
        AssertThirdInit(_ => { }, false);
        Assert.Multiple(() =>
        {
            Assert.That(ShapeInitMemo.Blocked, Is.True);
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
            Assert.That(_harmony!.GetPatchedMethods(), Has.Exactly(1).Items);
        });
    }
}

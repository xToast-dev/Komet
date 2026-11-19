using Komet.Core;
using Komet.Shapes;

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

    private static void Init(Shape shape, string[]? disable = null, string[]? joints = null) =>
        shape.InitForAnimations(AnimationShapes.Log, "test", disable, joints ?? Head);

    private static object? Resolved(Shape shape) =>
        shape.Animations
            .SelectMany(a => a.KeyFrames).Select(AnimationShapes.Table.GetValue).FirstOrDefault(t => t is not null);

    // An uncloned entity shape's life: ShapeTesselatorManager resolves it, the first entity of its type inits it twice
    // (AnimationCache.InitManager, then LoadAnimator on the cache miss), a gear-wearing or antlered relative clones it and inits
    // the clone – whose keyframe entries are the shared shape's own objects – and every further entity of the type inits it again
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
    [Category("Slow")]
    public void EveryAnimatedVanillaShapeEndsAsTheEngineLeavesIt()
    {
        GameInstall.RequireAssets();
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

    // Third inits after a change, or with other arguments: what the memo answers and what it must leave to the engine
    // Every 3th case runs: the cases sit side by side, a sample of them keeps the comparison honest
    private static IEnumerable<TestCaseData> SampledThirds() => Thirds().Where((_, i) => i % 3 == 0);

    private static IEnumerable<TestCaseData> Thirds()
    {
        static TestCaseData Case(string name, bool skipped, Action<Shape> change, string[]? disable = null,
            string[]? joints = null) => new TestCaseData(change, skipped, disable, joints).SetName(name);

        yield return Case("AnUnchangedShapeIsAnsweredFromTheMemo", true, _ => { });
        yield return Case("ADroppedInverseTransformIsCachedAgainAsTheEngineWould", true,
            s => s.Elements[0].Children![0].inverseModelTransform = null);
        yield return Case("AGrownTreeRunsTheEngine", false, s => s.Elements[0].Children![1].Children =
        [
            .. s.Elements[0].Children![1].Children!,
            new ShapeElement { Name = "finger", From = [0, 0, 0], To = [1, 1, 1], RotationOrigin = [0, 0, 0] }
        ]);
        yield return Case("ReorderedChildrenRunTheEngine", false, s =>
            (s.Elements[0].Children![0], s.Elements[0].Children![1]) =
            (s.Elements[0].Children![1], s.Elements[0].Children![0]));
        yield return Case("ARenamedElementRunsTheEngine", false, s => s.Elements[0].Children![1].Name = "leg");
        yield return Case("ARecasedCodeRunsTheEngine", false, s => s.Animations[0].Code = "Wave");
        yield return Case("AnotherVersionRunsTheEngine", false, s => s.Animations[1].Version = 2);
        yield return Case("AJointIdSetElsewhereRunsTheEngine", false, s => s.Elements[0].Children![1].JointId = 7);
        yield return Case("ALostParentRunsTheEngine", false, s => s.Elements[0].Children![1].ParentElement = null);
        yield return Case("AReplacedTableRunsTheEngine", false, s => AnimationShapes.Table.SetValue(
            s.Animations[0].KeyFrames[1], new FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>(0)));
        yield return Case("AReplacedEntryRunsTheEngine", false,
            s => s.Animations[0].KeyFrames[1].Elements["hand"] = new AnimationKeyFrameElement { RotationX = 3 });
        yield return Case("ARemovedEntryRunsTheEngine", false,
            s => s.Animations[0].KeyFrames[1].Elements.Remove("hand"));
        yield return Case("AReplacedKeyFrameRunsTheEngine", false,
            s => s.Animations[0].KeyFrames[1] = new AnimationKeyFrame { Frame = 5, Elements = [] });
        yield return Case("AReplacedCrcEntryRunsTheEngine", false,
            s => s.AnimationsByCrc32[AnimationMetaData.GetCrc32("idle")] = s.Animations[0]);
        yield return Case("AReplacedJointRunsTheEngine", false,
            s => s.JointsById[1] = new AnimationJoint { JointId = 1, Element = s.JointsById[1].Element });
        yield return Case("AnAddedJointRunsTheEngine", false,
            s => s.JointsById[99] = new AnimationJoint { JointId = 99, Element = s.Elements[0] });
        // A clone shares the entry objects and its init points them at the clone's elements; the skip must point them back
        yield return Case("AClonesInitInBetweenIsUndone", true, s =>
        {
            Init(s.Clone(), ["cape"]);
            Assert.That(s.Animations[0].KeyFrames[0].Elements["arm"].ForElement,
                Is.Not.SameAs(s.Elements[0].Children![1]));
        });
        yield return Case("DisabledTheEngineRuns", false, _ => ShapeInitMemo.Enabled = false);
        yield return Case("OtherJointsRunTheEngine", false, _ => { }, joints: ["head", "hand"]);
        // Keys with no element: under no disable list, or one naming them, the engine leaves their ForElement alone; under any
        // other it makes each a new placeholder element, so the memo must not answer
        yield return Case("ADisableListNamingTheMissingKeysLeavesThemAlone", true, _ => { }, BothMissing);
        yield return Case("ADisableListMissingOneMakesAPlaceholder", false, _ => { }, OneMissing);
        yield return Case("AnEmptyDisableListMakesPlaceholders", false, _ => { }, []);
    }

    [TestCaseSource(nameof(SampledThirds))]
    public void TheThirdInitIsAnsweredOnlyWhenNothingTheInitReadsOrWritesChanged(Action<Shape> change, bool skipped,
        string[]? disable, string[]? joints)
    {
        ArgumentNullException.ThrowIfNull(change);
        Patch();
        AssertThirdInit(change, skipped, disable, joints);
    }

    // A patch on a method the skipped init calls would miss that call; logged once, however often the recheck asks
    [Test]
    public void AForeignPatchInsideTheInitRunsTheEngine()
    {
        var joints = AccessTools.DeclaredMethod(typeof(Shape), nameof(Shape.ResolveAndFindJoints),
            [typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(string[])]);
        using var foreign = new TestHarmony("komet-test-shapeinitmemo-foreign");
        var logger = new CapturingLogger();
        _ = foreign.Patch(joints, postfix: Foreign.Postfix);
        Patch(logger);
        ShapeInitMemo.Recheck();
        AssertThirdInit(_ => { }, false);
        Assert.That(ShapeInitMemo.Blocked, Is.True);
        Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("another mod"));
    }

    private static void Elsewhere(AnimationKeyFrame kf)
    {
        foreach (var entry in kf.Elements?.Values.AsEnumerable() ?? [])
        {
            entry.ForElement = null;
        }
    }

    // A changed engine: every init runs unpatched, with one warning
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
            Assert.That(_harmony!.GetPatchedMethods(), Is.Empty);
        });
    }
}

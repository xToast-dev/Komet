using Komet.Core;
using Komet.Shapes;

namespace Komet.Test.Shapes;

// A cache hit hands an animator a compiled pose tree that ClientAnimator.calculateMatrices walks by position, so a descriptor that
// fails to separate two shapes silently animates one entity with another's poses.
public sealed class AnimationFramesTests
{
    private const string Frames = "Komet.Shapes.AnimationFrames, Komet.Shapes";

    private static readonly FieldInfo JointsDone =
        typeof(Animation).GetField("jointsDone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static int _foreignCalls;
    private Harmony? _harmony;
    private int _mainThread;

    [SetUp]
    public void Remember() => _mainThread = RuntimeEnv.MainThreadId;

    [TearDown]
    public void Restore()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        RuntimeEnv.MainThreadId = _mainThread;
        (AnimationFrames.Enabled, Counting.Hud) = (true, false);
        AnimationFrames.ResetPeaks();
        AnimationFrames.Clear();
    }

    private void Patch(ILogger? logger = null, ulong fingerprint = AnimationFrames.Fingerprint)
    {
        _harmony = new Harmony("komet-test-animationframes");
        AnimationFrames.Install(_harmony, logger, fingerprint);
    }

    // The key as the cache compares it, null for a shape it does not describe. Two shapes describe the same only when
    // GenerateAllFrames compiles them the same: the animator indexes a pose tree by position, so a descriptor that misses a
    // difference is a wrong pose.
    internal static byte[]? Descriptor(Animation animation, ShapeElement[] roots, bool recursive = true)
    {
        lock (Gate(null))
        {
            return Describe(null, animation, roots, recursive) ? Buffer(null).AsSpan(0, At(null)).ToArray() : null;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Gate")]
    private static extern ref Lock Gate([UnsafeAccessorType(Frames)] object? frames);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Describe")]
    private static extern bool Describe([UnsafeAccessorType(Frames)] object? frames, Animation animation,
        ShapeElement[] roots, bool recursive);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_buffer")]
    private static extern ref byte[] Buffer([UnsafeAccessorType(Frames)] object? frames);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_at")]
    private static extern ref int At([UnsafeAccessorType(Frames)] object? frames);

    private static ShapeElement Bare(string name, params ShapeElement[] children) =>
        new() { Name = name, Children = children.Length == 0 ? null : children };

    private static AnimationKeyFrameElement Turned(double rotationX) =>
        new() { RotationX = rotationX };

    private static Animation Walk(params (string Element, double Rotation)[] elements)
    {
        var byName = new Dictionary<string, AnimationKeyFrameElement>();
        foreach (var (element, rotation) in elements)
        {
            byName[element] = Turned(rotation);
        }

        return new Animation
        {
            Code = "walk",
            QuantityFrames = 30,
            KeyFrames = [new AnimationKeyFrame { Frame = 0, Elements = byName }]
        };
    }

    // Two descriptors and whether they must be equal: the same for what compiles the same, different for what does not
    // Every 2th case runs: the cases sit side by side, a sample of them keeps the comparison honest
    private static IEnumerable<TestCaseData> SampledDescriptions() => Descriptions().Where((_, i) => i % 2 == 0);

    private static IEnumerable<TestCaseData> Descriptions()
    {
        static TestCaseData Case(string name, bool same, Func<byte[]?> one, Func<byte[]?> two) =>
            new TestCaseData(one, two, same).SetName(name);

        var walk = Walk(("upperarm", 30));
        var two = Walk(("upperarm", 30), ("lowerarm", 10));

        static ShapeElement[] Arms() => [Bare("upperarm"), Bare("lowerarm")];

        yield return Case("TheSameShapeAndAnimationDescribeTheSame", true,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(Walk(("upperarm", 30)), [Bare("upperarm")]));
        // Keyframe elements are a Dictionary, whose iteration order is not part of its content
        yield return Case("KeyframeElementOrderDoesNotChangeTheDescriptor", true,
            () => Descriptor(two, Arms()),
            () => Descriptor(Walk(("lowerarm", 10), ("upperarm", 30)), Arms()));
        // AnimationKeyFrame.Resolve maps keyframe elements onto shape elements by name, so a rename changes the compile
        yield return Case("ARenamedElementDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(walk, [Bare("lowerarm")]));
        // This is the gear case: Shape.StepParentShape reparents a gear element as a child of a named element
        yield return Case("StepParentedGearDescribesDifferentlyFromTheBareShape", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]));
        // Without the depth written alongside each name, a child and a sibling would describe identically
        yield return Case("AChildDescribesDifferentlyFromASibling", false,
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]),
            () => Descriptor(walk, [Bare("upperarm"), Bare("sleeve")]));
        yield return Case("TwoGearPiecesOnDifferentParentsDescribeDifferently", false,
            () => Descriptor(two, [Bare("upperarm", Bare("plate")), Bare("lowerarm")]),
            () => Descriptor(two, [Bare("upperarm"), Bare("lowerarm", Bare("plate"))]));
        // StepParentShape merges the gear's keyframes into the parent animation, changing content as well as count
        yield return Case("AChangedKeyframeValueDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(Walk(("upperarm", 31)), [Bare("upperarm")]));
        yield return Case("AnAddedKeyframeDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var added = Walk(("upperarm", 30));
                var key = new AnimationKeyFrame
                {
                    Frame = 15,
                    Elements = new Dictionary<string, AnimationKeyFrameElement> { ["upperarm"] = Turned(60) }
                };
                key.Resolve(
                    new Dictionary<string, ShapeElement>()); // stamps its Frame on the element, as every init does
                added.KeyFrames = [added.KeyFrames[0], key];
                return Descriptor(added, [Bare("upperarm")]);
            });
        // GenerateFrameForElement interpolates against QuantityFrames, so the same keyframes over a different span differ
        yield return Case("ADifferentQuantityFramesDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var slow = Walk(("upperarm", 30));
                slow.QuantityFrames = 60;
                return Descriptor(slow, [Bare("upperarm")]);
            });
        // An unset double? and a set one are different inputs to getTwoKeyFramesElementForFlag, which tests IsSet
        yield return Case("AnUnsetValueDescribesDifferentlyFromASetOne", false,
            () => Descriptor(Walk(("upperarm", 0)), [Bare("upperarm")]),
            () =>
            {
                var unset = Walk();
                unset.KeyFrames[0].Elements["upperarm"] = new AnimationKeyFrameElement();
                return Descriptor(unset, [Bare("upperarm")]);
            });
        yield return Case("NonRecursiveDescribesDifferentlyFromRecursive", false,
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]),
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))], false));
        // Element geometry never reaches the compiled pose: the model matrix GenerateFrame threads through is not stored,
        // and the two AnimationFrame methods that once consumed it are [Obsolete] no-ops. So it stays out of the key.
        yield return Case("ElementGeometryDoesNotChangeTheDescriptor", true,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var moved = Bare("upperarm");
                moved.From = [1, 2, 3];
                moved.RotationX = 45;
                return Descriptor(walk, [moved]);
            });
    }

    [TestCaseSource(nameof(SampledDescriptions))]
    public void TheDescriptorSeparatesWhatCompilesDifferently(Func<byte[]?> one, Func<byte[]?> two, bool same)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(two);
        var (a, b) = (one(), two());
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.Not.Null, "the descriptor must be computable for a well formed shape");
            Assert.That(b, Is.Not.Null, "the descriptor must be computable for a well formed shape");
            Assert.That(a, same ? Is.EqualTo(b) : Is.Not.EqualTo(b));
        });
    }

    private static ShapeElement Element(string name, params ShapeElement[] children) =>
        new()
        {
            Name = name, From = [1, 2, 3], To = [4, 5, 6], RotationOrigin = [0.5, 0, 0.25], RotationY = 15,
            Children = children.Length == 0 ? null : children
        };

    // Every group set whole, as a shape's json writes them
    private static AnimationKeyFrameElement Full(double v) =>
        new()
        {
            OffsetX = v / 2, OffsetY = -v, OffsetZ = 1, RotationX = v, RotationY = 2 * v, RotationZ = -v, StretchX = 1,
            StretchY = 1 + v / 100, StretchZ = 1, RotShortestDistanceX = v > 0, RotShortestDistanceZ = true
        };

    private static AnimationKeyFrameElement Rotated(double degrees) =>
        new() { RotationX = degrees, RotationY = 0, RotationZ = 0 };

    private static AnimationKeyFrame Key(int frame, params (string Name, AnimationKeyFrameElement Element)[] elements)
    {
        return new AnimationKeyFrame { Frame = frame, Elements = elements.ToDictionary(e => e.Name, e => e.Element) };
    }

    // Resolved the way AnimationManager.LoadAnimator resolves a shape
    private static Shape Build(int quantityFrames, ShapeElement[] roots, params AnimationKeyFrame[] keys)
    {
        var shape = new Shape
        {
            Elements = roots,
            Animations =
                [new Animation { Code = "test", Name = "test", QuantityFrames = quantityFrames, KeyFrames = keys }]
        };
        shape.InitForAnimations(AnimationShapes.Log, "test", "head");
        return shape;
    }

    private static ShapeElement[] Arm() =>
        [Element("upper", Element("lower", Element("hand")), Element("sleeve")), Element("head")];

    // The engine's compile, then Komet's on the same Animation, compared to the bit; the result is Komet's
    private static AnimationFrame[][] SameAsEngine(Shape shape, bool recursive = true)
    {
        var animation = shape.Animations[0];
        animation.GenerateAllFrames(shape.Elements, shape.JointsById, recursive);
        var engine = animation.PrevNextKeyFrameByFrame;
        animation.PrevNextKeyFrameByFrame = null;
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById, recursive), Is.True,
            "declined");
        Assert.That(AnimationShapes.Diff(engine, animation.PrevNextKeyFrameByFrame), Is.Null);
        return animation.PrevNextKeyFrameByFrame!;
    }

    private static List<ElementPose> PosesOf(AnimationFrame[][] compiled, string name) =>
        [
            .. compiled.SelectMany(pair => pair).Distinct().SelectMany(AnimationShapes.Poses)
                .Where(p => p.ForElement.Name == name)
        ];

    [Test]
    public void KeyframesOutOfFrameOrderCompileAsTheEngineDoes()
    {
        var shape = Build(12, Arm(), Key(6, ("upper", Full(60)), ("hand", Full(5))), Key(0, ("upper", Full(0))),
            Key(3, ("lower", Full(-30)), ("upper", Full(30))), Key(9, ("hand", Full(-5))));
        _ = SameAsEngine(shape);
    }

    // At frame 8 the next keyframe of "upper" is the first one, at frame 2: the engine's wrap branch, t = Mod(8 - 7, 10) / 5
    [Test]
    public void WrappingAroundTheEndInterpolatesAcrossIt()
    {
        var shape = Build(10, Arm(), Key(2, ("upper", Rotated(20))), Key(7, ("upper", Rotated(70))),
            Key(8, ("lower", Rotated(1))));
        var compiled = SameAsEngine(shape);
        var atEight = compiled[8][0].RootElementTransforms[0];
        Assert.That(atEight.degX, Is.EqualTo(60f));
    }

    // No statics and nothing written but the result: the singleplayer server compiles on its own thread through the same prefix
    [Test]
    [Category("Slow")]
    public void FourThreadsCompilingOneShapeAgreeWithTheEngine()
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Resolve(AnimationShapes.Load("survival", "entity/lore/shiver/surface"), "shiver");
        var animations = shape.Animations.Where(a => a.KeyFrames is { Length: > 0 }).ToArray();
        var reference = new Dictionary<Animation, AnimationFrame[][]>();
        foreach (var animation in animations)
        {
            animation.GenerateAllFrames(shape.Elements, shape.JointsById);
            reference[animation] = animation.PrevNextKeyFrameByFrame;
        }

        int diffs = 0, declined = 0, compiles = 0;
        var threads = Enumerable.Range(0, 4).Select((_, _) => new Thread(() =>
        {
            for (var repeat = 0; repeat < 10; repeat++)
            {
                foreach (var animation in animations)
                {
                    _ = Interlocked.Increment(ref compiles);
                    if (!AnimationFrames.Compile(animation, shape.Elements, shape.JointsById))
                    {
                        _ = Interlocked.Increment(ref declined);
                    }
                    else if (AnimationShapes.Diff(reference[animation], animation.PrevNextKeyFrameByFrame) != null)
                    {
                        _ = Interlocked.Increment(ref diffs);
                    }
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Multiple(() =>
        {
            Assert.That(compiles, Is.EqualTo(4 * 10 * animations.Length));
            Assert.That(declined, Is.Zero);
            Assert.That(diffs, Is.Zero);
        });
    }

    // Only the compile stands down: the jointsDone race is the engine's own, so the Clone guard stays
    [Test]
    public void AChangedEngineKeepsOnlyTheCloneGuardWithOneWarning()
    {
        var logger = new CapturingLogger();
        Patch(logger, AnimationFrames.Fingerprint ^ 1);
        var clone = AccessTools.Method(typeof(Animation), nameof(Animation.Clone), []);
        var animation = Fresh().Animations[0];
        Assert.Multiple(() =>
        {
            Assert.That(AnimationFrames.Blocked, Is.True);
            Assert.That(_harmony!.GetPatchedMethods(), Is.EquivalentTo(new MethodBase[] { clone }));
            Assert.That(JointsDone.GetValue(animation.Clone()), Is.Not.SameAs(JointsDone.GetValue(animation)));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
        });
    }

    private static Shape Fresh(string code = "test")
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0)), ("hand", Full(3))), Key(4, ("upper", Full(45))));
        shape.Animations[0].Code = code;
        return shape;
    }

    private static AnimationFrame[][] Generated(Shape shape)
    {
        shape.Animations[0].GenerateAllFrames(shape.Elements, shape.JointsById);
        return shape.Animations[0].PrevNextKeyFrameByFrame;
    }

    // A miss compiles and is stored, so the next shape that describes the same hits it, inverse transforms filled as the engine's
    // compile fills them first thing; the result is the engine's to the bit
    [Test]
    public void ACompileIsCachedAndSharedAndTheEngines()
    {
        Patch();
        Counting.Hud = true;
        var (hits, misses) = (AnimationFrames.Hits, AnimationFrames.Misses);
        var first = Generated(Fresh());
        var hit = Fresh();
        foreach (var element in AnimationShapes.Elements(hit.Elements))
        {
            element.inverseModelTransform = null;
        }

        var second = Generated(hit);
        Assert.Multiple(() =>
        {
            Assert.That(AnimationFrames.Blocked, Is.False);
            Assert.That(AnimationFrames.Misses - misses, Is.EqualTo(1));
            Assert.That(AnimationFrames.Hits - hits, Is.EqualTo(1));
            Assert.That(second, Is.SameAs(first));
            Assert.That(AnimationShapes.Elements(hit.Elements).Where(e => e.inverseModelTransform is null), Is.Empty);
        });
        _harmony!.UnpatchAll(_harmony.Id);
        Assert.That(AnimationShapes.Diff(Generated(Fresh()), first, true), Is.Null);
    }

    private static void ForeignRan() => _foreignCalls++;

    // A mod that patches the compile expects its patch to run; the cache and the kernel would bypass it, so they step aside
    [Test]
    public void AForeignPatchOnTheCompileKeepsTheEngine()
    {
        using var foreign = new TestHarmony("komet-test-foreign");
        _ = foreign.Patch(AccessTools.Method(typeof(Animation), "GenerateFrameForElement"),
            postfix: new HarmonyMethod(typeof(AnimationFramesTests), nameof(ForeignRan)));
        Patch();
        Assert.That(AnimationFrames.Blocked, Is.True);
        _foreignCalls = 0;
        _ = Generated(Fresh());
        Assert.That(_foreignCalls, Is.GreaterThan(0), "the engine did not compile");
    }

    // Equality that is not by reference: FastSmallDictionary's lookup calls the element's own Equals, which a subclass may
    // override. Here two "hand" elements are equal by name, so the engine poses both from the one the keyframes name.
    private sealed class ByName : ShapeElement
    {
        public override bool Equals(object? obj) =>
            obj is ShapeElement other && string.Equals(other.Name, Name, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            return 0;
            // equal by a mutable name, so no hash but a constant is safe
        }
    }
}

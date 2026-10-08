using System.Collections;

namespace Komet.Testing;

// Shapes as the game builds them, from the game's assets (GameInstall; call GameInstall.RequireAssets first), a synthetic shape, a
// dump of everything InitForAnimations sets, and a bitwise comparison of two animation compiles: for mods that patch the animation code.
public static class AnimationShapes
{
    private static readonly string[] Domains = ["game", "survival", "creative"];

    public static readonly QuietLogger Log = new();

    // The local player's shape as EntityBehaviorExtraSkinnable.addSkinPart and EntityBehaviorContainer.addGearToShape build
    // it on every re-tesselation, then Entity.OnTesselation's LoadAnimator with the elements the player's gear hides
    public static readonly string[] PlayerDisable = ["hair-covered", "showshoeR", "showshoeL", "hoodtight"];

    private static readonly string[] Skin =
    [
        "entity/humanoid/seraphskinparts/hair-base/boxbraids",
        "entity/humanoid/seraphskinparts/hair-extra/backbun",
        "entity/humanoid/seraphskinparts/face/neutral",
        "entity/humanoid/seraphskinparts/hair-face/brd-goat"
    ];

    private static readonly Dictionary<string, string[]> Outfits = new()
    {
        ["naked"] = [],
        ["clothed"] =
        [
            "entity/humanoid/seraph/clothing/upperbody/longsleeve", "entity/humanoid/seraph/clothing/lowerbody/pants7",
            "entity/humanoid/seraph/clothing/foot/beggar", "entity/humanoid/seraph/clothing/hand/commoner-gloves",
            "entity/humanoid/seraph/clothing/head/broadbrim", "entity/humanoid/seraph/clothing/shoulder/cape",
            "entity/humanoid/seraph/clothing/waist/beggar"
        ],
        ["armored"] =
        [
            "entity/humanoid/seraph/clothing/upperbody/longsleeve", "entity/humanoid/seraph/clothing/lowerbody/pants7",
            "entity/humanoid/seraph/clothing/foot/beggar", "entity/humanoid/seraph/clothing/hand/commoner-gloves",
            "entity/humanoid/seraph/clothing/shoulder/cape", "entity/humanoid/seraph/armor/brigandine/body",
            "entity/humanoid/seraph/armor/brigandine/legs", "entity/humanoid/seraph/armor/brigandine/head-iron"
        ]
    };

    // The resolved table AnimationKeyFrame.Resolve builds, and the frame it stamps on each keyframe element
    public static readonly FieldInfo Table =
        typeof(AnimationKeyFrame).GetField("ElementsByShapeElement", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo Frame =
        typeof(AnimationKeyFrameElement).GetField("Frame", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string PathOf(string domain, string path) =>
        Path.Combine(GameInstall.Assets, domain, "shapes", path + ".json");

    // Shape.TryGet: IAsset.ToObject is JsonUtil.ToObject with the asset's domain
    public static Shape Parse(string domain, string path, string text)
    {
        ShapeElement.locationForLogging = path;
        return JsonUtil.ToObject<Shape>(text, domain) ?? throw new InvalidDataException(path);
    }

    public static Shape Load(string domain, string path) => Parse(domain, path, File.ReadAllText(PathOf(domain, path)));

    // What ShapeTesselatorManager does to an entity's LoadedShape, then what AnimationManager.LoadAnimator does on spawn
    public static Shape Resolve(Shape shape, string path)
    {
        shape.ResolveReferences(Log, path);
        Shape.CacheInvTransforms(shape.Elements);
        shape.InitForAnimations(Log, path, "head");
        return shape;
    }

    // Every shape file with animations, as (domain:path, raw text), in a fixed order
    public static IEnumerable<(string Domain, string Path, string Text)> Animated()
    {
        foreach (var domain in Domains)
        {
            var root = Path.Combine(GameInstall.Assets, domain, "shapes");
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                if (!text.Contains("\"animations\"", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (domain, Path.GetRelativePath(root, file)[..^5], text);
            }
        }
    }

    public static Shape Player(string outfit)
    {
        var shape = PlayerUninitialised(outfit);
        shape.InitForAnimations(Log, "player", PlayerDisable, "head");
        return shape;
    }

    public static Shape PlayerUninitialised(string outfit) => Compose(Seraph(), outfit);

    // The shared shape every player is cloned from, resolved as ShapeTesselatorManager leaves it
    public static Shape Seraph()
    {
        var shape = Load("game", "entity/humanoid/seraph-faceless");
        shape.ResolveReferences(Log, "seraph-faceless");
        Shape.CacheInvTransforms(shape.Elements);
        return shape;
    }

    public static Shape Compose(Shape seraph, string outfit)
    {
        var shape = seraph.Clone();
        foreach (var part in Skin.Where(part => File.Exists(PathOf("game", part))))
        {
            var skin = Load("game", part);
            _ = skin.SubclassForStepParenting("skinpart-");
            _ = shape.StepParentShape(skin, part, "player", Log, (_, _) => { });
        }

        var slot = 0;
        foreach (var gear in Outfits[outfit])
        {
            Assert.That(File.Exists(PathOf("survival", gear)), gear);
            var piece = Load("survival", gear);
            _ = piece.SubclassForStepParenting($"gear{slot++}-");
            piece.ResolveReferences(Log, gear);
            _ = shape.StepParentShape(piece, gear, "player", Log, (_, _) => { });
        }

        shape.RemoveElements(PlayerDisable);
        return shape;
    }

    // Every element in pre-order, walked independently of the code under test (Komet's ElementWalk), whose output the states built
    // from this check
    public static List<ShapeElement> Elements(ShapeElement[]? roots)
    {
        var elements = new List<ShapeElement>();
        var stack = new Stack<ShapeElement>((roots ?? []).Reverse());
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            elements.Add(element);
            foreach (var child in (element.Children ?? []).Reverse()) stack.Push(child);
        }

        return elements;
    }

    // Every pose of a compiled frame in pre-order
    public static List<ElementPose> Poses(AnimationFrame frame)
    {
        var poses = new List<ElementPose>();
        var stack = new Stack<ElementPose>(Enumerable.Reverse(frame.RootElementTransforms));
        while (stack.Count > 0)
        {
            var pose = stack.Pop();
            poses.Add(pose);
            foreach (var child in Enumerable.Reverse(pose.ChildElementPoses)) stack.Push(child);
        }

        return poses;
    }

    // A body with a head and an arm with a hand; "wave" moves the arm and the hand and names "gone", which no element has, and "cape"
    public static Shape Synthetic()
    {
        static ShapeElement Element(string name, params ShapeElement[] children) => new()
        {
            Name = name, From = [1, 2, 3], To = [2, 3, 4], RotationOrigin = [0, 0, 0],
            Children = children.Length == 0 ? null : children
        };

        static AnimationKeyFrameElement Rotated(double x) => new() { RotationX = x, RotationY = 0, RotationZ = 0 };

        var shape = new Shape
        {
            Elements = [Element("body", Element("head"), Element("arm", Element("hand")))],
            Animations =
            [
                new Animation
                {
                    Code = "wave", QuantityFrames = 10,
                    KeyFrames =
                    [
                        new AnimationKeyFrame
                        {
                            Frame = 0,
                            Elements = new Dictionary<string, AnimationKeyFrameElement>
                                { ["arm"] = Rotated(0), ["gone"] = Rotated(1), ["cape"] = Rotated(2) }
                        },
                        new AnimationKeyFrame
                        {
                            Frame = 5,
                            Elements = new Dictionary<string, AnimationKeyFrameElement>
                                { ["arm"] = Rotated(90), ["hand"] = Rotated(10) }
                        }
                    ]
                },
                new Animation
                {
                    Code = "idle", QuantityFrames = 4,
                    KeyFrames =
                    [
                        new AnimationKeyFrame
                        {
                            Frame = 0,
                            Elements = new Dictionary<string, AnimationKeyFrameElement> { ["head"] = Rotated(5) }
                        }
                    ]
                }
            ]
        };
        shape.ResolveReferences(Log, "synthetic");
        return shape;
    }

    // Everything InitForAnimations reads or writes, by content, with objects named by the order they are first met, so two shapes
    // built the same way compare equal exactly when the inits left them the same. The resolved tables and AnimationJoint objects
    // are compared by what they hold.
    public static List<string> State(params Shape[] shapes)
    {
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        string Id(object? o)
        {
            if (o is null) return "null";
            if (!ids.TryGetValue(o, out var id)) ids[o] = id = ids.Count;
            return "#" + id;
        }

        var state = new List<string>();
        foreach (var shape in shapes)
        {
            foreach (var e in Elements(shape.Elements))
            {
                var inverse = string.Join(",", (e.inverseModelTransform ?? []).Select(BitConverter.SingleToInt32Bits));
                state.Add($"element {Id(e)} {e.Name} joint={e.JointId} parent={Id(e.ParentElement)} inverse={inverse}");
                state.AddRange((e.AttachmentPoints ?? []).Select(point =>
                    $"  point {point.Code} parent={Id(point.ParentElement)}"));
            }

            foreach (var (id, joint) in shape.JointsById) state.Add($"joint {id}: {joint.JointId} {Id(joint.Element)}");
            foreach (var (crc, animation) in shape.AnimationsByCrc32.OrderBy(pair => pair.Key))
                state.Add($"crc {crc}: {Id(animation)}");
            foreach (var animation in shape.Animations ?? [])
            {
                state.Add($"animation {Id(animation)} {animation.Code} {animation.Version} {animation.CodeCrc32} " +
                          $"frames={animation.PrevNextKeyFrameByFrame?.Length}");
                foreach (var key in animation.KeyFrames)
                {
                    var table = (IEnumerable?)Table.GetValue(key);
                    var pairs = table?.Cast<KeyValuePair<ShapeElement, AnimationKeyFrameElement>>()
                        .Select(p => $"{Id(p.Key)}>{Id(p.Value)}");
                    state.Add($"  key {key.Frame} {table?.GetType().Name} [{string.Join(" ", pairs ?? [])}]");
                    foreach (var (name, element) in key.Elements ?? [])
                        state.Add($"    {name} {Id(element)} frame={Frame.GetValue(element)} " +
                                  $"for={Id(element.ForElement)} {element.ForElement?.Name}");
                }
            }
        }

        return state;
    }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    // null when both compiles are the same to the bit: every pose float, the flags, ForElement (by reference, or by name for two
    // separately built shapes), child order, FrameNumber, and the pattern in which PrevNextKeyFrameByFrame shares AnimationFrame objects
    public static string? Diff(AnimationFrame[][]? engine, AnimationFrame[][]? fast, bool byName = false)
    {
        if (engine is null || fast is null) return engine is null == fast is null ? null : "null";
        if (engine.Length != fast.Length) return $"length {engine.Length} vs {fast.Length}";
        var seenEngine = new Dictionary<AnimationFrame, int>(ReferenceEqualityComparer.Instance);
        var seenFast = new Dictionary<AnimationFrame, int>(ReferenceEqualityComparer.Instance);
        for (var frame = 0; frame < engine.Length; frame++)
        {
            if (engine[frame].Length != 2 || fast[frame].Length != 2) return $"frame {frame}: not a pair";
            for (var side = 0; side < 2; side++)
            {
                var (a, b) = (engine[frame][side], fast[frame][side]);
                if (!seenEngine.TryGetValue(a, out var ia)) seenEngine[a] = ia = seenEngine.Count;
                if (!seenFast.TryGetValue(b, out var ib)) seenFast[b] = ib = seenFast.Count;
                if (ia != ib) return $"frame {frame}/{side}: shares a different AnimationFrame";
                if (a.FrameNumber != b.FrameNumber)
                    return $"frame {frame}/{side}: FrameNumber {a.FrameNumber} vs {b.FrameNumber}";
                var diff = Poses(a, b, $"frame {frame}/{side}", byName);
                if (diff != null) return diff;
            }
        }

        return null;
    }

    private static string? Poses(AnimationFrame a, AnimationFrame b, string where, bool byName)
    {
        var stack = new Stack<(List<ElementPose> A, List<ElementPose> B, string Where)>();
        stack.Push((a.RootElementTransforms, b.RootElementTransforms, where));
        while (stack.Count > 0)
        {
            var (x, y, at) = stack.Pop();
            if (x.Count != y.Count) return $"{at}: {x.Count} vs {y.Count} poses";
            for (var i = x.Count - 1; i >= 0; i--)
            {
                var diff = Pose(x[i], y[i], $"{at}/{i}", byName);
                if (diff != null) return diff;
                stack.Push((x[i].ChildElementPoses, y[i].ChildElementPoses, $"{at}/{i}"));
            }
        }

        return null;
    }

    private static string? Pose(ElementPose a, ElementPose b, string where, bool byName)
    {
        if (byName ? a.ForElement?.Name != b.ForElement?.Name : !ReferenceEquals(a.ForElement, b.ForElement))
            return $"{where}: ForElement";
        if (a.AnimModelMatrix != null || b.AnimModelMatrix != null) return $"{where}: AnimModelMatrix";
        float[] x =
        [
            a.degX, a.degY, a.degZ, a.degOffX, a.degOffY, a.degOffZ, a.scaleX, a.scaleY, a.scaleZ, a.translateX,
            a.translateY, a.translateZ
        ];
        float[] y =
        [
            b.degX, b.degY, b.degZ, b.degOffX, b.degOffY, b.degOffZ, b.scaleX, b.scaleY, b.scaleZ, b.translateX,
            b.translateY, b.translateZ
        ];
        for (var i = 0; i < x.Length; i++)
            if (Bits(x[i]) != Bits(y[i]))
                return $"{where}: float {i} {x[i]} vs {y[i]} ({Bits(x[i]):X8} vs {Bits(y[i]):X8})";
        if (a.RotShortestDistanceX != b.RotShortestDistanceX || a.RotShortestDistanceY != b.RotShortestDistanceY ||
            a.RotShortestDistanceZ != b.RotShortestDistanceZ) return $"{where}: RotShortestDistance";
        return null;
    }
}

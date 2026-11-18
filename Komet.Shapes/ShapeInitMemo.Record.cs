using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Datastructures;
using ResolvedTable = Vintagestory.API.Datastructures.FastSmallDictionary<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;
using ResolvedArrays = Komet.Shapes.ShapeInitMemo.Arrays<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;

namespace Komet.Shapes;

// ShapeInitMemo: restoring and recording a memo, and its record types.
internal static partial class ShapeInitMemo
{
    // What the engine rewrites: ForElement and Frame in its order, attachment parents, then the lazy inverse transforms
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Restore(Memo memo, Shape shape)
    {
        foreach (var point in memo.Points.Bounded(MaxPoints))
        {
            point.Item.ParentElement = point.Parent;
        }

        var keys = CollectionsMarshal.AsSpan(memo.Keys);
        var entries = CollectionsMarshal.AsSpan(memo.Entries);
        for (var k = 0; k < Math.Min(keys.Length, MaxKeys); k++)
        {
            var frame = keys[k].Item.Frame;
            var end = k + 1 < keys.Length ? keys[k + 1].FirstEntry : entries.Length;
            for (var e = keys[k].FirstEntry; e < Math.Min(end, MaxEntries); e++)
            {
                ref readonly var entry = ref entries[e];
                if (entry.Element is not null) entry.Value.ForElement = entry.Element;

                AnimationFrames.At(entry.Value) = frame;
            }
        }

        shape.CacheInvTransforms();
    }

    // After a completed init: everything Same compares; false where the memo cannot answer. Runs about once per type,
    // so [AggressiveOptimization] (warmed by WarmUp) with lists sized up front, off the LOH.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Record(Memo memo, Shape shape, string[]? joints)
    {
        memo.Clear();
        if (shape.Elements is not { } roots || shape.Animations is not { } animations || joints is null) return false;

        if (shape.JointsById is not { } byId || shape.AnimationsByCrc32 is not { } byCrc) return false;

        if (joints.Length > MaxNames || animations.Length > MaxAnimations) return false;

        if (!Reserve(memo, animations)) return false;

        var names = new Dictionary<string, ShapeElement>(StringComparer.Ordinal);
        if (!Walk(roots, memo, Mode.Record, names)) return false;

        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        for (var a = 0; a < Math.Min(animations.Length, MaxAnimations); a++)
        {
            if (!KeepAnimation(memo, animations[a], byCrc, names, unresolved)) return false;
        }

        if (unresolved.Count > MaxNames || !KeepJoints(memo, byId)) return false;

        (memo.Roots, memo.Animations, memo.JointTable, memo.ByCrc, memo.ByCrcCount) =
            (roots, animations, byId, byCrc, byCrc.Count);
        (memo.Required, memo.Unresolved) = ((string[])joints.Clone(), [.. unresolved]);
        return true;
    }

    private static bool Reserve(Memo memo, Animation?[] animations)
    {
        var (keys, entries) = (0, 0);
        for (var a = 0; a < Math.Min(animations.Length, MaxAnimations); a++)
        {
            if (animations[a]?.KeyFrames is not { } frames || frames.Length > MaxKeys - keys) return false;

            keys += frames.Length;
            for (var k = 0; k < Math.Min(frames.Length, MaxKeys); k++)
            {
                entries += frames[k]?.Elements?.Count ?? 0;
                if (entries > MaxEntries) return false;
            }
        }

        _ = memo.Anims.EnsureCapacity(animations.Length);
        _ = memo.Keys.EnsureCapacity(keys);
        _ = memo.Entries.EnsureCapacity(entries);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepAnimation(Memo memo, Animation? animation, Dictionary<uint, Animation?> byCrc,
        Dictionary<string, ShapeElement> names, HashSet<string> unresolved)
    {
        if (animation?.KeyFrames is not { } keys || string.IsNullOrEmpty(animation.Code)) return false;

        var crc = AnimationMetaData.GetCrc32(animation.Code);
        if (!byCrc.TryGetValue(crc, out var winner) || winner is null ||
            memo.Keys.Count + keys.Length > MaxKeys)
        {
            return false;
        }

        memo.Anims.Add(new Anim(animation, animation.Code, animation.Version, crc, winner, keys, memo.Keys.Count));
        foreach (var key in keys.Bounded(MaxKeys))
        {
            if (!KeepKey(memo, key, names, unresolved)) return false;
        }

        return true;
    }

    // The resolved entries, kept only where the engine's table holds exactly them in order and the init left the memo's
    // ForElement and Frame on each (not with shared entries or a later resolve patch). An entry KeyframeShare hands out
    // sits under several names: its ForElement is the last name's, as Restore writes them in the engine's order.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepKey(Memo memo, AnimationKeyFrame? key, Dictionary<string, ShapeElement> names,
        HashSet<string> unresolved)
    {
        if (key?.Elements is not { } table || AnimationFrames.Resolved(key) is not ResolvedTable fast ||
            fast.GetType() != typeof(ResolvedTable))
        {
            return false;
        }

        if (table.Count > MaxPerKey || memo.Entries.Count + table.Count > MaxEntries) return false;

        var (first, resolved) = (memo.Entries.Count, 0);
        var (elements, values) = (ResolvedArrays.Keys(fast), ResolvedArrays.Values(fast));
        if (elements is null || values is null) return false;

        using var entries = table.GetEnumerator();
        for (var i = 0; i < MaxPerKey && entries.MoveNext(); i++)
        {
            var (name, entry) = entries.Current;
            if (name is null || entry is null || AnimationFrames.At(entry) != key.Frame) return false;

            var element = names.GetValueOrDefault(name);
            if (element is not null && !ReferenceEquals(entry.ForElement, element) && !KeyframeShare.Holds(entry))
            {
                return false;
            }

            memo.Entries.Add(new Entry(name, entry, element));
            if (element is null)
            {
                if (unresolved.Count <= MaxNames) _ = unresolved.Add(name);

                continue;
            }

            if (resolved >= fast.Count || !ReferenceEquals(elements[resolved], element) ||
                !ReferenceEquals(values[resolved], entry))
            {
                return false;
            }

            resolved++;
        }

        memo.Keys.Add(new Key(key, table, fast, resolved, first));
        return resolved == fast.Count;
    }

    // Every joint's element must be in the tree: SetJointId writes down from each
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepJoints(Memo memo, Dictionary<int, AnimationJoint> table)
    {
        if (table.Count > MaxJoints) return false;

        var tree = new HashSet<ShapeElement>(ReferenceEqualityComparer.Instance);
        foreach (var node in memo.Nodes.Bounded(ElementWalk.MaxElements))
        {
            _ = tree.Add(node.Element);
        }

        using var joints = table.GetEnumerator();
        for (var i = 0; i < MaxJoints && joints.MoveNext(); i++)
        {
            var (id, joint) = joints.Current;
            if (joint?.Element is null || !tree.Contains(joint.Element)) return false;

            memo.Joints.Add(new Joint(id, joint, joint.JointId, joint.Element));
        }

        return memo.Joints.Count == table.Count;
    }

    private enum Mode
    {
        Record, // after the engine's init: remember
        Check // before an init: is it the one remembered
    }

    private readonly record struct Node(
        ShapeElement Element,
        int Depth,
        string Name,
        ShapeElement? Parent,
        int JointId,
        AttachmentPoint[]? Points);

    private readonly record struct Point(AttachmentPoint Item, ShapeElement? Parent);

    private readonly record struct Anim(
        Animation Item,
        string Code,
        int Version,
        uint Crc,
        Animation Winner,
        AnimationKeyFrame[] Keys,
        int FirstKey);

    private readonly record struct Key(
        AnimationKeyFrame Item,
        Dictionary<string, AnimationKeyFrameElement> Table,
        ResolvedTable Fast,
        int ResolvedCount,
        int FirstEntry);

    private readonly record struct Entry(string Name, AnimationKeyFrameElement Value, ShapeElement? Element);

    private readonly record struct Joint(int Id, AnimationJoint Item, int JointId, ShapeElement Element);

    // An accessor into a generic type is declared on the open definition
    internal static class Arrays<TKey, TValue>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "keys")]
        public static extern ref TKey[]? Keys(FastSmallDictionary<TKey, TValue> table);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "values")]
        public static extern ref TValue[]? Values(FastSmallDictionary<TKey, TValue> table);
    }

    // One shape's last completed init; Inits counts to Repeats, so a shape is remembered from its second
    private sealed class Memo
    {
        public readonly List<Anim> Anims = [];
        public readonly List<Entry> Entries = [];
        public readonly List<Joint> Joints = [];
        public readonly List<Key> Keys = [];
        public readonly List<Node> Nodes = [];
        public readonly List<Point> Points = [];
        public Animation[]? Animations;
        public Dictionary<uint, Animation>? ByCrc;
        public int ByCrcCount, Inits;
        public Dictionary<int, AnimationJoint>? JointTable;
        public string[] Required = [], Unresolved = [];
        public ShapeElement[]? Roots;
        public bool Valid;

        public void Clear()
        {
            Nodes.Clear();
            Points.Clear();
            Anims.Clear();
            Keys.Clear();
            Entries.Clear();
            Joints.Clear();
            (Roots, Animations, JointTable, ByCrc, ByCrcCount) = (null, null, null, null, 0);
            (Required, Unresolved, Valid) = ([], [], false);
        }
    }
}

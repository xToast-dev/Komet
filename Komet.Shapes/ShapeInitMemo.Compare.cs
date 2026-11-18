using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResolvedTable = Vintagestory.API.Datastructures.FastSmallDictionary<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;
using ResolvedArrays = Komet.Shapes.ShapeInitMemo.Arrays<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;

namespace Komet.Shapes;

// ShapeInitMemo: walking a shape and comparing it with its memo.
internal static partial class ShapeInitMemo
{
    // The tree in CollectElements order: recording appends each element, checking compares it at the same place
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Walk(ShapeElement[] roots, Memo memo, Mode mode, Dictionary<string, ShapeElement>? names)
    {
        var walk = new ElementWalk(roots);
        var points = 0;
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element))
            {
                return !walk.Failed && (mode == Mode.Record || (n == memo.Nodes.Count && points == memo.Points.Count));
            }

            if (element?.Name is null)
            {
                return false; // the engine throws on either
            }

            if (mode == Mode.Record
                    ? !Keep(memo, element, walk.Depth, names)
                    : !SameNode(memo, n, element, walk.Depth, ref points))
            {
                return false;
            }
        }

        return false; // Next stops after MaxElements
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Keep(Memo memo, ShapeElement element, int depth, Dictionary<string, ShapeElement>? names)
    {
        if (!NotNull(names) || !NotNull(element.Name)) return false;

        if (depth == 0 && element.ParentElement is not null)
        {
            return false; // CountParents would walk out of the tree
        }

        names[element.Name] = element; // CollectElements: the last element of a name wins
        memo.Nodes.Add(new Node(element, depth, element.Name, element.ParentElement, element.JointId,
            element.AttachmentPoints));
        AttachmentPoint?[]? points = element.AttachmentPoints;
        if (points is null) return true;

        if (memo.Points.Count + points.Length > MaxPoints) return false;

        foreach (var point in points.Bounded(MaxPoints))
        {
            if (point is null)
            {
                return false; // the engine throws on it
            }

            memo.Points.Add(new Point(point, point.ParentElement));
        }

        return true;
    }

    // ParentElement is written back, not compared: Clone shares the points, and the clone's resolve repoints them
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameNode(Memo memo, int n, ShapeElement element, int depth, ref int points)
    {
        if (n >= memo.Nodes.Count)
        {
            return false; // a grown tree
        }

        var node = memo.Nodes[n];
        if (!ReferenceEquals(element, node.Element) || depth != node.Depth || element.JointId != node.JointId ||
            !ReferenceEquals(element.ParentElement, node.Parent) ||
            !string.Equals(element.Name, node.Name, StringComparison.Ordinal) ||
            !ReferenceEquals(element.AttachmentPoints, node.Points))
        {
            return false;
        }

        if (node.Points is null) return true;

        if (!Assert(points + node.Points.Length <= memo.Points.Count)) return false;

        for (var p = 0; p < Math.Min(node.Points.Length, MaxPoints); p++, points++)
        {
            if (!ReferenceEquals(node.Points[p], memo.Points[points].Item)) return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameAnimations(Memo memo, Shape shape)
    {
        var anims = CollectionsMarshal.AsSpan(memo.Anims);
        var (live, byCrc) = (shape.Animations, shape.AnimationsByCrc32);
        if (live is null || !ReferenceEquals(live, memo.Animations) || live.Length != anims.Length) return false;

        if (byCrc is null || !ReferenceEquals(byCrc, memo.ByCrc) || byCrc.Count != memo.ByCrcCount) return false;

        for (var a = 0; a < Math.Min(anims.Length, MaxAnimations); a++)
        {
            ref readonly var anim = ref anims[a];
            var animation = live[a];
            if (!ReferenceEquals(animation, anim.Item) ||
                !string.Equals(animation.Code, anim.Code, StringComparison.Ordinal) ||
                animation.Version != anim.Version || !ReferenceEquals(animation.KeyFrames, anim.Keys))
            {
                return false;
            }

            if (!byCrc.TryGetValue(anim.Crc, out var winner) || !ReferenceEquals(winner, anim.Winner)) return false;

            if (!SameKeyArray(memo, anim)) return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameKeyArray(Memo memo, in Anim anim)
    {
        if (anim.Keys is null || !Assert(anim.FirstKey + anim.Keys.Length <= memo.Keys.Count)) return false;

        for (var k = 0; k < Math.Min(anim.Keys.Length, MaxKeys); k++)
        {
            if (!ReferenceEquals(anim.Keys[k], memo.Keys[anim.FirstKey + k].Item)) return false;
        }

        return true;
    }

    // Every keyframe's dictionary entry by entry in iteration order, and the table the engine resolved from it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameKeys(Memo memo)
    {
        var keys = CollectionsMarshal.AsSpan(memo.Keys);
        var entries = CollectionsMarshal.AsSpan(memo.Entries);
        for (var k = 0; k < Math.Min(keys.Length, MaxKeys); k++)
        {
            var end = k + 1 < keys.Length ? keys[k + 1].FirstEntry : entries.Length;
            if (!Assert(keys[k].FirstEntry <= end) || !SameKey(keys[k], entries[keys[k].FirstEntry..end])) return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameKey(in Key key, ReadOnlySpan<Entry> entries)
    {
        var table = key.Item.Elements;
        if (!ReferenceEquals(table, key.Table) || table is null || table.Count != entries.Length) return false;

        var resolved = AnimationFrames.Resolved(key.Item);
        if (!ReferenceEquals(resolved, key.Fast)) return false;

        if (resolved is not ResolvedTable fast || fast.Count != key.ResolvedCount) return false;

        var (elements, values) = (ResolvedArrays.Keys(fast), ResolvedArrays.Values(fast));
        if (elements is null || values is null || elements.Length < key.ResolvedCount ||
            values.Length < key.ResolvedCount)
        {
            return false;
        }

        using var live = table.GetEnumerator();
        var j = 0;
        for (var i = 0; i < Math.Min(entries.Length, MaxPerKey); i++)
        {
            if (!live.MoveNext()) return false;

            ref readonly var entry = ref entries[i];
            if (!ReferenceEquals(live.Current.Value, entry.Value) ||
                !string.Equals(live.Current.Key, entry.Name, StringComparison.Ordinal))
            {
                return false;
            }

            if (entry.Element is null) continue;

            if (j >= key.ResolvedCount || !ReferenceEquals(elements[j], entry.Element) ||
                !ReferenceEquals(values[j], entry.Value))
            {
                return false;
            }

            j++;
        }

        return j == key.ResolvedCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameJoints(Memo memo, Dictionary<int, AnimationJoint>? table)
    {
        var joints = CollectionsMarshal.AsSpan(memo.Joints);
        if (!ReferenceEquals(table, memo.JointTable) || table is null || table.Count != joints.Length) return false;

        using var live = table.GetEnumerator();
        for (var i = 0; i < Math.Min(joints.Length, MaxJoints); i++)
        {
            if (!live.MoveNext()) return false;

            var (id, joint) = live.Current;
            ref readonly var expected = ref joints[i];
            if (id != expected.Id || joint is null || !ReferenceEquals(joint, expected.Item)) return false;

            if (joint.JointId != expected.JointId || !ReferenceEquals(joint.Element, expected.Element)) return false;
        }

        return true;
    }
}

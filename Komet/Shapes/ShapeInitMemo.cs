using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using ResolvedTable = Vintagestory.API.Datastructures.FastSmallDictionary<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;

namespace Komet.Shapes;

// Every animal without gear re-runs Shape.InitForAnimations on the shape its whole entity type shares, on every tesselation:
// Entity.OnTesselation hands an uncloned shape to AnimManager.LoadAnimatorCached, and AnimationCache.InitManager inits it before it
// looks the type up in its cache. That init allocates a FastSmallDictionary per keyframe and new AnimationJoint objects and zeroes
// every JointId before setting it again, all inside esr-tesseleateshape, once per animal.
//
// For the same shape object, the same arguments and an unchanged tree the init is idempotent, so a completed init is remembered per
// Shape (main thread only) and compared by reference. A skip still writes what the engine would write again, because Shape.Clone
// shares these objects with every clone and the clone's init points them at its own elements: ForElement and Frame of every keyframe
// entry, ParentElement of every attachment point (ShapeElement.Clone copies the array, not the points), and CacheInvTransforms.
//
// What stays different: the remembered resolved tables and AnimationJoint objects stay in place where the engine would allocate equal
// new ones, and warnings the engine logs on every init of a flawed shape are logged by the first init only. The skip also closes an
// engine race: the pool thread building another entity's mesh reads JointId from the shared shape while the init has it zeroed.
// This class owns the one InitForAnimations patch, and asks InitOnce's window about the local player's duplicate init first.
internal static class ShapeInitMemo
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xF2EEA794EC8A99FAUL;
    private const int MaxAnimations = 4096, MaxKeys = 1 << 16, MaxEntries = 1 << 18, MaxPerKey = 4096, MaxJoints = 4096;
    private const int MaxPoints = 4096, MaxNames = 256, SeamCount = 11, InsideCount = 9, Repeats = 2, MaxSeen = 64;
    private static readonly ConditionalWeakTable<Shape, Memo> Memos = [];
    private static readonly int[] Seen = new int[MaxSeen]; // identity hashes of shapes inited once, most of them clones
    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _installed, _foreign;
    private static int _seen;

    public static bool Enabled { get; set; } = true;

    // Another mod patches the init or something it calls, or the engine's is not the one verified: every init runs
    public static bool Blocked { get; private set; }

    public static bool Matched => _shaped; // the engine's bodies are the ones verified

    // Inits on the main thread answered from the memo, a total while Counting.Hud
    public static long Skipped { get; private set; }

    private static (ShapeElement[] Keys, AnimationKeyFrameElement[] Values) ArraysOf(ResolvedTable table) =>
        (Arrays<ShapeElement, AnimationKeyFrameElement>.Keys(table),
            Arrays<ShapeElement, AnimationKeyFrameElement>.Values(table));

    // The patch goes on whatever the memo decides, since InitOnce's skip rides on it
    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_installed, _shaped, Blocked, _seen, _seams) = (false, false, true, 0, Seams());
        (_logger, _foreign) = (logger, false);
        Memos.Clear();
        Array.Clear(Seen);
        if (!NotNull(harmony) || !Assert(_seams.Length == SeamCount)) return;
        _shaped = EngineShape.Matches(Shaped(), fingerprint, nameof(ShapeInitMemo), logger);
        var warm = _shaped && WarmUp(); // before the patch: the engine's init, never remembered
        if (_seams[0] is { } init) _ = NotNull(harmony.Patch(init, new HarmonyMethod(Check), new HarmonyMethod(Done)));
        Recheck();
        _installed = warm;
    }

    // Harmony patches are process wide: KometModSystem asks again on LevelFinalize, when every mod has started
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == SeamCount);
        _foreign = EngineShape.Report(_logger, nameof(ShapeInitMemo), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.All, null, typeof(ShapeInitMemo)));
        Blocked = !seamed || _foreign;
    }

    // The checks are [AggressiveOptimization]: the engine assemblies and Komet carry no ReadyToRun code. Their JIT happens here, at
    // mod load, not in the first frame a herd comes into view. A shape of its own, never in the table.
    private static bool WarmUp()
    {
        var (parent, child) = AnimationFrames.WarmTree();
        var key = new AnimationKeyFrame
        { Frame = 0, Elements = new Dictionary<string, AnimationKeyFrameElement> { ["komet-child"] = new() } };
        var animation = new Animation { Code = "komet-warmup", QuantityFrames = 2, KeyFrames = [key] };
        var shape = new Shape { Elements = [parent], Animations = [animation] };
        string[] joints = ["komet-parent"];
        shape.ResolveReferences(null, "komet-warmup");
        shape.InitForAnimations(null, "komet-warmup", null, joints);
        var memo = new Memo { Inits = Repeats };
        var same = Record(memo, shape, joints) && Same(memo, shape, null, joints);
        if (same) Restore(memo, shape);
        return Assert(same) && Assert(child.JointId > 0);
    }

    // [0] is patched; [1] to [9] are Inside(); [10] decides between the cached and the uncached init. Every lookup names its
    // parameters, so an overload an engine update adds cannot make it ambiguous.
    private static MethodBase?[] Seams()
    {
        var shape = typeof(Shape);
        MethodBase?[] seams =
        [
            AccessTools.Method(shape, nameof(Shape.InitForAnimations),
                [typeof(ILogger), typeof(string), typeof(string[]), typeof(string[])]),
            .. Inside(),
            AccessTools.DeclaredMethod(typeof(AnimationCache), "InitManager",
            [
                typeof(ICoreAPI), typeof(IAnimationManager), typeof(Entity), shape, typeof(RunningAnimation[]),
                typeof(bool), typeof(string[]), typeof(string[])
            ])
        ];
        return Assert(seams.Length == SeamCount) ? seams : [];
    }

    // What runs inside InitForAnimations, which the memo's skip and InitOnce's leave out: another mod's patch there would miss that
    // call ([7], the private per-keyframe ResolveReferences, writes ForElement, which the memo's skip writes itself)
    internal static MethodBase?[] Inside()
    {
        var shape = typeof(Shape);
        MethodBase?[] inside =
        [
            AccessTools.DeclaredMethod(shape, nameof(Shape.CacheInvTransforms), []),
            AccessTools.DeclaredMethod(shape, nameof(Shape.CollectAndResolveReferences),
                [typeof(ILogger), typeof(string), typeof(string[])]),
            AccessTools.DeclaredMethod(shape, nameof(Shape.CollectElements),
            [
                typeof(ShapeElement[]), typeof(IDictionary<string, ShapeElement>), typeof(HashSet<string>),
                typeof(ILogger), typeof(string)
            ]),
            AccessTools.DeclaredMethod(shape, nameof(Shape.ResolveAndFindJoints),
                [typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(string[])]),
            AccessTools.DeclaredMethod(typeof(AnimationKeyFrame), nameof(AnimationKeyFrame.Resolve),
                [typeof(Dictionary<string, ShapeElement>)]),
            AccessTools.DeclaredMethod(typeof(ShapeElement), nameof(ShapeElement.ResolveReferences), []),
            AccessTools.DeclaredMethod(typeof(ShapeElement), nameof(ShapeElement.SetJointId), [typeof(int)]),
            AccessTools.DeclaredMethod(shape, nameof(Shape.ResolveReferences),
            [
                typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(AnimationKeyFrame),
                typeof(string[])
            ]),
            AccessTools.DeclaredMethod(shape, nameof(Shape.GetElementByName),
                [typeof(string), typeof(StringComparison)])
        ];
        return Assert(inside.Length == InsideCount) ? inside : [];
    }

    // The bodies the memo's proof rests on: the seams and the table insert AnimationKeyFrame.Resolve fills the resolved table with,
    // whose keys and values arrays the memo reads
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams(), AccessTools.PropertySetter(typeof(ResolvedTable), "Item")];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    private static bool Check(Shape __instance, string[]? disableElements, string[]? requireJointsForElements,
        out Memo? __state)
    {
        __state = null;
        if (InitOnce.Duplicate(__instance, disableElements, requireJointsForElements)) return false;
        if (!_installed || __instance is null ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return true;
        if (!Memos.TryGetValue(__instance, out var memo))
        {
            if (!Enabled || Blocked || !SeenBefore(__instance)) return true;
            memo = new Memo { Inits = Repeats - 1 };
            Memos.AddOrUpdate(__instance, memo);
        }

        var same = Enabled && !Blocked && memo.Valid &&
                   Same(memo, __instance, disableElements, requireJointsForElements);
        memo.Valid = false; // until the engine's init is seen to complete, or the skip below has put everything back
        if (!same)
        {
            __state = memo;
            return true;
        }

        Restore(memo, __instance);
        memo.Valid = Assert(memo.Inits >= Repeats);
        if (Counting.Hud) Skipped++;
        return false;
    }

    private static void Done(Shape __instance, string[]? disableElements, string[]? requireJointsForElements,
        Memo? __state, bool __runOriginal)
    {
        InitOnce.Completed(__instance, disableElements, requireJointsForElements);
        if (__state is not { } memo || !__runOriginal || !NotNull(__instance)) return;
        memo.Inits = Math.Min(memo.Inits + 1, Repeats);
        // Blocked: nothing will be skipped, so nothing is worth remembering
        if (Enabled && !Blocked && memo.Inits >= Repeats)
            memo.Valid = Record(memo, __instance, requireJointsForElements);
    }

    // A clone is inited once and thrown away, so a shape gets a memo only on its second init: the first leaves its identity hash
    // in a small ring, which costs no allocation and no GC handle per clone. A collision only records a shape that did not repeat.
    private static bool SeenBefore(Shape shape)
    {
        var hash = RuntimeHelpers.GetHashCode(shape);
        if (!Assert(Seen.Length == MaxSeen) || !Index(_seen, MaxSeen)) return false;
        for (var i = 0; i < MaxSeen; i++)
            if (Seen[i] == hash) return true;
        (Seen[_seen], _seen) = (hash, (_seen + 1) % MaxSeen);
        return false;
    }

    // Every read of the init and everything it writes, against the live objects
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Same(Memo memo, Shape shape, string[]? disable, string[]? joints)
    {
        if (!NotNull(memo) || !SameArguments(memo, disable, joints)) return false;
        if (memo.Roots is null || !ReferenceEquals(shape.Elements, memo.Roots)) return false;
        return Walk(memo.Roots, memo, Mode.Check, null) && SameAnimations(memo, shape) && SameKeys(memo) &&
               SameJoints(memo, shape.JointsById);
    }

    // The same joint list by content; a disable list only matters for keys that resolve to nothing: under null, or one that names
    // them, the engine leaves their ForElement alone, under any other it mints a new placeholder element for each
    private static bool SameArguments(Memo memo, string[]? disable, string[]? joints)
    {
        if (joints is null || !Assert(memo.Required.Length <= MaxNames) ||
            !joints.AsSpan().SequenceEqual(memo.Required)) return false;
        if (disable is null || memo.Unresolved.Length == 0) return true;
        if (!Assert(memo.Unresolved.Length <= MaxNames)) return false;
        for (var i = 0; i < Math.Min(memo.Unresolved.Length, MaxNames); i++)
            if (Array.IndexOf(disable, memo.Unresolved[i]) < 0) return false;
        return true;
    }

    // The tree in the order CollectElements visits it. Recording appends every element, checking compares it with the one remembered
    // at the same place.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Walk(ShapeElement[] roots, Memo memo, Mode mode, Dictionary<string, ShapeElement>? names)
    {
        if (!NotNull(roots) || !NotNull(memo)) return false;
        var walk = new ElementWalk(roots);
        var points = 0;
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element))
                return !walk.Failed && (mode == Mode.Record || (n == memo.Nodes.Count && points == memo.Points.Count));
            if (element?.Name is null) return false; // the engine throws on either
            if (mode == Mode.Record
                    ? !Keep(memo, element, walk.Depth, names)
                    : !SameNode(memo, n, element, walk.Depth, ref points))
                return false;
        }

        return !Assert(walk.Failed); // Next stops after MaxElements
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Keep(Memo memo, ShapeElement element, int depth, Dictionary<string, ShapeElement>? names)
    {
        if (!NotNull(names) || !NotNull(element.Name)) return false;
        if (depth == 0 && element.ParentElement is not null) return false; // CountParents would walk out of the tree
        names[element.Name] = element; // CollectElements: the last element of a name wins
        memo.Nodes.Add(new Node(element, depth, element.Name, element.ParentElement, element.JointId,
            element.AttachmentPoints));
        if (element.AttachmentPoints is not { } points) return true;
        if (memo.Points.Count + points.Length > MaxPoints) return false;
        foreach (var point in points.Bounded(MaxPoints))
        {
            if (point is null) return false; // the engine throws on it
            memo.Points.Add(new Point(point, point.ParentElement));
        }

        return true;
    }

    // An attachment point's ParentElement is written back rather than compared, like ForElement: ShapeElement.Clone copies the
    // AttachmentPoints array but not the points in it, and the clone's ResolveReferences points the shared ones at its own
    // elements.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameNode(Memo memo, int n, ShapeElement element, int depth, ref int points)
    {
        if (n >= memo.Nodes.Count || !Assert(n >= 0)) return false; // a grown tree
        var node = memo.Nodes[n];
        if (!ReferenceEquals(element, node.Element) || depth != node.Depth || element.JointId != node.JointId ||
            !ReferenceEquals(element.ParentElement, node.Parent) ||
            !string.Equals(element.Name, node.Name, StringComparison.Ordinal) ||
            !ReferenceEquals(element.AttachmentPoints, node.Points)) return false;
        if (node.Points is null) return true;
        if (!Assert(points + node.Points.Length <= memo.Points.Count)) return false;
        for (var p = 0; p < Math.Min(node.Points.Length, MaxPoints); p++, points++)
            if (!ReferenceEquals(node.Points[p], memo.Points[points].Item)) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameAnimations(Memo memo, Shape shape)
    {
        var anims = CollectionsMarshal.AsSpan(memo.Anims);
        var (live, byCrc) = (shape.Animations, shape.AnimationsByCrc32);
        if (live is null || !ReferenceEquals(live, memo.Animations) || live.Length != anims.Length) return false;
        if (byCrc is null || !ReferenceEquals(byCrc, memo.ByCrc) || byCrc.Count != memo.ByCrcCount) return false;
        if (!Assert(anims.Length <= MaxAnimations)) return false;
        for (var a = 0; a < Math.Min(anims.Length, MaxAnimations); a++)
        {
            ref readonly var anim = ref anims[a];
            var animation = live[a];
            if (!ReferenceEquals(animation, anim.Item) ||
                !string.Equals(animation.Code, anim.Code, StringComparison.Ordinal) ||
                animation.Version != anim.Version || !ReferenceEquals(animation.KeyFrames, anim.Keys)) return false;
            if (!byCrc.TryGetValue(anim.Crc, out var winner) || !ReferenceEquals(winner, anim.Winner)) return false;
            if (!SameKeyArray(memo, anim)) return false;
        }

        return true;
    }

    // An array keeps its length, not its content
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameKeyArray(Memo memo, in Anim anim)
    {
        if (anim.Keys is null || !Assert(anim.FirstKey + anim.Keys.Length <= memo.Keys.Count)) return false;
        for (var k = 0; k < Math.Min(anim.Keys.Length, MaxKeys); k++)
            if (!ReferenceEquals(anim.Keys[k], memo.Keys[anim.FirstKey + k].Item)) return false;
        return true;
    }

    // Every keyframe's dictionary entry by entry in iteration order, and the table the engine resolved from it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameKeys(Memo memo)
    {
        var keys = CollectionsMarshal.AsSpan(memo.Keys);
        var entries = CollectionsMarshal.AsSpan(memo.Entries);
        if (!Assert(keys.Length <= MaxKeys) || !Assert(entries.Length <= MaxEntries)) return false;
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
        var (elements, values) = ArraysOf(fast);
        if (elements is null || values is null || elements.Length < key.ResolvedCount ||
            values.Length < key.ResolvedCount) return false;

        using var live = table.GetEnumerator();
        var j = 0;
        for (var i = 0; i < Math.Min(entries.Length, MaxPerKey); i++)
        {
            if (!live.MoveNext()) return false;
            ref readonly var entry = ref entries[i];
            if (!ReferenceEquals(live.Current.Value, entry.Value) ||
                !string.Equals(live.Current.Key, entry.Name, StringComparison.Ordinal)) return false;
            if (entry.Element is null) continue;
            if (j >= key.ResolvedCount || !ReferenceEquals(elements[j], entry.Element) ||
                !ReferenceEquals(values[j], entry.Value)) return false;
            j++;
        }

        return Assert(entries.Length <= MaxPerKey) && j == key.ResolvedCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool SameJoints(Memo memo, Dictionary<int, AnimationJoint>? table)
    {
        var joints = CollectionsMarshal.AsSpan(memo.Joints);
        if (!ReferenceEquals(table, memo.JointTable) || table is null || table.Count != joints.Length) return false;
        if (!Assert(joints.Length <= MaxJoints)) return false;
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

    // What the engine writes again on an unchanged shape: ForElement of every resolved entry and Frame of every entry, in its
    // order, so an entry object two keyframes share ends as the engine leaves it; the attachment points' ParentElement as it left
    // them; then the lazy inverse transforms it starts with
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Restore(Memo memo, Shape shape)
    {
        if (!NotNull(memo) || !Assert(ReferenceEquals(shape.Elements, memo.Roots))) return; // Same has just said so
        foreach (var point in memo.Points.Bounded(MaxPoints)) point.Item.ParentElement = point.Parent;
        var keys = CollectionsMarshal.AsSpan(memo.Keys);
        var entries = CollectionsMarshal.AsSpan(memo.Entries);
        if (!Assert(keys.Length <= MaxKeys) || !Assert(entries.Length <= MaxEntries)) return;
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

    // After a completed init: everything Same compares. False when the shape is not one the memo can answer for. The disable list
    // that init had does not matter: what it did to keys without an element is in the entries, and SameArguments asks whether the
    // next init's list would leave them alone. Record runs about once per entity type, never often enough to tier up, so it is
    // [AggressiveOptimization] (compiled at install by WarmUp) and sizes its lists up front: a big animal's grown copies would land
    // on the large object heap.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Record(Memo memo, Shape shape, string[]? joints)
    {
        memo.Clear();
        if (shape.Elements is not { } roots || shape.Animations is not { } animations || joints is null) return false;
        if (shape.JointsById is not { } byId || shape.AnimationsByCrc32 is not { } byCrc) return false;
        if (joints.Length > MaxNames || animations.Length > MaxAnimations || !Assert(memo.Nodes.Count == 0))
            return false;
        if (!Reserve(memo, animations)) return false;
        var names = new Dictionary<string, ShapeElement>(StringComparer.Ordinal);
        if (!Walk(roots, memo, Mode.Record, names)) return false;
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        for (var a = 0; a < Math.Min(animations.Length, MaxAnimations); a++)
            if (!KeepAnimation(memo, animations[a], byCrc, names, unresolved)) return false;
        if (unresolved.Count > MaxNames || !KeepJoints(memo, byId)) return false;
        (memo.Roots, memo.Animations, memo.JointTable, memo.ByCrc, memo.ByCrcCount) =
            (roots, animations, byId, byCrc, byCrc.Count);
        (memo.Required, memo.Unresolved) = ((string[])joints.Clone(), [.. unresolved]);
        return Assert(memo.Anims.Count == animations.Length) && Assert(memo.Keys.Count <= MaxKeys);
    }

    // Room for every keyframe and entry at once; false for a shape too large to remember
    private static bool Reserve(Memo memo, Animation[] animations)
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
        return Assert(keys <= MaxKeys) && Assert(entries <= MaxEntries);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepAnimation(Memo memo, Animation? animation, Dictionary<uint, Animation> byCrc,
        Dictionary<string, ShapeElement> names, HashSet<string> unresolved)
    {
        if (animation?.KeyFrames is not { } keys || string.IsNullOrEmpty(animation.Code) || !NotNull(byCrc))
            return false;
        var crc = AnimationMetaData.GetCrc32(animation.Code);
        if (!byCrc.TryGetValue(crc, out var winner) || winner is null ||
            memo.Keys.Count + keys.Length > MaxKeys) return false;
        memo.Anims.Add(new Anim(animation, animation.Code, animation.Version, crc, winner, keys, memo.Keys.Count));
        foreach (var key in keys.Bounded(MaxKeys))
            if (!KeepKey(memo, key, names, unresolved)) return false;
        return Assert(memo.Anims.Count > 0);
    }

    // The entries as the engine resolved them, kept only where the memo knows what the engine does: its table holds exactly the
    // resolved ones in that order (not so for a mod's element type with its own Equals, say), and since the write-back resolves
    // ForElement by the memo's own name lookup, the init that has just completed left exactly that on every resolved entry and this
    // keyframe's Frame on every entry (not so for an entry object two keys share, or a patch on the per-keyframe resolve applied
    // after the recheck).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepKey(Memo memo, AnimationKeyFrame? key, Dictionary<string, ShapeElement> names,
        HashSet<string> unresolved)
    {
        if (key?.Elements is not { } table || AnimationFrames.Resolved(key) is not ResolvedTable fast ||
            fast.GetType() != typeof(ResolvedTable)) return false;

        if (table.Count > MaxPerKey || memo.Entries.Count + table.Count > MaxEntries || !NotNull(names)) return false;
        var (first, resolved) = (memo.Entries.Count, 0);
        var (elements, values) = ArraysOf(fast);
        using var entries = table.GetEnumerator();
        for (var i = 0; i < MaxPerKey && entries.MoveNext(); i++)
        {
            var (name, entry) = entries.Current;
            if (name is null || entry is null || AnimationFrames.At(entry) != key.Frame) return false;
            var element = names.GetValueOrDefault(name);
            if (element is not null && !ReferenceEquals(entry.ForElement, element)) return false;
            memo.Entries.Add(new Entry(name, entry, element));
            if (element is null)
            {
                if (unresolved.Count <= MaxNames) _ = unresolved.Add(name);
                continue;
            }

            if (resolved >= fast.Count || !ReferenceEquals(elements[resolved], element) ||
                !ReferenceEquals(values[resolved], entry)) return false;
            resolved++;
        }

        memo.Keys.Add(new Key(key, table, fast, resolved, first));
        return Assert(memo.Entries.Count - first == table.Count) && resolved == fast.Count;
    }

    // Every joint's element must be in the tree: the engine's SetJointId pass writes down from each of them
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool KeepJoints(Memo memo, Dictionary<int, AnimationJoint> table)
    {
        if (!NotNull(table) || table.Count > MaxJoints || !Assert(memo.Joints.Count == 0)) return false;
        var tree = new HashSet<ShapeElement>(ReferenceEqualityComparer.Instance);
        foreach (var node in memo.Nodes.Bounded(ElementWalk.MaxElements)) _ = tree.Add(node.Element);
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

    private readonly record struct Node(ShapeElement Element, int Depth, string Name, ShapeElement? Parent, int JointId,
        AttachmentPoint[]? Points);

    private readonly record struct Point(AttachmentPoint Item, ShapeElement? Parent);

    private readonly record struct Anim(Animation Item, string Code, int Version, uint Crc, Animation Winner,
        AnimationKeyFrame[] Keys, int FirstKey);

    private readonly record struct Key(AnimationKeyFrame Item, Dictionary<string, AnimationKeyFrameElement> Table,
        ResolvedTable Fast, int ResolvedCount, int FirstEntry);

    private readonly record struct Entry(string Name, AnimationKeyFrameElement Value, ShapeElement? Element);

    private readonly record struct Joint(int Id, AnimationJoint Item, int JointId, ShapeElement Element);

    // FastSmallDictionary's arrays: an accessor into a generic type is declared on the open definition
    private static class Arrays<TKey, TValue>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "keys")]
        public static extern ref TKey[] Keys(FastSmallDictionary<TKey, TValue> table);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "values")]
        public static extern ref TValue[] Values(FastSmallDictionary<TKey, TValue> table);
    }

    // One shape's last completed init; Inits counts them up to Repeats, so a shape is remembered from its second on
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
            _ = Assert(Nodes.Count == 0) && Assert(Inits >= 0);
        }
    }
}

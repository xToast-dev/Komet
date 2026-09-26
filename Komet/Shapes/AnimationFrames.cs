using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace Komet.Shapes;

// Animation.GenerateAllFrames compiles an animation into QuantityFrames pairs of pose trees, lazily: ClientAnimator.AnimNowActive and
// OnFrame call it whenever PrevNextKeyFrameByFrame is null, inside esr-afteranim on the render thread (and the singleplayer server's
// animators on its thread). Animation.Clone does not copy PrevNextKeyFrameByFrame and Entity.OnTesselation nulls it, so every
// re-tesselation of a geared entity compiles again: EntityBehaviorContainer.addGearToShape clones the shared shape. The engine's own
// AnimationCache keys on entity code and shape path and leaves cloned shapes out, because Shape.StepParentShape reparents gear
// elements and merges the gear's keyframes, after which that key no longer identifies the compile.
//
// So this cache keys on what the compile reads: the element names in the order GenerateFrame visits them with their depth, and the
// animation's code, QuantityFrames and keyframes. A shape whose compile also reads object identity (a keyframe element object in two
// places or stamped by another keyframe, an element the tree holds twice) or an element's own Equals (a subclass) is not described:
// it compiles uncached. The model matrix GenerateFrame threads through is never stored (AnimationFrame's SetTransform and
// FinalizeMatrices are [Obsolete] no-ops), JointId only feeds jointsDone, and ShapeElement.GetLocalTransformMatrix writes only its
// output argument, so geometry stays out of the key and two entities in the same outfit share one compiled set.
// Sharing needs no copy: nothing in the shipped assemblies writes into a compiled set (calculateMatrices reads the poses only as
// ElementPose.Add arguments, ForElement on a cached pose is dead because its readers walk RootPoses, AnimModelMatrix is never set).
// A hit compares the whole descriptor, not the hash, so no hash collision hands an animator the pose tree of another shape.
//
// A miss compiles with the kernel in AnimationFrames.Compile.cs; where that declines, the engine compiles and the postfix stores its
// result. Another mod's patch on a reproduced method, or a body that is not the 1.22.7 one, leaves every compile to the engine.
internal static partial class AnimationFrames
{
    internal const int MaxEntries = 512;
    private const int MaxKeyElements = 4096, MaxKeyFrames = 4096, MaxDescriptor = 1 << 22, MaxBucket = 64;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, List<Entry>> Cache = [];

    // What Describe met, under Gate
    private static readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);

    private static byte[] _buffer = new byte[4096];
    private static string[] _names = new string[64];
    private static int _at, _entries;
    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _installed, _foreign;

    public static bool Enabled { get; set; } = true;

    // Another mod patches the compile, or the engine's is not the one reproduced here: the engine compiles everything
    public static bool Blocked { get; private set; }

    public static bool Matched => _shaped; // the engine's bodies are the ones reproduced

    public static long Hits { get; private set; } // totals while Counting.Hud, under Gate
    public static long Misses { get; private set; }
    public static double WorstMs { get; private set; } // the slowest compile on the render thread since ResetPeaks
    public static string WorstCode { get; private set; } = "";

    public static void ResetPeaks()
    {
        _ = Assert(Hits >= 0 && Misses >= 0) && Assert(WorstMs >= 0);
        (WorstMs, WorstCode) = (0, "");
    }

    // A compiled set belongs to the shapes of one world, and the descriptor holds element names that world interned
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Seen.Clear();
            (_at, _entries, _names) = (0, 0, new string[64]);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jointsDone")]
    private static extern ref HashSet<int> JointsDone(Animation animation);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        Clear();
        ResetPeaks();
        (_installed, _shaped, Blocked, _seams, _logger, _foreign) = (false, false, true, Seams(), logger, false);
        var clone = AccessTools.Method(typeof(Animation), nameof(Animation.Clone), []);
        if (!NotNull(harmony) || !NotNull(clone) || !Assert(_seams.Length == SeamCount)) return;
        // JointsDone throws otherwise
        if (Assert(AccessTools.Field(typeof(Animation), "jointsDone")?.FieldType == typeof(HashSet<int>)))
            _ = NotNull(harmony.Patch(clone, postfix: new HarmonyMethod(Cloned)));
        _shaped = EngineShape.Matches(Shaped(), fingerprint, nameof(AnimationFrames), logger);
        // the compile's accessors name fields of those bodies: on a changed engine they could throw
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Generate), new HarmonyMethod(Generated)));
        Recheck();
        _installed = WarmUp();
    }

    // Seam 0 is patched here and only a rewritten body there counts: a prefix ordered before Komet's and every postfix still run, a
    // later prefix that Harmony lets skip the original (bool result, or an out/ref/reference argument) is skipped whenever Komet
    // answers, as the engine body is. Any patch on what it calls would be skipped too. Harmony patches are process wide, so
    // KometModSystem asks again on LevelFinalize.
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == SeamCount);
        var foreign = seamed && (EngineShape.Foreign(_seams.AsSpan(0, 1), EngineShape.Kinds.Body, null) ||
                                 EngineShape.Foreign(_seams.AsSpan(1), EngineShape.Kinds.All, null));
        _foreign = EngineShape.Report(_logger, nameof(AnimationFrames), _foreign, foreign);
        Blocked = !seamed || _foreign;
    }

    // Animation.Clone copies jointsDone by reference, so an animation and its clones share one HashSet that GenerateAllFrames clears
    // once per keyframe: a race as soon as two threads compile, and the singleplayer server compiles its animators on its own thread.
    // Kept on a changed engine too: every compile is then the engine's, and a clone still shares the set.
    private static void Cloned(Animation __result)
    {
        if (!NotNull(__result)) return;
        ref var done = ref JointsDone(__result);
        if (done is not null) done = [];
    }

    private static bool Generate(Animation __instance, ShapeElement[] rootElements,
        Dictionary<int, AnimationJoint> jointsById, bool recursive, out object? __state)
    {
        __state = null;
        var start = Counting.Hud && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId
            ? Stopwatch.GetTimestamp() : 0;
        Snapshot? key = null;
        if (Enabled && !Blocked && _installed && Compilable(__instance, rootElements, jointsById))
        {
            if (Hit(__instance, rootElements, recursive, ref key)) return false;
            if (Compile(__instance, rootElements, jointsById, recursive))
            {
                Keep(key, __instance.PrevNextKeyFrameByFrame);
                Record(start, __instance.Code);
                return false;
            }
        }

        if (key is not null || start > 0) __state = new Pending(key, start); // boxed only when the engine compiles
        return true;
    }

    private static void Generated(Animation __instance, object? __state)
    {
        if (__state is not Pending pending || !NotNull(__instance)) return;
        Keep(pending.Key, __instance.PrevNextKeyFrameByFrame);
        Record(pending.Start, __instance.Code);
    }

    // What the engine throws on (no keyframes, QuantityFrames, the joint cap) it throws itself, with its own message
    private static bool Compilable(Animation? animation, ShapeElement[]? roots, Dictionary<int, AnimationJoint>? joints)
    {
        if (animation?.KeyFrames is not { Length: > 0 } || animation.QuantityFrames <= 0 || roots is null ||
            joints is null) return false;
        return roots.Length <= ElementWalk.MaxElements && joints.Count < GlobalConstants.MaxAnimatedElements;
    }

    // On a miss key is the descriptor to store the compile under, null for one too large. GenerateAllFrames opens with
    // ShapeElement.CacheInverseTransformMatrixRecursive on every root, which calculateMatrices reads, so a hit runs that lazy idempotent
    // init too; a recursive descriptor has seen every element non-null, so it throws only where the engine's first statement would.
    private static bool Hit(Animation animation, ShapeElement[] roots, bool recursive, ref Snapshot? key)
    {
        if (!NotNull(animation) || !Assert(roots.Length <= ElementWalk.MaxElements)) return false;
        AnimationFrame[][]? found;
        lock (Gate)
        {
            if (!Describe(animation, roots, recursive)) return false;
            var hash = Hash();
            found = Find(hash, _buffer.AsSpan(0, _at));
            if (found is null) key = Take(hash);
            if (Counting.Hud) (Hits, Misses) = found is null ? (Hits, Misses + 1) : (Hits + 1, Misses);
        }

        if (found is null) return false;
        for (var i = 0; i < Math.Min(roots.Length, ElementWalk.MaxElements); i++)
            roots[i].CacheInverseTransformMatrixRecursive();
        animation.PrevNextKeyFrameByFrame = found;
        return true;
    }

    private static void Record(long start, string? code)
    {
        if (start <= 0) return;
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (Finite(ms) && ms > WorstMs) (WorstMs, WorstCode) = (ms, code ?? "");
    }

    private static void Keep(Snapshot? key, AnimationFrame[][]? frames)
    {
        if (key is not { } fresh || !NotNull(frames) || !Assert(frames.Length > 0)) return;
        lock (Gate)
        {
            Store(fresh, frames);
        }
    }

    // The key as the cache compares it, null for a shape it does not describe. Two shapes describe the same only when
    // GenerateAllFrames compiles them the same, the one property the tests pin: the animator indexes a pose tree by position, so a
    // descriptor that misses a difference is a wrong pose.
    internal static byte[]? Descriptor(Animation animation, ShapeElement[] roots, bool recursive = true)
    {
        lock (Gate)
        {
            if (!NotNull(animation) || !NotNull(roots) || !Describe(animation, roots, recursive)) return null;
            return Assert(_at <= _buffer.Length) ? _buffer.AsSpan(0, _at).ToArray() : null;
        }
    }

    private static bool Describe(Animation animation, ShapeElement[] roots, bool recursive)
    {
        _at = 0;
        Seen.Clear();
        if (!NotNull(animation) || !NotNull(roots)) return false;
        return Int(recursive ? 1 : 0) && Text(animation.Code) && Frames(animation) && Tree(roots, recursive);
    }

    private static bool Frames(Animation animation)
    {
        var keys = animation.KeyFrames;
        if (!NotNull(keys) || keys.Length > MaxKeyFrames) return false;
        if (!Int(animation.QuantityFrames) || !Int(keys.Length)) return false;
        for (var i = 0; i < Math.Min(keys.Length, MaxKeyFrames); i++)
            if (!Frame(keys[i]))
                return false;
        return true;
    }

    // A dictionary's iteration order is not part of its content, so the element names are sorted before they are written
    private static bool Frame(AnimationKeyFrame? key)
    {
        if (!NotNull(key) || !Int(key.Frame)) return false;
        var elements = key.Elements;
        if (elements is null) return Int(-1);
        var count = elements.Count;
        if (!Int(count) || count > MaxKeyElements) return false;
        if (_names.Length < count) _names = new string[Math.Min(MaxKeyElements, Math.Max(count, 2 * _names.Length))];
        if (!Assert(_names.Length >= count)) return false;
        elements.Keys.CopyTo(_names, 0);
        Array.Sort(_names, 0, count, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(count, MaxKeyElements); i++)
            if (!Text(_names[i]) || !elements.TryGetValue(_names[i], out var element) || !Element(element, key.Frame))
                return false;
        return true;
    }

    // Origin and the shortest-distance flags do not reach the compiled pose (GenerateFrameForElement walks flags 0 to 2), but a key
    // that covers more than the compile reads can only cost a miss, never a wrong hit. The object itself is read too: by
    // GenerateFrameForElement's left == right, and for t by the Frame the last AnimationKeyFrame.Resolve stamped on it.
    private static bool Element(AnimationKeyFrameElement? element, int frame)
    {
        if (!NotNull(element) || !Seen.Add(element) || At(element) != frame) return false;
        if (!Num(element.OffsetX) || !Num(element.OffsetY) || !Num(element.OffsetZ)) return false;
        if (!Num(element.RotationX) || !Num(element.RotationY) || !Num(element.RotationZ)) return false;
        if (!Num(element.StretchX) || !Num(element.StretchY) || !Num(element.StretchZ)) return false;
        if (!Num(element.OriginX) || !Num(element.OriginY) || !Num(element.OriginZ)) return false;
        return Int(element.RotShortestDistanceX ? 1 : 0) && Int(element.RotShortestDistanceY ? 1 : 0) &&
               Int(element.RotShortestDistanceZ ? 1 : 0);
    }

    // GenerateFrame's visit order with each element's depth, so that a child and a sibling cannot describe the same way. GenerateFrame
    // poses an element the tree holds twice at each place, and a subclass could override Equals, which the engine looks elements up by.
    private static bool Tree(ShapeElement[] roots, bool recursive)
    {
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element)) return !walk.Failed;
            if (element is null || element.GetType() != typeof(ShapeElement) || !Seen.Add(element)) return false;
            if (!Int(walk.Depth) || !Text(element.Name)) return false;
            if (!recursive) walk.Skip();
        }

        return !Assert(walk.Failed); // Next stops after MaxElements
    }

    private static bool Room(int bytes)
    {
        if (!Assert(bytes >= 0) || !Assert(_at >= 0)) return false;
        if (_at + bytes > MaxDescriptor) return false;
        if (_at + bytes <= _buffer.Length) return true;
        var room = Math.Min(MaxDescriptor, Math.Max(_at + bytes, 2 * _buffer.Length));
        if (!Assert(room >= _at + bytes)) return false;
        Array.Resize(ref _buffer, room);
        return true;
    }

    private static bool Int(int value)
    {
        if (!Room(sizeof(int)) || !Assert(_at + sizeof(int) <= _buffer.Length)) return false;
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_at), value);
        _at += sizeof(int);
        return true;
    }

    // The raw bits, so that a NaN keyframe still compares equal to itself
    private static bool Num(double? value)
    {
        if (!Int(value.HasValue ? 1 : 0) || !Assert(_at >= 0)) return false;
        if (!value.HasValue) return true;
        if (!Room(sizeof(long)) || !Assert(_at + sizeof(long) <= _buffer.Length)) return false;
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_at), BitConverter.DoubleToInt64Bits(value.Value));
        _at += sizeof(long);
        return true;
    }

    private static bool Text(string? value)
    {
        if (!Int(value?.Length ?? -1) || !Assert(_at >= 0)) return false;
        if (value is null || value.Length == 0) return true;
        var bytes = sizeof(char) * value.Length;
        if (!Room(bytes) || !Assert(_at + bytes <= _buffer.Length)) return false;
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(_buffer.AsSpan(_at));
        _at += bytes;
        return true;
    }

    private static ulong Hash()
    {
        var hash = 14695981039346656037UL;
        if (!Assert(_at >= 0) || !Assert(_at <= _buffer.Length)) return hash;
        for (var i = 0; i < Math.Min(_at, MaxDescriptor); i++) hash = (hash ^ _buffer[i]) * 1099511628211UL;
        return hash;
    }

    // The compiled set stored under exactly these descriptor bytes, under Gate
    private static AnimationFrame[][]? Find(ulong hash, ReadOnlySpan<byte> bytes)
    {
        if (!Assert(bytes.Length <= MaxDescriptor) || !Assert(_entries <= MaxEntries)) return null;
        if (!Cache.TryGetValue(hash, out var bucket) || !NotNull(bucket)) return null;
        foreach (var entry in bucket.Bounded(MaxBucket))
            if (entry.Bytes.AsSpan().SequenceEqual(bytes))
                return entry.Compiled;
        return null;
    }

    private static Snapshot Take(ulong hash)
    {
        if (!Assert(_at >= 0) || !Assert(_at <= _buffer.Length)) return new Snapshot(hash, []);
        return new Snapshot(hash, _buffer.AsSpan(0, _at).ToArray());
    }

    // Bounded by entries, each a compiled set of up to ~180 KiB: past MaxEntries the cache starts over rather than grow
    private static void Store(Snapshot key, AnimationFrame[][] frames)
    {
        if (!NotNull(frames) || !NotNull(key.Bytes)) return;
        if (_entries >= MaxEntries)
        {
            Cache.Clear();
            _entries = 0;
        }

        // two threads missed on the same descriptor and both compiled
        if (Find(key.Code, key.Bytes) is not null) return;
        if (!Cache.TryGetValue(key.Code, out var bucket)) Cache[key.Code] = bucket = [];
        if (!Assert(bucket.Count < MaxBucket) || !Assert(_entries < MaxEntries)) return; // 64 under one 64-bit hash
        bucket.Add(new Entry(key.Bytes, frames));
        _entries++;
    }

    private readonly record struct Snapshot(ulong Code, byte[] Bytes);

    private readonly record struct Entry(byte[] Bytes, AnimationFrame[][] Compiled);

    // What a miss hands the postfix when the engine compiles: the descriptor to store its result under, and when the compile began
    private readonly record struct Pending(Snapshot? Key, long Start);
}

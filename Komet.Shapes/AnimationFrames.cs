using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Shapes;

// GenerateAllFrames compiles lazily on the render thread whenever PrevNextKeyFrameByFrame is null; clones lack it and
// OnTesselation nulls it, so geared entities recompile on every re-tesselation (the engine's AnimationCache skips
// cloned shapes). This cache keys on what the compile reads: element names in visit order with depth, code,
// QuantityFrames, keyframes; identity-dependent shapes run uncached. Geometry stays out, so one outfit shares one set,
// nothing writes into a set. Hits compare the whole descriptor.
internal static partial class AnimationFrames
{
    internal const int MaxEntries = 512;
    private const int MaxKeyElements = 4096, MaxKeyFrames = 4096, MaxDescriptor = 1 << 22, MaxBucket = 64;
    private const int MaxWords = MaxDescriptor / sizeof(ulong);
    private const ulong Golden = 0x9E3779B97F4A7C15UL; // 2^64 / φ, odd
    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, List<Entry>> Cache = [];
    private static readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
    private static readonly SeamGuard Guard = new(nameof(AnimationFrames), PatchKinds.All, [typeof(AnimationFrames)],
        unblocked: true);

    // Under Gate: the descriptor being built and the cache's entry count
    private static byte[] _buffer = new byte[4096];
    private static string[] _names = new string[64];
    private static int _at, _entries;
    private static bool _installed;

    public static bool Enabled { get; set; } = true;

    // Another mod patches the compile, or the engine's body differs: the engine compiles everything
    public static bool Blocked => Guard.Blocked;

    public static bool Matched => Guard.Matched; // the engine's bodies are the ones reproduced

    // Only a rewritten body counts: a later skipping prefix, or a patch on a callee, is skipped whenever Komet answers
    internal static Action Recheck { get; } = static () => Guard.Report(Guard.Matched,
        Guard.Seams.Length == SeamCount && (EngineShape.Foreign(Guard.Seams.AsSpan(0, 1), PatchKinds.Body, null) ||
                                            EngineShape.Foreign(Guard.Seams.AsSpan(1), PatchKinds.All, null)));

    public static int Count => _entries; // compiled sets kept

    public static long Hits { get; private set; } // totals while Counting.Hud, under Gate
    public static long Misses { get; private set; }
    public static double WorstMs { get; private set; } // the slowest compile on the render thread since ResetPeaks
    public static string WorstCode { get; private set; } = "";

    public static void ResetPeaks()
    {
        (WorstMs, WorstCode) = (0, "");
    }

    // A compiled set belongs to one world's shapes; the descriptor holds names that world interned, and a grown buffer
    // (up to 4 MiB, on the LOH) would outlive it
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Seen.Clear();
            (_at, _entries, _names, _buffer) = (0, 0, new string[64], new byte[4096]);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jointsDone")]
    private static extern ref HashSet<int> JointsDone(Animation animation);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        Clear();
        ResetPeaks();
        _installed = false;
        var seams = Guard.Reset(Seams(), logger);
        var clone = AccessTools.Method(typeof(Animation), nameof(Animation.Clone), []);
        if (!NotNull(harmony) || !NotNull(clone)) return;
        // JointsDone throws otherwise
        if (Assert(AccessTools.Field(typeof(Animation), "jointsDone")?.FieldType == typeof(HashSet<int>)))
            _ = harmony.Patch(clone, postfix: new HarmonyMethod(Cloned));
        // the compile's accessors name fields of those bodies; on a changed engine they could throw
        if (!Guard.Match(Shaped(), fingerprint)) return;
        _ = harmony.Patch(seams[0], new HarmonyMethod(Generate), new HarmonyMethod(Generated));
        Recheck();
        _installed = WarmUp();
    }

    // Clones share jointsDone, which GenerateAllFrames clears per keyframe: a race once two threads compile
    private static void Cloned(Animation __result)
    {
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
        if (__state is not Pending pending) return;
        Keep(pending.Key, __instance.PrevNextKeyFrameByFrame);
        Record(pending.Start, __instance.Code);
    }

    // What the engine throws on (no keyframes, QuantityFrames, joint cap) it throws itself
    private static bool Compilable(Animation? animation, ShapeElement[]? roots, Dictionary<int, AnimationJoint>? joints)
    {
        if (animation?.KeyFrames is not { Length: > 0 } || animation.QuantityFrames <= 0 || roots is null ||
            joints is null) return false;
        return roots.Length <= ElementWalk.MaxElements && joints.Count < GlobalConstants.MaxAnimatedElements;
    }

    // On a miss key is the descriptor to store under, null if too large. A hit also runs the engine's lazy idempotent
    // CacheInverseTransformMatrixRecursive.
    private static bool Hit(Animation animation, ShapeElement[] roots, bool recursive, ref Snapshot? key)
    {
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
        var ms = FrameClock.ToMs(Stopwatch.GetTimestamp() - start);
        if (ms > WorstMs) (WorstMs, WorstCode) = (ms, code ?? "");
    }

    private static void Keep(Snapshot? key, AnimationFrame[][]? frames)
    {
        if (key is not { } fresh || !NotNull(frames) || !Assert(frames.Length > 0)) return;
        lock (Gate) Store(fresh, frames);
    }

    private static bool Describe(Animation animation, ShapeElement[] roots, bool recursive)
    {
        _at = 0;
        Seen.Clear();
        return Int(recursive ? 1 : 0) && Text(animation.Code) && Frames(animation) && Tree(roots, recursive);
    }

    private static bool Frames(Animation animation)
    {
        var keys = animation.KeyFrames;
        if (keys.Length > MaxKeyFrames) return false;
        if (!Int(animation.QuantityFrames) || !Int(keys.Length)) return false;
        for (var i = 0; i < Math.Min(keys.Length, MaxKeyFrames); i++)
            if (!Frame(keys[i])) return false;
        return true;
    }

    // Dictionary order is not content, so element names are sorted first
    private static bool Frame(AnimationKeyFrame? key)
    {
        if (!NotNull(key) || !Int(key.Frame)) return false;
        var elements = key.Elements;
        if (elements is null) return Int(-1);
        var count = elements.Count;
        if (!Int(count) || count > MaxKeyElements) return false;
        if (_names.Length < count) _names = new string[Math.Min(MaxKeyElements, Math.Max(count, 2 * _names.Length))];
        elements.Keys.CopyTo(_names, 0);
        Array.Sort(_names, 0, count, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(count, MaxKeyElements); i++)
            if (!Text(_names[i]) || !elements.TryGetValue(_names[i], out var element) || !Element(element, key.Frame))
                return false;
        return true;
    }

    // Over-keying only costs a miss. The object is read too: left == right, and t from its Resolve-stamped Frame.
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

    // Visit order with depth, so child and sibling differ; a twice-held element or subclass Equals is declined
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

        return false; // Next stops after MaxElements
    }

    private static bool Room(int bytes)
    {
        if (_at + bytes > MaxDescriptor) return false;
        if (_at + bytes <= _buffer.Length) return true;
        var room = Math.Min(MaxDescriptor, Math.Max(_at + bytes, 2 * _buffer.Length));
        Array.Resize(ref _buffer, room);
        return true;
    }

    private static bool Int(int value)
    {
        if (!Room(sizeof(int))) return false;
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_at), value);
        _at += sizeof(int);
        return true;
    }

    // Raw bits, so a NaN keyframe still equals itself
    private static bool Num(double? value)
    {
        if (!Int(value.HasValue ? 1 : 0)) return false;
        if (!value.HasValue) return true;
        if (!Room(sizeof(long))) return false;
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_at), BitConverter.DoubleToInt64Bits(value.Value));
        _at += sizeof(long);
        return true;
    }

    private static bool Text(string? value)
    {
        if (!Int(value?.Length ?? -1)) return false;
        if (value is null || value.Length == 0) return true;
        var bytes = sizeof(char) * value.Length;
        if (!Room(bytes)) return false;
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(_buffer.AsSpan(_at));
        _at += bytes;
        return true;
    }

    // Eight bytes a step, not FNV's one: every hit hashes its whole descriptor on the compiling thread. The rotation
    // brings the product's well-mixed high bits down to the low ones the dictionary buckets by; hits compare the
    // bytes, so a collision only shares a bucket.
    private static ulong Hash()
    {
        var bytes = _buffer.AsSpan(0, Math.Min(_at, MaxDescriptor));
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        var hash = 14695981039346656037UL;
        for (var i = 0; i < Math.Min(words.Length, MaxWords); i++)
            hash = System.Numerics.BitOperations.RotateLeft((hash ^ words[i]) * Golden, 29);
        for (var i = words.Length * sizeof(ulong); i < Math.Min(bytes.Length, MaxDescriptor); i++)
            hash = System.Numerics.BitOperations.RotateLeft((hash ^ bytes[i]) * Golden, 29);
        return hash;
    }

    // The compiled set stored under exactly these descriptor bytes, under Gate
    private static AnimationFrame[][]? Find(ulong hash, ReadOnlySpan<byte> bytes)
    {
        if (!Cache.TryGetValue(hash, out var bucket)) return null;
        foreach (var entry in bucket.Bounded(MaxBucket))
            if (entry.Bytes.AsSpan().SequenceEqual(bytes)) return entry.Compiled;
        return null;
    }

    private static Snapshot Take(ulong hash) =>
        new(hash, _buffer.AsSpan(0, _at).ToArray());

    // Past MaxEntries (each up to ~180 KiB) the cache starts over rather than grow
    private static void Store(Snapshot key, AnimationFrame[][] frames)
    {
        if (_entries >= MaxEntries)
        {
            Cache.Clear();
            _entries = 0;
        }

        // two threads missed on the same descriptor and both compiled
        if (Find(key.Code, key.Bytes) is not null) return;
        if (!Cache.TryGetValue(key.Code, out var bucket)) Cache[key.Code] = bucket = [];
        if (!Assert(bucket.Count < MaxBucket)) return; // 64 under one 64-bit hash
        bucket.Add(new Entry(key.Bytes, frames));
        _entries++;
    }

    private readonly record struct Snapshot(ulong Code, byte[] Bytes);

    private readonly record struct Entry(byte[] Bytes, AnimationFrame[][] Compiled);

    // What a miss hands the postfix when the engine compiles: the descriptor and the start time
    private readonly record struct Pending(Snapshot? Key, long Start);
}

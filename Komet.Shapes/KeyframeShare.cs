using System.Runtime.InteropServices;
using HarmonyLib;

namespace Komet.Shapes;

// Every shape file deserializes its keyframe elements anew: entity shapes on load, overlays and gear per entity, block
// entity shapes per block. The vanilla assets hold 404k elements of which 50k differ (Frame and values), at 224 bytes
// each. Once an Animation is deserialized, each element is swapped for the first one with the same Frame and values.
// - The engine reads an element's values, Frame and, between two keyframes of one element, whether both are one object.
//   Frame is part of the key: two keyframes share an element only where they share the frame and values, too.
// - ForElement is written on each resolve and read nowhere but FreeRAMServer (a name), as with clones, which share
//   their elements already; AnimationFrames and ShapeInitMemo accept shared entries.
// - Nothing writes an element's values after loading (no vanilla code, nor the mods checked).
internal static class KeyframeShare
{
    private const int MaxEntries = 1 << 20, MaxKeys = 4096, MaxElements = 4096, Fields = 12;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Values, AnimationKeyFrameElement> Table = [];
    private static long _shared;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Distinct elements kept, and elements swapped for one of them since the world began
    public static int Kept
    {
        get
        {
            lock (Gate) return Table.Count;
        }
    }

    public static long Shared => Interlocked.Read(ref _shared);

    public static void Install(Harmony harmony)
    {
        Stop();
        var loaded = AccessTools.DeclaredMethod(typeof(Animation), "OnDeserializedMethod");
        if (!NotNull(harmony) || !NotNull(loaded)) return;
        _ = harmony.Patch(loaded, postfix: new HarmonyMethod(Loaded));
        Installed = true;
    }

    // Leaving the world: its elements are no one's any more
    public static void Stop()
    {
        lock (Gate) Table.Clear();
        (_shared, Installed) = (0, false);
    }

    // An element this table hands out, so it may sit under several names and shapes
    internal static bool Holds(AnimationKeyFrameElement? element)
    {
        if (element is null || !Installed || !Of(element, AnimationFrames.At(element), out var values)) return false;
        lock (Gate) return Table.TryGetValue(values, out var kept) && ReferenceEquals(kept, element);
    }

    // Animation's [OnDeserialized]: its keyframes and their elements are complete
    private static void Loaded(Animation __instance)
    {
        if (!Enabled || __instance?.KeyFrames is not { } keys) return;
        var shared = 0;
        lock (Gate)
            foreach (var key in keys.Bounded(MaxKeys))
                shared += Share(key);
        if (shared > 0) _ = Interlocked.Add(ref _shared, shared);
    }

    // Under Gate. Writes through the value's ref: the enumeration stays valid.
    private static int Share(AnimationKeyFrame? key)
    {
        if (key?.Elements is not { } table || table.Count > MaxElements) return 0;
        var shared = 0;
        foreach (var (name, element) in table.Bounded(MaxElements))
        {
            if (element is null || !Of(element, key.Frame, out var values)) continue;
            if (Table.TryGetValue(values, out var kept))
            {
                if (ReferenceEquals(kept, element)) continue;
                CollectionsMarshal.GetValueRefOrNullRef(table, name) = kept;
                shared++;
            }
            else if (Table.Count < MaxEntries)
            {
                AnimationFrames.At(element) = key.Frame; // as Resolve stamps it
                Table[values] = element;
            }
        }

        return shared;
    }

    // The exact bits of every value an element holds; a subclass is left alone
    internal static bool Of(AnimationKeyFrameElement element, int frame, out Values values)
    {
        values = default;
        if (element.GetType() != typeof(AnimationKeyFrameElement)) return false;
        Span<long> bits = stackalloc long[Fields];
        var set = 0;
        ReadOnlySpan<double?> read =
        [
            element.OffsetX, element.OffsetY, element.OffsetZ, element.StretchX, element.StretchY, element.StretchZ,
            element.RotationX, element.RotationY, element.RotationZ, element.OriginX, element.OriginY, element.OriginZ
        ];
        for (var i = 0; i < Fields; i++)
        {
            if (read[i] is not { } value) continue;
            set |= 1 << i;
            bits[i] = BitConverter.DoubleToInt64Bits(value);
        }

        if (element.RotShortestDistanceX) set |= 1 << Fields;
        if (element.RotShortestDistanceY) set |= 1 << (Fields + 1);
        if (element.RotShortestDistanceZ) set |= 1 << (Fields + 2);
        values = new Values(frame, set, bits[0], bits[1], bits[2], bits[3], bits[4], bits[5], bits[6], bits[7], bits[8],
            bits[9], bits[10], bits[11]);
        return true;
    }

    internal readonly record struct Values(int Frame, int Set, long OffsetX, long OffsetY, long OffsetZ, long StretchX,
        long StretchY, long StretchZ, long RotationX, long RotationY, long RotationZ, long OriginX, long OriginY,
        long OriginZ);
}

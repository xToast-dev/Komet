using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client;

namespace Komet.Diagnostics;

// The engine's frame profiler (on for Komet's HUD) makes a new marks dictionary, a ProfileEntry per mark and a child
// range with its own dictionary every frame: 376 marks a frame were 12 % of all allocations in a steady-state trace, so
// a share of the GC pauses. The client frame profiler's root dictionaries alternate between two kept sets, emptied as the
// frame begins: the marks keep this frame's order (a pass is the marks after its stage mark). The previous frame's set
// stays whole until the frame after it (FrameClock reads it at the frame's end); its ProfileEntry objects (11 MB per 20 s
// in a steady-state trace, every reader copies their counts within the frame) go to a spare stack as the set is emptied,
// and MarkInternal's `new ProfileEntry()` takes from it on the thread that runs the client frame.
// The server's and other threads' profilers keep the engine's way. Mark(code, param) joined code and param anew at every call, one
// string per dialog and frame ("gdm-finFr-" + DebugName, "rendGui" + DebugName: 2 % of all allocations in flight; the
// render stages' "beginrenderstage-" + stage and BlockEntity's "initbebehavior-" + type another 0.4 %); a string, enum or
// type param's joined name is kept and given to the same MarkInternal. Other params (positions) as the engine.
internal static class ProfilerPool
{
    private const int MaxCodes = 4096, MaxChildren = 64, MaxNames = 8192, MaxInstructions = 512;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Code, object Param), string> Names = new();

    private static readonly Kept[] Sets = [new(), new()];
    private static readonly Stack<ProfileEntry> Spare = new();
    private static long _frames;
    private static int _owner = -1; // the client frame's thread, the only one Spare is used on

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }
    public static bool Recycling { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MarkInternal")]
    private static extern void MarkInternal(FrameProfilerUtil profiler, string code);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentEntry")]
    private static extern ref ProfileEntryRange? Current(FrameProfilerUtil profiler);

    public static void Install(Harmony harmony)
    {
        Patched = false;
        var begin = AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Begin), [typeof(string), typeof(object[])]);
        if (!NotNull(harmony) || !NotNull(begin) ||
            !Assert(Il.Binds(begin, AccessTools.Method(typeof(ProfilerPool), nameof(Began))))) return;
        Patched = NotNull(harmony.Patch(begin, postfix: new HarmonyMethod(Began)));
        var mark = AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Mark), [typeof(string), typeof(object)]);
        if (Patched && NotNull(mark)) _ = harmony.Patch(mark, new HarmonyMethod(AccessTools.Method(typeof(ProfilerPool), nameof(Marked))));
        Recycling = false;
        var markInternal = AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), "MarkInternal", [typeof(string)]);
        if (Patched && NotNull(markInternal)) _ = harmony.Patch(markInternal, transpiler: new HarmonyMethod(Rewrite));
    }

    // The one `new ProfileEntry()` becomes Entry(); any other shape of the body is left as it is
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (ctor, mine) = (AccessTools.Constructor(typeof(ProfileEntry), []), AccessTools.Method(typeof(ProfilerPool), nameof(Entry)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) || !NotNull(mine)) return code;
        Recycling = Il.SubstituteSingle(code, c => c.opcode == System.Reflection.Emit.OpCodes.Newobj && ReferenceEquals(c.operand, ctor), mine);
        return code;
    }

    // A spare entry, zeroed, on the client frame's thread; a new one elsewhere or with none spare
    internal static ProfileEntry Entry()
    {
        if (!Enabled || Environment.CurrentManagedThreadId != _owner || !Spare.TryPop(out var entry)) return new ProfileEntry();
        (entry.ElapsedTicks, entry.CallCount) = (0, 0);
        return entry;
    }

    // Prefix on Mark(code, param): a string param's joined name from the table; the engine throws for a null code
    private static bool Marked(FrameProfilerUtil __instance, string code, object param)
    {
        if (!Enabled || param is not (string or Enum or Type) || code is null || __instance is null ||
            !(__instance.Enabled || __instance.PrintSlowTicks)) return true;
        if (!Names.TryGetValue((code, param), out var joined))
        {
            joined = code + param;
            if (Names.Count < MaxNames) Names[(code, param)] = joined;
        }

        MarkInternal(__instance, joined);
        return false;
    }

    // After Begin made the client frame's root: the root takes this frame's set, emptied. A click's root (Begin("mousedown")
    // or "mouseup", between two frames) keeps the engine's dictionaries: taking a set there turned the alternation, so the
    // next frame emptied the set of the frame the HUD still reads at Ortho.
    private static void Began(FrameProfilerUtil __instance, string? beginText)
    {
        if (!Enabled || !Patched || beginText is not null || !ReferenceEquals(__instance, ScreenManager.FrameProfiler) ||
            !__instance.Enabled || Current(__instance) is not { } root) return;
        if (root.Marks is not null || root.ChildRanges is not null) return; // not the fresh root Begin makes
        var turn = (int)(++_frames & 1);
        var set = Sets[turn];
        if (set.Marks.Count > MaxCodes || set.Children.Count > MaxChildren)
        {
            Sets[turn] = new Kept(); // a flood of codes: this frame the engine's way, the next of this turn pooled again
            return;
        }

        _owner = Environment.CurrentManagedThreadId;
        Emptied(set.Marks);
        foreach (var child in set.Children.Values.Bounded(MaxChildren)) Rewound(child, root);
        (root.Marks, root.ChildRanges) = (set.Marks, set.Children);
    }

    // The marks' entries go spare (no dictionary holds them after), the marks are emptied
    private static void Emptied(Dictionary<string, ProfileEntry>? marks)
    {
        if (marks is null) return;
        foreach (var entry in marks.Values.Bounded(MaxCodes))
            if (Spare.Count < MaxCodes && entry is not null)
                Spare.Push(entry);
        marks.Clear();
    }

    // A kept child range under this frame's root, its counts zeroed and marks emptied, its own children likewise (two
    // levels: the engine nests "behaviors" under the root and entity ticks under that)
    private static void Rewound(ProfileEntryRange child, ProfileEntryRange parent)
    {
        (child.ParentRange, child.ElapsedTicks, child.CallCount) = (parent, 0, 0);
        Emptied(child.Marks);
        if (child.ChildRanges is not { } children) return;
        foreach (var grandchild in children.Values.Bounded(MaxChildren))
        {
            (grandchild.ParentRange, grandchild.ElapsedTicks, grandchild.CallCount) = (child, 0, 0);
            Emptied(grandchild.Marks);
            grandchild.ChildRanges = null; // deeper nesting starts anew
        }
    }

    private sealed class Kept
    {
        public Dictionary<string, ProfileEntry> Marks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ProfileEntryRange> Children { get; } = new(StringComparer.Ordinal);
    }
}

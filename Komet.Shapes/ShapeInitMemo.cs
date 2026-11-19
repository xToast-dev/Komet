using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Common.Entities;
using ResolvedTable = Vintagestory.API.Datastructures.FastSmallDictionary<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;

namespace Komet.Shapes;

// Every gearless animal re-inits its type's shared shape per tesselation (AnimationCache.InitManager inits before its
// lookup), allocating per keyframe and zeroing JointId, a race with the pool thread reading it. For the same shape,
// arguments and tree the init is idempotent, so it is remembered per Shape (main thread); a skip still rewrites what
// clones share (ForElement, Frame, ParentElement, CacheInvTransforms). Warnings of a flawed shape log once.
internal static partial class ShapeInitMemo
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xF2EEA794EC8A99FAUL;
    private const int MaxAnimations = 4096, MaxKeys = 1 << 16, MaxEntries = 1 << 18, MaxPerKey = 4096, MaxJoints = 4096;
    private const int MaxPoints = 4096, MaxNames = 256, Repeats = 2, MaxSeen = 64;
    private static readonly ConditionalWeakTable<Shape, Memo> Memos = [];
    private static readonly int[] Seen = new int[MaxSeen]; // identity hashes of shapes inited once, most of them clones

    private static readonly SeamGuard Guard = new(nameof(ShapeInitMemo), KapiPatchKinds.All, [typeof(ShapeInitMemo)],
        unblocked: true);

    private static bool _installed;
    private static int _seen;

    public static bool Enabled { get; set; } = true;

    // Another mod patches the init or its callees, or the engine differs: every init runs
    public static bool Blocked => Guard.Blocked;

    public static bool Matched => Guard.Matched; // the engine's bodies are the ones verified

    // Features.Recheck asks again on LevelFinalize, when every mod has started
    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Main-thread inits answered from the memo, a total while Counting.Hud
    public static long Skipped { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_installed, _seen) = (false, 0);
        var seams = Guard.Reset(Seams(), logger);
        Memos.Clear();
        Array.Clear(Seen);
        // WarmUp before the patch: the engine's init, never remembered
        if (!Guard.Match(Shaped(), fingerprint) || !WarmUp() || seams[0] is not { } init) return;

        _ = harmony.Patch(init, new HarmonyMethod(Check), new HarmonyMethod(Done));
        Recheck();
        _installed = true;
    }

    // The [AggressiveOptimization] checks JIT here at mod load, not when a herd appears; a shape of its own
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

    // [0] patched; [1]-[9] are Inside(); [10] picks cached or uncached init
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
        return seams;
    }

    // What InitForAnimations runs, which the skip leaves out ([7] ResolveReferences writes ForElement; the skip does too)
    private static MethodBase?[] Inside()
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
        return inside;
    }

    // The proof's bodies: the seams and the table insert whose keys and values arrays the memo reads
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams(), AccessTools.PropertySetter(typeof(ResolvedTable), "Item")];
        return shaped;
    }

    private static bool Check(Shape? __instance, string[]? disableElements, string[]? requireJointsForElements,
        out Memo? __state)
    {
        __state = null;
        if (!_installed || __instance is null ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            return true;
        }

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
        memo.Valid = true;
        if (Counting.Hud) Skipped++;

        return false;
    }

    private static void Done(Shape __instance, string[]? requireJointsForElements, Memo? __state, bool __runOriginal)
    {
        if (__state is not { } memo || !__runOriginal) return;

        memo.Inits = Math.Min(memo.Inits + 1, Repeats);
        if (Enabled && !Blocked && memo.Inits >= Repeats)
        {
            memo.Valid = Record(memo, __instance, requireJointsForElements);
        }
    }

    // A shape gets a memo on its second init (clones are inited once); the first leaves its hash in a ring
    private static bool SeenBefore(Shape shape)
    {
        var hash = RuntimeHelpers.GetHashCode(shape);
        for (var i = 0; i < MaxSeen; i++)
        {
            if (Seen[i] == hash) return true;
        }

        (Seen[_seen], _seen) = (hash, (_seen + 1) % MaxSeen);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Same(Memo memo, Shape shape, string[]? disable, string[]? joints)
    {
        if (!SameArguments(memo, disable, joints)) return false;

        if (memo.Roots is null || !ReferenceEquals(shape.Elements, memo.Roots)) return false;

        return Walk(memo.Roots, memo, Mode.Check, null) && SameAnimations(memo, shape) && SameKeys(memo) &&
               SameJoints(memo, shape.JointsById);
    }

    // Same joints by content; a disable list matters only for unresolved keys (others mint placeholders)
    private static bool SameArguments(Memo memo, string[]? disable, string[]? joints)
    {
        if (joints is null || !joints.AsSpan().SequenceEqual(memo.Required)) return false;

        if (disable is null || memo.Unresolved.Length == 0) return true;

        for (var i = 0; i < Math.Min(memo.Unresolved.Length, MaxNames); i++)
        {
            if (Array.IndexOf(disable, memo.Unresolved[i]) < 0) return false;
        }

        return true;
    }
}

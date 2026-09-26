using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common.Entities;

namespace Komet.Shapes;

// The local player's shape is initialised for animation twice on every re-tesselation. Entity.OnTesselation ends in
// AnimManager.LoadAnimator(…, willDeleteElements, "head"), which runs Shape.InitForAnimations on the shape; EntityPlayer then, for
// IsSelf, calls OtherAnimManager.LoadAnimator(…, null, "head") on the same shape, which runs it again. In between only
// PlayerHeadController's constructor runs, and it only looks poses up. The second run recomputes exactly what the first left:
// ForElement and Frame on every keyframe element (a null disable list only drops the placeholder the first run already put on a
// missing element), ParentElement, JointId, and JointsById under the same ids for the same elements. Only the resolved keyframe
// tables and the AnimationJoint objects come out new, equal in content, and both animators read them lazily through the Animation and
// the dictionary they share.
//
// One thing does change in between: the first animator's AnimatorBase constructor lower-cases every Animation.Code, and the second
// init keys Shape.AnimationsByCrc32 by the code it finds then. Vanilla codes are lower case already; a code that is not would get an
// entry only the second init makes, so the second init then runs.
//
// So a [ThreadStatic] window around EntityPlayer.OnTesselation remembers the last InitForAnimations inside its base call that
// completed, and ShapeInitMemo's prefix skips the first one after base returns when it is the same shape with the same joints, no
// other disable list and the same animation codes. Everything else runs: another shape, other joints, a renamed animation, an init
// inside base that threw, a nested tesselation, a second init after the skipped one, a subclass of EntityPlayer (which may override
// the protected OnTesselation and change the shape after the first init), and all of it while another mod patches one of the
// tesselation, animation manager and head controller methods between the two calls, or anything inside the skipped init.
internal static class InitOnce
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x72F1B0A0AD48C62BUL;
    private const int MaxNames = 256, MaxAnimations = 4096, SeamCount = 18;

    [ThreadStatic] private static EntityPlayer? _player;
    [ThreadStatic] private static Shape? _shape;
    [ThreadStatic] private static string[]? _joints, _disabled;

    [ThreadStatic]
    private static Animation[]? _animations; // the remembered init's animations and their codes as it saw them

    [ThreadStatic] private static string?[]? _codes;
    [ThreadStatic] private static int _depth;

    [ThreadStatic]
    private static bool _armed; // base has returned: the next init on the remembered shape is the duplicate

    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _installed, _foreign;

    public static bool Enabled { get; set; } = true;

    // Another mod patches what runs between or inside the two inits, or the engine's is not the one verified: both run
    internal static bool Blocked { get; private set; }

    public static long Skipped { get; private set; } // a total while Counting.Hud, main thread

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_installed, _shaped, Blocked, _seams, _logger, _foreign) = (false, false, true, Seams(), logger, false);
        if (!NotNull(harmony) || !Assert(_seams.Length == SeamCount)) return;
        _shaped = EngineShape.Matches(Shaped(), fingerprint, nameof(InitOnce), logger);
        if (!_shaped) return;
        var self = typeof(InitOnce);
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(self, nameof(Open)),
            finalizer: new HarmonyMethod(self, nameof(Close))));
        _ = NotNull(harmony.Patch(_seams[1], postfix: new HarmonyMethod(self, nameof(Based))));
        Recheck();
        _installed = true;
    }

    // ShapeInitMemo owns the patch on [2], InitForAnimations. Harmony patches are process wide: KometModSystem asks again on
    // LevelFinalize, when every mod has started.
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == SeamCount);
        _foreign = EngineShape.Report(_logger, nameof(InitOnce), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.All, null, typeof(InitOnce),
                typeof(ShapeInitMemo)));
        Blocked = !seamed || _foreign;
    }

    // [0] to [2] are patched, [3] to [8] run between the two inits: EntityPlayer.OnTesselation calls base, whose protected overload
    // ends in LoadAnimator; then PlayerHeadController's constructor and the second LoadAnimator with its Init. [9] to [17] run inside
    // the init that is skipped, ShapeInitMemo.Inside(). Declared methods only: were EntityPlayer to stop overriding OnTesselation, the
    // lookup must fail rather than find Entity's. Every lookup names its parameters, so an overload an engine update adds cannot make
    // it ambiguous.
    private static MethodBase?[] Seams()
    {
        var shapeRef = typeof(Shape).MakeByRefType();
        Type[] init = [typeof(ICoreAPI), typeof(Entity)];
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(EntityPlayer), nameof(EntityPlayer.OnTesselation),
                [shapeRef, typeof(string)]),
            AccessTools.DeclaredMethod(typeof(Entity), nameof(Entity.OnTesselation), [shapeRef, typeof(string)]),
            AccessTools.Method(typeof(Shape), nameof(Shape.InitForAnimations),
                [typeof(ILogger), typeof(string), typeof(string[]), typeof(string[])]),
            AccessTools.DeclaredMethod(typeof(Entity), "OnTesselation",
                [shapeRef, typeof(string), typeof(bool).MakeByRefType()]),
            AccessTools.DeclaredMethod(typeof(AnimationManager), nameof(AnimationManager.LoadAnimator),
            [
                typeof(ICoreAPI), typeof(Entity), typeof(Shape), typeof(RunningAnimation[]), typeof(bool),
                typeof(string[]), typeof(string[])
            ]),
            AccessTools.DeclaredMethod(typeof(AnimationManager), nameof(AnimationManager.Init), init),
            AccessTools.DeclaredMethod(typeof(PlayerAnimationManager), nameof(PlayerAnimationManager.Init), init),
            AccessTools.Constructor(typeof(PlayerHeadController),
                [typeof(IAnimationManager), typeof(EntityPlayer), typeof(Shape)]),
            AccessTools.Constructor(typeof(EntityHeadController),
                [typeof(IAnimationManager), typeof(EntityAgent), typeof(Shape)]),
            .. ShapeInitMemo.Inside()
        ];
        return Assert(seams.Length == SeamCount) &&
               Assert(seams[0] is null || seams[0]!.DeclaringType == typeof(EntityPlayer))
            ? seams
            : [];
    }

    // The bodies the skip's proof rests on: the seams, and AnimatorBase's constructor, the first animator's, which lower-cases every
    // Animation.Code between the two inits (see Unchanged)
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped =
        [
            .. Seams(),
            AccessTools.Constructor(typeof(AnimatorBase),
                [typeof(WalkSpeedSupplierDelegate), typeof(Animation[]), typeof(Action<string>)])
        ];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    internal static void Open(EntityPlayer __instance)
    {
        if (!NotNull(__instance) || !Assert(_depth >= 0)) return;
        if (++_depth > 1)
        {
            // a tesselation inside a tesselation is no longer the sequence this knows: nothing more is skipped until it closes
            (_player, _shape, _armed) = (null, null, false);
            return;
        }

        // Only EntityPlayer itself: a subclass may override the protected OnTesselation and change the shape after base's init
        var player = __instance.GetType() == typeof(EntityPlayer) ? __instance : null;
        (_player, _shape, _joints, _disabled, _armed) = (player, null, null, null, false);
        (_animations, _codes) = (null, null);
    }

    // Harmony's finalizer: runs however the method leaves, and a void finalizer rethrows the original exception untouched
    internal static void Close()
    {
        if (!Assert(_depth > 0)) return; // Open ran first
        if (--_depth > 0) return;
        _ = Assert(!_armed || _shape is not null); // armed only ever with a remembered shape
        (_player, _shape, _joints, _disabled, _armed) = (null, null, null, null, false);
        (_animations, _codes) = (null, null);
    }

    // Entity.OnTesselation has returned into EntityPlayer's: whatever init it ran last is the one the second would repeat
    internal static void Based(Entity __instance)
    {
        if (_depth != 1 || !NotNull(__instance) || !ReferenceEquals(__instance, _player)) return;
        if (_armed) (_armed, _shape) = (false, null); // base ran twice: not the sequence this knows
        else _armed = _shape is not null && Assert(_joints is null || _joints.Length <= MaxNames);
    }

    // Asked first by ShapeInitMemo's InitForAnimations prefix: true skips this init as the duplicate of the one base just completed
    internal static bool Duplicate(Shape shape, string[]? disable, string[]? joints)
    {
        if (!_armed)
        {
            // An init inside base begins: until Completed sees it end, it is the last one and is remembered as nothing, so one that
            // throws half way (Entity.OnTesselation catches and logs it) cannot leave an earlier one to be repeated
            if (_depth == 1) _shape = null;
            return false;
        }

        if (!Enabled || Blocked || !_installed || !NotNull(shape) || !Assert(_depth == 1)) return false;
        if (!ReferenceEquals(shape, _shape) || !Same(joints, _joints)) return false;
        if (disable is not null && !Same(disable, _disabled)) return false;
        if (!Unchanged(shape.Animations)) return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // And told by its postfix about every init that completed, skipped ones included
    internal static void Completed(Shape shape, string[]? disable, string[]? joints)
    {
        if (_depth != 1 || _player is null || !NotNull(shape)) return;
        if (_armed)
        {
            (_armed, _shape) = (false, null); // one skip per window: a third init runs whatever it is
            return;
        }

        (_shape, _joints, _disabled) = (shape, Copy(joints), Copy(disable));
        (_animations, _codes) = Codes(shape.Animations);
        _ = Assert(_shape is not null);
    }

    // The animations as the completed init saw them, codes included, or (null, null) for none or too many to remember
    private static (Animation[]?, string?[]?) Codes(Animation[]? animations)
    {
        if (animations is null || animations.Length > MaxAnimations) return (null, null);
        var copy = new Animation[animations.Length];
        var codes = new string?[animations.Length];
        for (var i = 0; i < Math.Min(animations.Length, MaxAnimations); i++)
            (copy[i], codes[i]) = (animations[i], animations[i]?.Code);
        return Assert(copy.Length == codes.Length) && Assert(codes.Length == animations.Length)
            ? (copy, codes)
            : (null, null);
    }

    // The same animations under the same codes: CollectAndResolveReferences would then write AnimationsByCrc32 exactly as the
    // completed init did. A code the first animator lower-cased in between would add an entry, so that init has to run.
    private static bool Unchanged(Animation[]? animations)
    {
        if (animations is null || _animations is null || _codes is null) return false;
        if (!Assert(_codes.Length == _animations.Length) || animations.Length != _animations.Length) return false;
        if (!Assert(animations.Length <= MaxAnimations)) return false; // Codes remembers no more
        for (var i = 0; i < Math.Min(animations.Length, MaxAnimations); i++)
        {
            if (!ReferenceEquals(animations[i], _animations[i]) ||
                !string.Equals(animations[i]?.Code, _codes[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string[]? Copy(string[]? names)
    {
        if (names is null || !Assert(names.Length <= MaxNames)) return null;
        var copy = new string[names.Length];
        names.AsSpan().CopyTo(copy);
        return Assert(copy.Length == names.Length) ? copy : null;
    }

    private static bool Same(string[]? names, string[]? remembered)
    {
        if (names is null || remembered is null) return names is null && remembered is null;
        if (!Assert(remembered.Length <= MaxNames) || names.Length != remembered.Length) return false;
        for (var i = 0; i < Math.Min(names.Length, MaxNames); i++)
            if (!string.Equals(names[i], remembered[i], StringComparison.Ordinal))
                return false;
        return true;
    }
}

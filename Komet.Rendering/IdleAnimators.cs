using HarmonyLib;
using Komet.Api;
using Komet.Core;
using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Every animating block entity registers an AnimationUtil and AnimatableRenderer called every frame; bench world ~2000
// with 14 animating (8000 mostly cache-missing calls returning at once). Only renderers whose OnRenderFrame is one of
// those two engine bodies are skipped, while verified (EngineShape) and unpatched (AnimatableCulling's prefix lets idle
// ones through). With extended debug info the loop also checks GL errors and marks the profiler per renderer (1263
// "ShadowFar-animatable" marks in one frame, nearly all idle); both are skipped after a skipped Frame (no GL call; its
// time lands in the next mark). Frame still loads handler and renderer, so both loops first ask Skip (Parked) for one
// byte per entry, polled at stage start: a parked idle entry touches no object; Frame stays the fallback.
internal static partial class IdleAnimators
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x4D20A29C71A4C070UL;

    private const int MaxInstructions = 256, Sites = 2, MaxTypes = 256;
    private const string UtilName = "Vintagestory.GameContent.BlockEntityAnimationUtil";

    private static readonly Dictionary<Type, Kind> Kinds = []; // main thread only
    private static readonly MethodBase?[] Bodies = Seams();
    private static readonly Kind RendererKind = Classify(typeof(AnimatableRenderer)); // after Bodies: the type of most calls
    private static Type? _lastType;
    private static Kind _lastKind, _utilKind; // the last: _utilType's, classified at Install
    private static ILogger? _logger;
    private static bool _shaped, _foreign, _rewritten, _unmarked;
    private static bool _left; // the renderer Frame was last handed was left out (main thread)

    public static bool Enabled { get; set; } = true;

    // Bodies unverified, loops not rewritten, or another mod patches the bodies
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;
    internal static bool Rewritten => _rewritten;
    internal static bool Unmarked => _unmarked; // the extended-debug loop's check and mark go through Check and Mark

    // Counting.Hud total (main thread)
    public static long Skipped { get; private set; }

    private enum Kind : byte { Other, Renderer, Util }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "stopRenderTriggered")]
    private static extern ref bool StopTriggered(AnimationUtil util);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _logger, _foreign, _rewritten, _unmarked, _left, _parks, _crossed) =
            (false, true, logger, false, false, false, false, false, false);
        (_lastType, _lastKind, _utilKind, _utilType) = (null, Kind.Other, Kind.Other, null);
        Kinds.Clear();
        Clear();
        var trigger = _trigger = AccessTools.DeclaredMethod(typeof(ClientEventManager),
            nameof(ClientEventManager.TriggerRenderStage), [typeof(EnumRenderStage), typeof(float)]);
        if (!NotNull(harmony) || !NotNull(trigger) || !NotNull(Bodies[0]) || !NotNull(Bodies[1])) return;
        _shaped = EngineShape.Matches(Bodies, fingerprint, nameof(IdleAnimators), logger);
        if (!_shaped) return;
        // util type of nearly every other call; without the content assembly nothing parks
        _utilType = AccessTools.TypeByName(UtilName);
        _utilKind = _utilType is { } util ? Classify(util) : Kind.Other;
        Measure();
        _ = harmony.Patch(trigger, new HarmonyMethod(Staged), transpiler: new HarmonyMethod(Rewrite));
        if (!_rewritten) logger?.Warning("Komet IdleAnimators: the render loops were not found; idle animatables are not skipped");
        var start = AccessTools.DeclaredMethod(typeof(AnimationUtil), nameof(AnimationUtil.StartAnimation),
            [typeof(AnimationMetaData)]);
        if (_parks && _utilKind == Kind.Util && NotNull(start))
            _ = harmony.Patch(start, postfix: new HarmonyMethod(Woken));
        Recheck();
    }

    internal static void Recheck()
    {
        var seamed = _shaped && _rewritten;
        _foreign = EngineShape.Report(_logger, nameof(IdleAnimators), _foreign,
            seamed && EngineShape.Foreign(Bodies, PatchKinds.All, null, typeof(AnimatableCulling)));
        Blocked = !seamed || _foreign;
        // a foreign transpiler may sit between Skip and the call or rebuild the loop: Frame decides alone then
        var crossed = _parks && EngineShape.Foreign([_trigger], PatchKinds.Body, null, typeof(IdleAnimators));
        if (crossed && !_crossed)
            _logger?.Notification(
                "Komet IdleAnimators: another mod rewrites TriggerRenderStage, idle animatables are asked one by one");
        _crossed = crossed;
    }

    internal static MethodBase?[] Seams() =>
    [
        AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame),
            [typeof(float), typeof(EnumRenderStage)]),
        AccessTools.DeclaredMethod(typeof(AnimationUtil), nameof(AnimationUtil.OnRenderFrame),
            [typeof(float), typeof(EnumRenderStage)])
    ];

    // Both render loops (extendedDebugInfo and plain) call through Frame, else engine IL. The debug loop's CheckGlError
    // and Mark between the calls go through Check/Mark if found exactly there. Both open with Skip if both are
    // engine's.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        (_rewritten, _unmarked, _parks) = (false, false, false);
        var (call, frame) = (AccessTools.Method(typeof(IRenderer), nameof(IRenderer.OnRenderFrame)),
            AccessTools.Method(typeof(IdleAnimators), nameof(Frame)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(call) || !NotNull(frame) ||
            !NotNull(generator)) return code;
        if (!Assert(Il.Count(code, c => c.Calls(call)) == Sites)) return code;
        var (first, done) = (code.FindIndex(c => c.Calls(call)), 0);
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(call) && Il.Substitute(code, i, frame)) done++;
        _rewritten = done == Sites; // a site left as it was still calls the renderer, as Frame does while blocked
        if (!_rewritten) return code;
        var second = code.FindIndex(first + 1, c => c.Calls(frame));
        _unmarked = Unmark(code, first, second); // substitutes in place: the two indexes hold
        _parks = Gate(code, generator, first, second);
        return code;
    }

    private static bool Unmark(List<CodeInstruction> code, int first, int second)
    {
        var (check, mark) = (AccessTools.Method(typeof(ClientPlatformAbstract), nameof(ClientPlatformAbstract.CheckGlError),
            [typeof(string)]), AccessTools.Method(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Mark), [typeof(string)]));
        if (!NotNull(check) || !NotNull(mark)) return false;
        var (at, by) = (Il.Single(code, c => c.Calls(check)), Il.Single(code, c => c.Calls(mark)));
        if (!(first < at && at < by && by < second)) return false;
        var self = typeof(IdleAnimators);
        return Il.Substitute(code, at, AccessTools.DeclaredMethod(self, nameof(Check))) &&
               Il.Substitute(code, by, AccessTools.DeclaredMethod(self, nameof(Mark)));
    }

    // Both loops ask Skip (list, index) first and jump to the increment on yes. The later loop is changed first so its
    // insert shifts nothing the other points at.
    private static bool Gate(List<CodeInstruction> code, ILGenerator generator, int first, int second)
    {
        var skip = AccessTools.DeclaredMethod(typeof(IdleAnimators), nameof(Skip), [typeof(List<RenderHandler>), typeof(int)]);
        var (item, count) = (AccessTools.Method(typeof(List<RenderHandler>), "get_Item", [typeof(int)]),
            AccessTools.PropertyGetter(typeof(List<RenderHandler>), nameof(List<>.Count)));
        if (!NotNull(skip) || !NotNull(item) || !NotNull(count)) return false;
        var (debug, plain) = (Loop(code, first, item, count), Loop(code, second, item, count));
        if (debug.Head < 0 || plain.Head < 0 || debug.Step >= plain.Head) return false;
        return Enter(code, generator, plain, skip) && Enter(code, generator, debug, skip);
    }

    // Loop around the Frame call: body opens loading list, index, get_Item, Renderer and the two arguments, carrying
    // the label the closing blt jumps to; ends with the increment before the test (index, Count, branch). Else (-1,
    // -1).
    private static (int Head, int Step) Loop(List<CodeInstruction> code, int call, MethodInfo item, MethodInfo count)
    {
        const int Body = 6, Tail = 7;
        (int, int) none = (-1, -1);
        if (call < Body) return none;
        var head = call - Body;
        var (list, index) = (Il.Local(code[head], Il.Uses.Load), Il.Local(code[head + 1], Il.Uses.Load));
        if (list < 0 || index < 0 || list == index || !code[head + 2].Calls(item) || code[head].labels.Count != 1 ||
            code[head].blocks.Count > 0) return none;
        var back = code.FindIndex(call, c => c.opcode == OpCodes.Blt || c.opcode == OpCodes.Blt_S);
        if (back - Tail <= call || code[back].operand is not Label target || !code[head].labels.Contains(target))
            return none;
        var step = back - Tail;
        var shaped = Il.Local(code[step], Il.Uses.Load) == index && code[step + 1].opcode == OpCodes.Ldc_I4_1 &&
                     code[step + 2].opcode == OpCodes.Add && Il.Local(code[step + 3], Il.Uses.Store) == index &&
                     Il.Local(code[step + 4], Il.Uses.Load) == index && Il.Local(code[step + 5], Il.Uses.Load) == list &&
                     code[step + 6].Calls(count) && code[step].blocks.Count == 0;
        return shaped ? (head, step) : none;
    }

    // Before the body: load list and index, call Skip, on yes branch to the increment; the body's label moves up.
    private static bool Enter(List<CodeInstruction> code, ILGenerator generator, (int Head, int Step) loop, MethodInfo skip)
    {
        var next = generator.DefineLabel();
        code[loop.Step].labels.Add(next);
        var (list, index) = (code[loop.Head], code[loop.Head + 1]);
        var check = new CodeInstruction(list.opcode, list.operand).MoveLabelsFrom(list);
        code.InsertRange(loop.Head,
        [
            check, new CodeInstruction(index.opcode, index.operand), new CodeInstruction(OpCodes.Call, skip),
            new CodeInstruction(OpCodes.Brtrue, next)
        ]);
        return true;
    }

    internal static void Frame(IRenderer renderer, float dt, EnumRenderStage stage)
    {
        if (TrySkip(renderer))
        {
            _left = true;
            return;
        }

        renderer.OnRenderFrame(dt, stage); // null throws as the engine's callvirt did
        _left = false;
    }

    // Frame's question, also asked by ModTimes' replay: renderer skipped (and counted)
    internal static bool TrySkip(IRenderer? renderer)
    {
        Ahead(renderer);
        if (!Enabled || Blocked || !Idle(renderer)) return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // Null platform or profiler throws as the engine's callvirt did, unless the renderer was skipped
    internal static void Check(ClientPlatformAbstract platform, string errmsg)
    {
        if (!_left) platform.CheckGlError(errmsg);
    }

    internal static void Mark(FrameProfilerUtil profiler, string code)
    {
        if (_left) _left = false;
        else profiler.Mark(code);
    }

    internal static bool Idle(IRenderer? renderer) => KindOf(renderer?.GetType()) switch
    {
        Kind.Renderer => !Unsafe.As<AnimatableRenderer>(renderer)!.ShouldRender,
        Kind.Util => Resting(Unsafe.As<AnimationUtil>(renderer)!),
        _ => false
    };

    // No-op without animator or renderer, else steps while an animation runs and stops rendering after
    private static bool Resting(AnimationUtil util)
    {
        if (util is not { animator: { } animator, renderer: { } shown }) return true;
        if (util.activeAnimationsByAnimCode is not { Count: 0 } || animator.ActiveAnimationCount != 0) return false;
        return !shown.ShouldRender || StopTriggered(util);
    }

    // A type counts when its OnRenderFrame is one of the two engine bodies; decided once per type. The two common types
    // alternate in Opaque past the one-entry cache, so they are answered first.
    private static Kind KindOf(Type? type)
    {
        if (type is null) return Kind.Other;
        if (type == typeof(AnimatableRenderer)) return RendererKind;
        if (type == _utilType) return _utilKind;
        if (type == _lastType) return _lastKind;
        if (!Kinds.TryGetValue(type, out var kind))
        {
            kind = Classify(type);
            if (Kinds.Count < MaxTypes) Kinds[type] = kind;
        }

        (_lastType, _lastKind) = (type, kind);
        return kind;
    }

    private static Kind Classify(Type type)
    {
        if (!typeof(IRenderer).IsAssignableFrom(type)) return Kind.Other;
        var map = type.GetInterfaceMap(typeof(IRenderer));
        var slot = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IRenderer.OnRenderFrame));
        if (!Index(slot, map.TargetMethods.Length)) return Kind.Other;
        var target = map.TargetMethods[slot].MethodHandle.Value; // the same body whichever type reflected it
        if (target == Bodies[0]?.MethodHandle.Value) return Kind.Renderer;
        return target == Bodies[1]?.MethodHandle.Value ? Kind.Util : Kind.Other;
    }
}

// Frame decides per call, so each idle entry still costs two or three dependent cache misses, ~12,900 times a frame in
// the user's base, all but ~90 idle. Here ShadowFar, ShadowNear, Opaque and OIT are polled once at stage start: parked
// objects lie in dense prefetched arrays, answering into one byte per list entry the transpiled loops read before the
// handler (Skip). Arrays rebuild when the list or its _version changed, at most once per stage and frame. Parked: only
// BlockEntityAnimationUtil (exact type) and the AnimatableRenderers it owns; vanilla never wakes them mid-stage
// (MarkBlockDirty callbacks run in Before; StartAnimation hooked by Woken), whereas GearRenderer toggles its tripod in
// Opaque. Plain AnimationUtils and mod renderers stay with Frame. A list changed mid-stage makes Skip answer no for the
// rest. A util holds its BlockEntity, so an unpolled parking (knob off, stage no longer triggered) would keep entities
// past their chunks: Release drops it, the owned set with Opaque's.
internal static partial class IdleAnimators
{
    private const int Stages = 4, OpaqueSlot = 2, MaxParked = 1 << 16;

    // Prefetch distances (rebuild, poll): object Lead entries ahead, its referents Parts (unmeasured; beyond Ahead's)
    private const int Lead = 24, Parts = 12;

    private static readonly Parking[] Parkings = [new(), new(), new(), new()]; // by slot, main thread only

    // Renderers of Opaque's parked utils at its last rebuild; _owners counts changes
    private static HashSet<AnimatableRenderer> _owned = new(ReferenceEqualityComparer.Instance),
        _spare = new(ReferenceEqualityComparer.Instance);

    private static Type? _utilType;
    private static MethodBase? _trigger; // ClientEventManager.TriggerRenderStage
    internal static MethodBase? Trigger => _trigger;
    private static Parking? _current; // the stage being dispatched, when it parks anything
    private static List<RenderHandler>? _list; // its list, its bytes and the list's _version they were polled for
    private static byte[] _bits = [];
    private static int _edits, _owners, _frame;
    private static bool _parks, _crossed; // the last: another mod's transpiler on the loop (Recheck)

    internal static bool Parks => _parks; // both loops of TriggerRenderStage ask Skip

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    private static extern ref int Edits(List<RenderHandler> list);

    // First question of the transpiled loops and ModTimes' replay: entry found idle at stage start. Another list (a
    // nested stage polled its own) answers no.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Skip(List<RenderHandler> list, int i)
    {
        var bits = _bits;
        if ((uint)i >= (uint)bits.Length || bits[i] == 0 || !ReferenceEquals(list, _list) || Edits(list) != _edits)
            return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // StartAnimation: a util started mid-stage after the poll runs from here, its renderer asked again (draws after
    // MarkBlockDirty's callback in a later Before). The next stage polls anew.
    internal static void Woken(AnimationUtil __instance, bool __result)
    {
        if (!__result || _current is not { } parking || !NotNull(__instance) || __instance.GetType() != _utilType ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        _ = Assert(parking.Unpark(__instance, __instance.renderer) <= 2); // each registers once per stage
    }

    // Leaves no engine object behind (Install; Stop, before the world goes)
    internal static void Clear()
    {
        (_walked, _list, _current, _owners, _frame) = (null, null, null, 0, 0);
        _bits = [];
        _owned.Clear();
        _spare.Clear();
        for (var i = 0; i < Stages; i++) Parkings[i] = new();
    }

    // Stage start: rebuild unless of this list as is (outside Opaque, also the owned set), poll, hand bytes to Skip
    private static bool Poll(List<RenderHandler> list, EnumRenderStage stage)
    {
        var slot = stage switch
        {
            EnumRenderStage.ShadowFar => 0,
            EnumRenderStage.ShadowNear => 1,
            EnumRenderStage.Opaque => OpaqueSlot,
            EnumRenderStage.OIT => 3,
            _ => -1
        };
        if (slot < 0) return false;
        var parking = Parkings[slot];
        parking.Polled = _frame;
        if (!ReferenceEquals(parking.Handlers, list) || parking.Stamp != Edits(list) ||
            (slot != OpaqueSlot && parking.Owners != _owners))
        {
            if (parking.Built == _frame) return false; // changed again within the frame: Frame decides, call by call
            Rebuild(parking, list, slot == OpaqueSlot);
        }

        if (parking.UtilCount + parking.RendererCount == 0 || !PollRenderers(parking) || !PollUtils(parking)) return false;
        (_list, _edits, _current) = (list, parking.Stamp, parking);
        _bits = parking.Bits;
        return true;
    }

    private static void Rebuild(Parking parking, List<RenderHandler> list, bool opaque)
    {
        var items = CollectionsMarshal.AsSpan(list);
        var count = Math.Min(items.Length, MaxParked);
        parking.Reset(count);
        var (renderers, utils) = (RendererKind == Kind.Renderer, _utilKind == Kind.Util && _utilType is not null);
        for (var i = 0; i < Math.Min(count, MaxParked); i++)
        {
            if (i + Lead < count) Fetch(items[i + Lead]);
            if (i + Parts < count && items[i + Parts] is { } ahead) Fetch(ahead.Renderer);
            if (items[i]?.Renderer is not { } renderer) continue;
            var type = renderer.GetType();
            if (renderers && type == typeof(AnimatableRenderer)) parking.Add(Unsafe.As<AnimatableRenderer>(renderer), i);
            else if (utils && type == _utilType) parking.Add(Unsafe.As<AnimationUtil>(renderer), i);
        }

        if (opaque) Own(parking);
        parking.KeepOwned(_owned);
        (parking.Handlers, parking.Stamp, parking.Owners, parking.Built) = (list, Edits(list), _owners, _frame);
    }

    // Counted as a change only when the set differs
    private static void Own(Parking parking)
    {
        var (next, changed) = (_spare, false);
        next.Clear();
        for (var k = 0; k < Math.Min(parking.UtilCount, MaxParked); k++)
            if (parking.Utils[k]?.renderer is { } shown && shown.GetType() == typeof(AnimatableRenderer) && next.Add(shown))
                changed |= !_owned.Contains(shown);
        changed |= next.Count != _owned.Count;
        (_owned, _spare) = (next, _owned);
        _spare.Clear(); // the old set would hold renderers of block entities unloaded since
        if (changed) _owners++;
    }

    // All parkings, or those unpolled last frame, release objects; the owned set goes with Opaque's, as a change.
    private static void Release(bool all)
    {
        var stale = _frame - 1;
        for (var slot = 0; slot < Parkings.Length; slot++)
        {
            var parking = Parkings[slot];
            if (!parking.Holds || (!all && parking.Polled >= stale)) continue;
            parking.Reset(0);
        }

        if (Parkings[OpaqueSlot].Holds || _owned.Count + _spare.Count == 0) return;
        _owned.Clear();
        _spare.Clear();
        _owners++;
    }

    // Idle while not rendering: one field, on its own line
    private static bool PollRenderers(Parking parking)
    {
        var (renderers, at, bits) = (parking.Renderers, parking.RendererAt, parking.Bits);
        var (count, shownAt) = (parking.RendererCount, _shownAt);
        if (!Assert(count <= renderers.Length && count <= at.Length)) return false;
        for (var k = 0; k < Math.Min(count, MaxParked); k++)
        {
            if (k + Lead < count) FetchAt(renderers[k + Lead], shownAt);
            bits[at[k]] = renderers[k].ShouldRender ? (byte)0 : (byte)1;
        }

        return true;
    }

    private static bool PollUtils(Parking parking)
    {
        var (utils, at, bits) = (parking.Utils, parking.UtilAt, parking.Bits);
        var (count, shownAt, countAt) = (parking.UtilCount, _shownAt, _countAt);
        if (!Assert(count <= utils.Length && count <= at.Length)) return false;
        for (var k = 0; k < Math.Min(count, MaxParked); k++)
        {
            if (k + Lead < count) Fetch(utils[k + Lead], 2);
            // what Resting reads: animator count, renderer ShouldRender, dictionary count (often on its second line)
            if (k + Parts < count && utils[k + Parts] is { } next)
            {
                FetchAt(next.animator, countAt);
                FetchAt(next.renderer, shownAt);
                Fetch(next.activeAnimationsByAnimCode, 2);
            }

            bits[at[k]] = Resting(utils[k]) ? (byte)1 : (byte)0;
        }

        return true;
    }

    // A stage's parked objects, their list places and Skip's bytes (one per entry; 0 if not parked)
    private sealed class Parking
    {
        public List<RenderHandler>? Handlers;
        public int Stamp, Owners, Built = -1, Polled, UtilCount, RendererCount;
        public AnimationUtil[] Utils = [];
        public AnimatableRenderer[] Renderers = [];
        public int[] UtilAt = [], RendererAt = [];
        public byte[] Bits = [];

        public bool Holds => Handlers is not null || UtilCount + RendererCount > 0;

        // Room for `count` entries; last build's objects released
        public void Reset(int count)
        {
            Array.Clear(Utils, 0, Math.Min(UtilCount, Utils.Length));
            Array.Clear(Renderers, 0, Math.Min(RendererCount, Renderers.Length));
            Array.Clear(Bits);
            (UtilCount, RendererCount, Handlers) = (0, 0, null);
            if (Bits.Length < count)
            {
                var size = Math.Min(MaxParked, count + count / 2); // headroom: block entities keep loading
                (Bits, Utils, Renderers, UtilAt, RendererAt) =
                    (new byte[size], new AnimationUtil[size], new AnimatableRenderer[size], new int[size], new int[size]);
            }
        }

        public void Add(AnimatableRenderer renderer, int at)
        {
            if (!Index(RendererCount, Renderers.Length) || !Index(at, Bits.Length)) return;
            (Renderers[RendererCount], RendererAt[RendererCount]) = (renderer, at);
            RendererCount++;
        }

        public void Add(AnimationUtil util, int at)
        {
            if (!Index(UtilCount, Utils.Length) || !Index(at, Bits.Length)) return;
            (Utils[UtilCount], UtilAt[UtilCount]) = (util, at);
            UtilCount++;
        }

        public void KeepOwned(HashSet<AnimatableRenderer> owned)
        {
            var kept = 0;
            for (var k = 0; k < Math.Min(RendererCount, MaxParked); k++)
            {
                if (!owned.Contains(Renderers[k])) continue;
                (Renderers[kept], RendererAt[kept]) = (Renderers[k], RendererAt[k]);
                kept++;
            }

            Array.Clear(Renderers, kept, RendererCount - kept);
            RendererCount = kept;
        }

        // Util and renderer leave this stage's bytes; returns entries cleared
        public int Unpark(AnimationUtil util, AnimatableRenderer? shown)
        {
            var found = 0;
            for (var k = 0; k < Math.Min(UtilCount, MaxParked); k++)
            {
                if (!ReferenceEquals(Utils[k], util) || !Index(UtilAt[k], Bits.Length)) continue;
                Bits[UtilAt[k]] = 0;
                found++;
            }

            for (var k = 0; k < Math.Min(RendererCount, MaxParked); k++)
            {
                if (shown is null || !ReferenceEquals(Renderers[k], shown) || !Index(RendererAt[k], Bits.Length)) continue;
                Bits[RendererAt[k]] = 0;
                found++;
            }

            return found;
        }
    }
}

// Asking a renderer if idle costs a chain of dependent cache misses (handler, renderer, util's animator, renderer,
// dictionary). Frame runs in list order, so a cursor prefetches: handler Far ahead, renderer Near, util parts Next. A
// mismatched cursor stops. Parked stages prefetch in their poll instead (what reaches Frame there is mostly busy).
internal static partial class IdleAnimators
{
    private const int Far = 16, Near = 8, Next = 4, Line = 64, MaxOffset = 1024;
    private const string CountName = "activeAnimCount";

    private static List<RenderHandler>? _walked;
    private static int _cursor;

    // Offsets of the poll's fields from the object address (main thread, Install): ShouldRender and activeAnimCount, a
    // dozen references deep, mostly on the object's second or third line
    private static nint _shownAt, _countAt;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = CountName)]
    private static extern ref int ActiveCount(AnimatorBase animator);

    // Unpolled stages release parked objects: every stage while nothing parks, at Before those skipped last frame
    internal static void Staged(List<RenderHandler>[] ___renderersByStage, EnumRenderStage stage)
    {
        (_walked, _list, _current) = (null, null, null);
        _bits = [];
        var polls = Enabled && !Blocked && _parks && !_crossed;
        if (stage == EnumRenderStage.Before) _frame++;
        if (!polls || stage == EnumRenderStage.Before) Release(!polls);
        if (!Enabled || Blocked || !NotNull(___renderersByStage) || !Index((int)stage, ___renderersByStage.Length)) return;
        var list = ___renderersByStage[(int)stage];
        if (!NotNull(list)) return;
        if (!(polls && Poll(list, stage)) && Sse.IsSupported) (_walked, _cursor) = (list, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ahead(IRenderer? renderer)
    {
        if (_walked is not { } list) return;
        var items = CollectionsMarshal.AsSpan(list);
        var at = _cursor;
        if ((uint)at >= (uint)items.Length || !ReferenceEquals(items[at]?.Renderer, renderer))
        {
            _walked = null;
            return;
        }

        _cursor = at + 1;
        if (at + Far < items.Length) Fetch(items[at + Far]);
        if (at + Near < items.Length && items[at + Near] is { } near) Fetch(near.Renderer, 2);
        if (at + Next < items.Length && items[at + Next]?.Renderer is AnimationUtil util)
        {
            Fetch(util.animator);
            Fetch(util.renderer, 2);
            Fetch(util.activeAnimationsByAnimCode);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Fetch(object? item, int lines = 1)
    {
        if (item is null || !Sse.IsSupported) return;
        var address = (byte*)Unsafe.As<object, nint>(ref item);
        Sse.Prefetch0(address);
        if (lines > 1) Sse.Prefetch0(address + Line);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void FetchAt(object? item, nint at)
    {
        if (item is null || !Sse.IsSupported) return;
        Sse.Prefetch0((byte*)Unsafe.As<object, nint>(ref item) + at);
    }

    // Field offset from the object address: from the first field (StrongBox's one field in any class) plus the method
    // table pointer. Uninitialized probes: no constructor runs.
    private static void Measure()
    {
        var shown = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        _shownAt = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(shown).Value,
            ref Unsafe.As<bool, byte>(ref shown.ShouldRender)) + IntPtr.Size;
        if (!Assert(_shownAt > 0 && _shownAt < MaxOffset)) _shownAt = 0;
        // only guard for the protected field (engine reads the property): a renamed field would throw out of Install
        _countAt = Assert(AccessTools.DeclaredField(typeof(AnimatorBase), CountName)?.FieldType == typeof(int))
            ? CountAt((AnimatorBase)RuntimeHelpers.GetUninitializedObject(typeof(ClientAnimator)))
            : 0;
    }

    // Apart from Measure: the accessor binds its field when compiled, which only a call does
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint CountAt(AnimatorBase probe)
    {
        var at = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(probe).Value,
            ref Unsafe.As<int, byte>(ref ActiveCount(probe))) + IntPtr.Size;
        return Assert(at > 0 && at < MaxOffset) ? at : 0;
    }
}

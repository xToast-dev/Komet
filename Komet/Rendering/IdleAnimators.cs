using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Every animating block entity registers an AnimationUtil and an AnimatableRenderer that the engine calls every frame; the bench
// world has ~2000 with 14 animating (8000 calls that return at once, mostly cache misses). Only renderers whose
// IRenderer.OnRenderFrame is one of those two engine bodies are skipped, and only while the bodies are verified (EngineShape) and
// unpatched by other mods (AnimatableCulling's prefix lets an idle renderer through).
//
// With extended debug info on (a client setting, on in the user's game) the loop also asks the platform for a GL error and marks the
// profiler after every renderer: 1263 "ShadowFar-animatable" marks in one of the user's frames, one per animatable block entity and
// nearly all idle, each a virtual call, a dictionary lookup and a clock read for a renderer that did nothing. The check and the mark
// after a renderer Frame left out are left out too: it made no GL call, and the time it did not take lands in the next mark.
//
// Frame still loads each handler and renderer to ask it. So both loops first ask Skip (IdleAnimators.Parked) for one byte per list
// entry, polled at the start of the stage: a parked idle entry costs no object at all, and Frame stays the fallback for the rest.
internal static partial class IdleAnimators
{
    // EngineShape of Seams() in Vintage Story 1.22.7
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

    // Bodies not verified, render loops not rewritten, or another mod patches the bodies
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;
    internal static bool Rewritten => _rewritten;
    internal static bool Unmarked => _unmarked; // the extended-debug loop's check and mark go through Check and Mark

    // Total while Counting.Hud (main thread)
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
        if (!NotNull(harmony) || !NotNull(trigger) || !Assert(Bodies.Length == 2) || !NotNull(Bodies[0]) ||
            !NotNull(Bodies[1])) return;
        _shaped = EngineShape.Matches(Bodies, fingerprint, nameof(IdleAnimators), logger);
        if (!_shaped) return;
        // the util type of nearly every other call; without the content assembly nothing is parked
        _utilType = AccessTools.TypeByName(UtilName);
        _utilKind = _utilType is { } util ? Classify(util) : Kind.Other;
        Measure();
        _ = NotNull(harmony.Patch(trigger, new HarmonyMethod(Staged), transpiler: new HarmonyMethod(Rewrite)));
        var start = AccessTools.DeclaredMethod(typeof(AnimationUtil), nameof(AnimationUtil.StartAnimation),
            [typeof(AnimationMetaData)]);
        if (_parks && _utilKind == Kind.Util && NotNull(start))
            _ = NotNull(harmony.Patch(start, postfix: new HarmonyMethod(Woken)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && _rewritten && Assert(Bodies.Length == 2);
        _foreign = EngineShape.Report(_logger, nameof(IdleAnimators), _foreign,
            seamed && EngineShape.Foreign(Bodies, EngineShape.Kinds.All, null, typeof(AnimatableCulling)));
        Blocked = !seamed || _foreign;
        // another mod's transpiler on the loop may sit between Skip and the call, or rebuild the loop: Frame decides alone then
        var crossed = _parks && EngineShape.Foreign([_trigger], EngineShape.Kinds.Body, null, typeof(IdleAnimators));
        if (crossed && !_crossed)
            _logger?.Notification(
                "Komet IdleAnimators: another mod rewrites TriggerRenderStage, idle animatables are asked one by one");
        _crossed = crossed;
    }

    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame),
                [typeof(float), typeof(EnumRenderStage)]),
            AccessTools.DeclaredMethod(typeof(AnimationUtil), nameof(AnimationUtil.OnRenderFrame),
                [typeof(float), typeof(EnumRenderStage)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // Both render loops (extendedDebugInfo and plain) call through Frame; any other shape keeps the engine's IL. The debug loop's one
    // CheckGlError and one Mark, between the two calls, go through Check and Mark, or stay as they are when not found just there.
    // Then both loop bodies open with Skip, when both loops are the engine's.
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
        if (!NotNull(check) || !NotNull(mark) || !Assert(first >= 0 && second > first)) return false;
        var (at, by) = (Il.Single(code, c => c.Calls(check)), Il.Single(code, c => c.Calls(mark)));
        if (!(first < at && at < by && by < second)) return false;
        var self = typeof(IdleAnimators);
        return Il.Substitute(code, at, AccessTools.DeclaredMethod(self, nameof(Check))) &&
               Il.Substitute(code, by, AccessTools.DeclaredMethod(self, nameof(Mark)));
    }

    // Both loops ask Skip, with the list and the index, before anything else and step on to the increment when it answers yes. The
    // later loop is changed first, so its insert shifts nothing the other one points at.
    private static bool Gate(List<CodeInstruction> code, ILGenerator generator, int first, int second)
    {
        var skip = AccessTools.DeclaredMethod(typeof(IdleAnimators), nameof(Skip), [typeof(List<RenderHandler>), typeof(int)]);
        var (item, count) = (AccessTools.Method(typeof(List<RenderHandler>), "get_Item", [typeof(int)]),
            AccessTools.PropertyGetter(typeof(List<RenderHandler>), nameof(List<>.Count)));
        if (!NotNull(skip) || !NotNull(item) || !NotNull(count) || !Assert(first >= 0 && second > first)) return false;
        var (debug, plain) = (Loop(code, first, item, count), Loop(code, second, item, count));
        if (debug.Head < 0 || plain.Head < 0 || debug.Step >= plain.Head) return false;
        return Enter(code, generator, plain, skip) && Enter(code, generator, debug, skip);
    }

    // The loop around the Frame call at `call`. Its body opens by loading the list and the index, get_Item, the handler's Renderer and
    // the two arguments, and carries the label the closing blt jumps back to. It ends with the index's increment (load, add one,
    // store) right before the test, which loads the index and the list's Count and branches back. (-1, -1) for any other shape.
    private static (int Head, int Step) Loop(List<CodeInstruction> code, int call, MethodInfo item, MethodInfo count)
    {
        const int Body = 6, Tail = 7;
        (int, int) none = (-1, -1);
        if (!NotNull(code) || !NotNull(item) || !NotNull(count) || !Index(call, code.Count) || call < Body) return none;
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

    // In front of the body: load the list and the index (as the body does), call Skip, on yes branch to the increment. The body hands
    // its label (the loop's way back in) to the first load.
    private static bool Enter(List<CodeInstruction> code, ILGenerator generator, (int Head, int Step) loop, MethodInfo skip)
    {
        if (!Index(loop.Head + 1, code.Count) || !Index(loop.Step, code.Count) || !Assert(loop.Head < loop.Step))
            return false;
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

    // Frame's question, which ModTimes' replay asks too: the renderer is left out (and counted)
    internal static bool TrySkip(IRenderer? renderer)
    {
        Ahead(renderer);
        if (!Enabled || Blocked || !Idle(renderer)) return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // A null platform or profiler throws as the engine's callvirt did, unless the renderer was left out
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

    // AnimationUtil.OnRenderFrame does nothing without animator or renderer, else steps while an animation runs and stops a
    // render once none does
    private static bool Resting(AnimationUtil util)
    {
        if (util is not { animator: { } animator, renderer: { } shown }) return true;
        if (util.activeAnimationsByAnimCode is not { Count: 0 } || animator.ActiveAnimationCount != 0) return false;
        return !shown.ShouldRender || StopTriggered(util);
    }

    // A type counts when its IRenderer.OnRenderFrame is one of the two engine bodies (not re-implemented); decided once per type. The
    // two types nearly every call comes with alternate in Opaque, past the one-entry cache, so they are answered first.
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
        if (!NotNull(type) || !typeof(IRenderer).IsAssignableFrom(type) || !Assert(Bodies.Length == 2))
            return Kind.Other;
        var map = type.GetInterfaceMap(typeof(IRenderer));
        var slot = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IRenderer.OnRenderFrame));
        if (!Index(slot, map.TargetMethods.Length)) return Kind.Other;
        var target = map.TargetMethods[slot].MethodHandle.Value; // the same body whichever type reflected it
        if (target == Bodies[0]?.MethodHandle.Value) return Kind.Renderer;
        return target == Bodies[1]?.MethodHandle.Value ? Kind.Util : Kind.Other;
    }
}

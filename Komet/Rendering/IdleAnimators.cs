using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Every block entity that can animate (chests, doors, trapdoors, querns, ruin machinery) registers an AnimationUtil for Opaque and an
// AnimatableRenderer for Opaque, ShadowFar and ShadowNear (and OIT), and the engine calls each every frame, animating or not. The
// bench world has about 2000 of them with 14 animating: 8000 calls a frame that return at once, mostly cache misses behind an
// interface call. The render loops now ask first and leave out the call when OnRenderFrame would do nothing: an AnimatableRenderer
// with ShouldRender false, an AnimationUtil without animator or renderer, or with no animation running and no render left to stop.
// The engine checks the pause in between, which only ever makes the util do less. Only renderers whose IRenderer.OnRenderFrame is one
// of those two engine bodies are asked, and only while the bodies are the verified ones (EngineShape) and no other mod patches them
// (AnimatableCulling's prefix lets an idle renderer through to the engine's own return); every other renderer is called as before.
internal static class IdleAnimators
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x4D20A29C71A4C070UL;

    private const int MaxInstructions = 256, Sites = 2, MaxTypes = 256;

    private static readonly Dictionary<Type, Kind> Kinds = []; // main thread only
    private static readonly MethodBase?[] Bodies = Seams();
    private static Type? _lastType;
    private static Kind _lastKind;
    private static ILogger? _logger;
    private static bool _shaped, _foreign, _rewritten;

    public static bool Enabled { get; set; } = true;

    // The engine's bodies are not the ones verified, the render loops were not rewritten, or another mod patches the bodies
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;
    internal static bool Rewritten => _rewritten;

    // Calls left out, a total while Counting.Hud (main thread)
    public static long Skipped { get; private set; }

    private enum Kind : byte { Other, Renderer, Util }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "stopRenderTriggered")]
    private static extern ref bool StopTriggered(AnimationUtil util);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _logger, _foreign, _rewritten) = (false, true, logger, false, false);
        (_lastType, _lastKind) = (null, Kind.Other);
        Kinds.Clear();
        var trigger = AccessTools.DeclaredMethod(typeof(ClientEventManager),
            nameof(ClientEventManager.TriggerRenderStage), [typeof(EnumRenderStage), typeof(float)]);
        if (!NotNull(harmony) || !NotNull(trigger) || !Assert(Bodies.Length == 2) || !NotNull(Bodies[0]) ||
            !NotNull(Bodies[1])) return;
        _shaped = EngineShape.Matches(Bodies, fingerprint, nameof(IdleAnimators), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(trigger, transpiler: new HarmonyMethod(Rewrite)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && _rewritten && Assert(Bodies.Length == 2);
        _foreign = EngineShape.Report(_logger, nameof(IdleAnimators), _foreign,
            seamed && EngineShape.Foreign(Bodies, EngineShape.Kinds.All, null, typeof(AnimatableCulling)));
        Blocked = !seamed || _foreign;
    }

    // The two bodies whose early return a left-out call stands for
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

    // Both render loops (the one with extendedDebugInfo and the plain one) call through Frame; any other shape keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten = false;
        var (call, frame) = (AccessTools.Method(typeof(IRenderer), nameof(IRenderer.OnRenderFrame)),
            AccessTools.Method(typeof(IdleAnimators), nameof(Frame)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(call) || !NotNull(frame))
            return code;
        if (!Assert(Il.Count(code, c => c.Calls(call)) == Sites)) return code;
        var done = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(call) && Il.Substitute(code, i, frame)) done++;
        _rewritten = done == Sites; // a site left as it was still calls the renderer, as Frame does while blocked
        return code;
    }

    // Stands in for renderer.OnRenderFrame(dt, stage) in the render loops
    internal static void Frame(IRenderer renderer, float dt, EnumRenderStage stage)
    {
        if (Enabled && !Blocked && Idle(renderer))
        {
            if (Counting.Hud) Skipped++;
            return;
        }

        renderer.OnRenderFrame(dt, stage); // a null renderer throws as the engine's callvirt did
    }

    // Whether the renderer's OnRenderFrame would return without doing anything
    internal static bool Idle(IRenderer? renderer) => KindOf(renderer?.GetType()) switch
    {
        Kind.Renderer => !Unsafe.As<AnimatableRenderer>(renderer)!.ShouldRender,
        Kind.Util => IdleUtil(Unsafe.As<AnimationUtil>(renderer)!),
        _ => false
    };

    // AnimationUtil.OnRenderFrame: nothing without an animator or a renderer; otherwise it steps the animator while an animation runs
    // and stops a render that is still on once none does
    private static bool IdleUtil(AnimationUtil util)
    {
        var (animator, renderer, active) = (util.animator, util.renderer, util.activeAnimationsByAnimCode);
        if (animator is null || renderer is null) return true;
        return active is not null && active.Count == 0 && animator.ActiveAnimationCount == 0 &&
               (!renderer.ShouldRender || StopTriggered(util));
    }

    // A type counts when the IRenderer.OnRenderFrame it runs is one of the two engine bodies: an AnimatableRenderer or AnimationUtil
    // that does not implement the interface again. Decided once per type.
    private static Kind KindOf(Type? type)
    {
        if (type is null) return Kind.Other;
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

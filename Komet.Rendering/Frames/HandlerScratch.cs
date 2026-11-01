using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Common;

namespace Komet.Rendering;

// Delegate garbage the engine makes per call, the largest left while turning on the spot (trace 2026-10-28, 30 s, 326 MB):
// the climate and wind events copy their handler list on every trigger (Delegate.GetInvocationList, 26 MB, mostly the
// cloud renderer's climate asks), and every particle's collision step makes a new lambda over its ParticlePhysics for
// WalkBlocks (14 MB, on the main thread). A multicast delegate never changes and the triggers only read the list, so the
// list is kept per delegate; the lambda captures only its ParticlePhysics, so one is kept per instance. Each rewrite
// takes its one call; an engine with another shape keeps its own body. The list cache switches with FrameScratch.
internal static class HandlerScratch
{
    private const int MaxInstructions = 4096;

    private static readonly ConditionalWeakTable<Delegate, Delegate[]> Lists = [];
    private static readonly ConditionalWeakTable<object, Action<Block, int, int, int>> Walks = [];
    private static MethodInfo? _walk; // ParticlePhysics' WalkBlocks lambda, set by the rewrite before it calls Walk

    private static readonly Rewrites Done = new();

    public static int Rewritten => Done.Count; // of the three

    internal static MethodBase?[] Seams() =>
    [
        AccessTools.DeclaredMethod(typeof(EventManager), nameof(EventManager.TriggerOnGetClimate)),
        AccessTools.DeclaredMethod(typeof(EventManager), nameof(EventManager.TriggerOnGetWindSpeed)),
        AccessTools.DeclaredMethod(typeof(ParticlePhysics), nameof(ParticlePhysics.UpdateMotion))
    ];

    public static void Install(Harmony harmony)
    {
        Done.Clear();
        var seams = Seams();
        if (!NotNull(harmony)) return;
        for (var i = 0; i < Math.Min(seams.Length, 3); i++)
            if (seams[i] is MethodInfo seam)
                _ = harmony.Patch(seam, transpiler: new HarmonyMethod(AccessTools.Method(typeof(HandlerScratch),
                    i < 2 ? nameof(RewriteList) : nameof(RewriteWalk))));
    }

    // The trigger's GetInvocationList becomes Invocations
    internal static List<CodeInstruction> RewriteList(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var list = AccessTools.Method(typeof(Delegate), nameof(Delegate.GetInvocationList));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(list)) return code;
        Done.Mark(original, Il.SubstituteSingle(code, c => c.operand is MethodInfo m && m == list,
            AccessTools.Method(typeof(HandlerScratch), nameof(Invocations))));
        return code;
    }

    // UpdateMotion's new Action(this, lambda) becomes Walk(this, lambda)
    internal static List<CodeInstruction> RewriteWalk(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo { DeclaringType: var t } &&
                                      t == typeof(Action<Block, int, int, int>));
        if (at < 1 || code[at - 1].opcode != OpCodes.Ldftn || code[at - 1].operand is not MethodInfo { IsStatic: false } lambda ||
            lambda.DeclaringType != typeof(ParticlePhysics)) return code;
        _walk = lambda;
        Done.Mark(original, Il.Substitute(code, at, AccessTools.Method(typeof(HandlerScratch), nameof(Walk))));
        return code;
    }

    // The handlers' list, the same array while the delegate is the same (the engine's triggers only read it)
    internal static Delegate[] Invocations(Delegate handlers)
    {
        if (!FrameScratch.Enabled || !NotNull(handlers)) return handlers.GetInvocationList();
        if (Lists.TryGetValue(handlers, out var list)) return list;
        list = handlers.GetInvocationList();
        Lists.AddOrUpdate(handlers, list);
        return list;
    }

    // The WalkBlocks lambda over target, made once per ParticlePhysics: the engine's method and target, so the same
    // whether FrameScratch is on or not (made anew each call would be a reflection call per particle step)
    internal static Action<Block, int, int, int> Walk(object target, IntPtr method)
    {
        var walk = _walk!;
        _ = Assert(method != IntPtr.Zero);
        if (Walks.TryGetValue(target, out var made)) return made;
        made = walk.CreateDelegate<Action<Block, int, int, int>>(target);
        Walks.AddOrUpdate(target, made);
        return made;
    }
}

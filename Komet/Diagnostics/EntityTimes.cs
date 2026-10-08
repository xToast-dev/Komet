using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// What the client's entities cost the render thread, the collector left out. The frame profile books entity work to marks that also
// take every GC pause landing in them: in the bench's spike frames esr-afteranim, entityAgent-ticking and esr-beforeanim, when long,
// were the frame's GC pause plus at most 2 ms nearly every time. While the HUD counts, each entity's game tick
// (ClientSystemEntities.OnGameTick), animation frame and render preparation (SystemRenderEntities.OnBeforeRender) is timed less the
// pause time inside it, and the slowest of each is kept with its entity code, so first-time work (a shape, an animation, a sound)
// names its entity. Otherwise the calls go straight through: the same virtual call on the same arguments as the engine's.
internal static class EntityTimes
{
    public const int Parts = 3, Tick = 0, Animation = 1, Prepare = 2;
    private const int TickSite = 1, FrameSites = 2, AllSites = TickSite | FrameSites;

    private static readonly long[] Totals = new long[Parts]; // ticks, while Counting.Hud (main thread)
    private static readonly double[] Worst = new double[Parts];
    private static readonly string[] WorstCodes = ["", "", ""];
    private static int _sites;

    public static bool Installed => _sites == AllSites;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "entity")]
    private static extern ref Entity Owner(AnimationManager manager);

    public static long Ticks(int part) => Index(part, Parts) ? Totals[part] : 0;

    // Since ResetPeaks: one entity's slowest call and its code
    public static double SlowestMs(int part) => Index(part, Parts) ? Worst[part] : 0;
    public static string SlowestCode(int part) => Index(part, Parts) ? WorstCodes[part] : "";

    public static void ResetPeaks()
    {
        _ = Assert(Worst.Length == Parts) && Assert(WorstCodes.Length == Parts);
        Array.Clear(Worst);
        Array.Fill(WorstCodes, "");
    }

    internal static void Clear()
    {
        Array.Clear(Totals);
        ResetPeaks();
        _ = Assert(Totals[Tick] == 0);
    }

    public static void Install(Harmony harmony)
    {
        _sites = 0;
        Clear();
        var (tick, frame) = (AccessTools.DeclaredMethod(typeof(ClientSystemEntities), "OnGameTick", [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]));
        if (!NotNull(harmony) || !NotNull(tick) || !NotNull(frame)) return;
        _ = NotNull(harmony.Patch(tick, transpiler: new HarmonyMethod(RewriteTick)));
        _ = NotNull(harmony.Patch(frame, transpiler: new HarmonyMethod(RewriteFrame)));
    }

    // The one Entity.OnGameTick call goes through Timed; any other shape keeps the engine's IL
    internal static List<CodeInstruction> RewriteTick(IEnumerable<CodeInstruction> instructions)
    {
        _sites &= ~TickSite;
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        var at = Il.Single(code, c => c.Calls(AccessTools.Method(typeof(Entity), nameof(Entity.OnGameTick))));
        if (at >= 0 && Il.Substitute(code, at, AccessTools.DeclaredMethod(typeof(EntityTimes), nameof(Ticked))))
            _sites |= TickSite;
        _ = Assert((_sites & ~AllSites) == 0);
        return code;
    }

    // The one IAnimationManager.OnClientFrame and the one EntityRenderer.BeforeRender call, both or neither
    internal static List<CodeInstruction> RewriteFrame(IEnumerable<CodeInstruction> instructions)
    {
        _sites &= ~FrameSites;
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        var (animate, before) = (AccessTools.Method(typeof(IAnimationManager), nameof(IAnimationManager.OnClientFrame)),
            AccessTools.Method(typeof(EntityRenderer), nameof(EntityRenderer.BeforeRender)));
        var (a, b) = (Il.Single(code, c => c.Calls(animate)), Il.Single(code, c => c.Calls(before)));
        var self = typeof(EntityTimes);
        if (a < 0 || b < 0 || !Il.Substitute(code, a, AccessTools.DeclaredMethod(self, nameof(Animated)))) return code;
        if (Il.Substitute(code, b, AccessTools.DeclaredMethod(self, nameof(Prepared)))) _sites |= FrameSites;
        else (code[a].opcode, code[a].operand) = (OpCodes.Callvirt, animate);
        _ = Assert((_sites & ~AllSites) == 0);
        return code;
    }

    // A null argument throws as the engine's callvirt did
    internal static void Ticked(Entity entity, float dt)
    {
        if (!Counting.Hud)
        {
            entity.OnGameTick(dt);
            return;
        }

        var (start, pause) = (Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration());
        entity.OnGameTick(dt);
        Book(Tick, start, pause, entity);
    }

    internal static void Animated(IAnimationManager manager, float dt)
    {
        if (!Counting.Hud)
        {
            manager.OnClientFrame(dt);
            return;
        }

        var (start, pause) = (Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration());
        manager.OnClientFrame(dt);
        Book(Animation, start, pause, manager is AnimationManager known ? Owner(known) : null);
    }

    internal static void Prepared(EntityRenderer renderer, float dt)
    {
        if (!Counting.Hud)
        {
            renderer.BeforeRender(dt);
            return;
        }

        var (start, pause) = (Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration());
        renderer.BeforeRender(dt);
        Book(Prepare, start, pause, renderer.entity);
    }

    // The pause is the runtime's own clock, so the difference is clamped: a call is never booked below zero
    internal static void Book(int part, long start, TimeSpan pause, Entity? entity)
    {
        var paused = (long)((GC.GetTotalPauseDuration() - pause).TotalSeconds * Stopwatch.Frequency);
        var spent = Math.Max(0, Stopwatch.GetTimestamp() - start - Math.Max(0, paused));
        if (!Index(part, Parts) || !Assert(start > 0)) return;
        Totals[part] += spent;
        var ms = FrameClock.ToMs(spent);
        if (ms > Worst[part]) (Worst[part], WorstCodes[part]) = (ms, entity?.Code?.ToShortString() ?? "");
    }
}

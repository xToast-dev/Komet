using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// SystemRenderEntities.OnBeforeRender walks every entity renderer each frame, and EntityShapeRenderer.BeforeRender tesselates every
// visible entity whose shape is not fresh, all of them in that one frame. Only the main-thread half is in it: Entity.OnTesselation
// composes the shape (clone, gear and skin parts, step-parenting), inits it for animation and loads the animator, textures are
// resolved and skins rendered into the atlas; the mesh is built on the thread pool. Entities come due in bursts: shapeFresh starts
// false and is cleared on load and spawn, so a chunk full of animals coming online or the camera turning toward a herd tesselates
// all of them at once, and so do gear and skin changes.
//
// So the one read of ShapeFresh in BeforeRender becomes Fresh(entity), which says "fresh" (skip this frame) once the tesselations of
// the frame have used up the budget. The first one in a frame always runs, so does the local player's (its gear must change on the
// frame it is put on), and so does any entity put off MaxWaitFrames times, counted in frames it asked. shapeFresh itself stays false,
// so the entity asks again next frame. A deferred entity is in a state vanilla already produces for any entity outside the frustum or
// whose chunk is not drawn yet: not tesselated, no mesh, Animator null, which BeforeRender, the render passes and
// AnimationManager.OnClientFrame all handle; one that re-tesselates keeps its old mesh and animator together until its turn. The
// TesselateShape call it guards is timed in place, so the budget counts only tesselation; direct calls (the character dialog) are left
// alone. Millis 0 hands everything back to the engine. A prefix on OnBeforeRender starts the frame and sets the 'esr-pre' mark, so the
// Before-stage renderers ahead of the entity loop no longer land in the first entity's esr-tesseleateshape. Entities are known by id
// and code only, so no static keeps a world alive after it is left, not even one whose TesselateShape threw.
internal static class EntityTessBudget
{
    public const int Engine = 0, MaxMillis = 50, DefaultMillis = 4, MaxWaitFrames = 30;
    private const int MaxWaiting = 4096;

    private const string MarkName = "esr-tesseleateshape",
        PreMark = "esr-pre",
        Renderer = "Vintagestory.GameContent.EntityShapeRenderer";

    private const long NoEntity = long.MinValue;
    private static readonly Dictionary<long, int> Waiting = []; // entity id → how often it has been put off
    private static long _frame, _spent, _budget, _began, _self = NoEntity; // _self: the local player's entity id

    // Of the entity whose tesselation Begin and End time: its code, never the entity
    private static AssetLocation? _code;

    public static int Millis { get; set; } = DefaultMillis;
    public static bool Substituted { get; private set; } // the ShapeFresh read was found and replaced
    public static long Tesselations { get; private set; } // totals while Counting.Hud, main thread
    public static long Deferred { get; private set; } // entity-frames put off
    public static long Ticks { get; private set; } // in TesselateShape from BeforeRender

    public static long MostWaited
    {
        get;
        private set;
    } // frames put off, of an entity that has since tesselated; peaks since ResetPeaks

    public static double WorstFrameMs { get; private set; } // of those in one frame
    public static double SlowestMs { get; private set; } // one entity's tesselation
    public static string SlowestCode { get; private set; } = ""; // and whose it was: nine deer or one player

    public static void ResetPeaks()
    {
        _ = Assert(MostWaited >= 0) && Assert(WorstFrameMs >= 0);
        (MostWaited, WorstFrameMs, SlowestMs, SlowestCode) = (0, 0, 0, "");
    }

    // No frame, nothing waiting, nothing counted
    internal static void Reset()
    {
        ResetPeaks();
        (Tesselations, Deferred, Ticks) = (0, 0, 0);
        (_frame, _spent, _budget, _began, _self, _code) = (0, 0, 0, 0, NoEntity, null);
        Waiting.Clear();
        _ = Assert(Waiting.Count == 0);
    }

    public static void Install(Harmony harmony, ILogger? logger)
    {
        Reset();
        Substituted = false;
        if (!NotNull(harmony)) return;
        var frame = AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]);
        var prefix = new HarmonyMethod(typeof(EntityTessBudget), nameof(Frame));
        if (NotNull(frame) && Assert(Il.Binds(frame, prefix.method))) _ = NotNull(harmony.Patch(frame, prefix));
        if (Before() is { } before)
            _ = NotNull(harmony.Patch(before,
                transpiler: new HarmonyMethod(typeof(EntityTessBudget), nameof(Substitute))));
        if (!Substituted)
            logger?.Warning(
                "Komet EntityTessBudget: EntityShapeRenderer.BeforeRender is not the method it was written for; " +
                "every entity tessellates at once, as in the engine");
    }

    // EntityShapeRenderer lives in VSEssentials, which Komet does not reference; declared there, not inherited
    internal static MethodInfo? Before()
    {
        var type = AccessTools.TypeByName(Renderer);
        return type is null
            ? null
            : AccessTools.DeclaredMethod(type, nameof(EntityRenderer.BeforeRender), [typeof(float)]);
    }

    // Harmony injects ClientSystem.game by name
    internal static void Frame(ClientMain ___game)
    {
        ScreenManager.FrameProfiler?.Mark(PreMark); // what the renderers before this one took, not the first entity
        Start(___game?.EntityPlayer);
    }

    // A frame of the entity loop begins; self is the local player, whose tesselation is never put off
    internal static void Start(Entity? self)
    {
        if (Counting.Hud && _spent > 0) WorstFrameMs = Math.Max(WorstFrameMs, ToMs(_spent));
        (_frame, _spent, _self, _code) = (_frame + 1, 0, self?.EntityId ?? NoEntity, null);
        _budget = Assert(Millis is >= Engine and <= MaxMillis) ? Millis * Stopwatch.Frequency / 1000 : 0;
        if (_budget == 0 && Waiting.Count > 0) Waiting.Clear();
    }

    // Stands in for entity.ShapeFresh in BeforeRender: true skips the tesselation this frame. A null entity throws as the engine's
    // callvirt did.
    internal static bool Fresh(Entity entity)
    {
        if (entity.ShapeFresh) return true;
        _code = entity.Code;
        if (_budget <= 0 || !Assert(_spent >= 0)) return false;
        var id = entity.EntityId;
        var waited = Waiting.Count > 0 && Waiting.TryGetValue(id, out var times) ? times : 0;
        if (_spent < _budget || id == _self || waited >= MaxWaitFrames)
        {
            if (waited == 0) return false;
            _ = Waiting.Remove(id);
            if (Counting.Hud) MostWaited = Math.Max(MostWaited, waited);
            return false;
        }

        // Counted in frames it asked and was put off, not frames since: one that left the view while waiting has not waited
        if (waited == 0 && Waiting.Count >= MaxWaiting)
            Waiting.Clear(); // despawned while waiting: start over rather than grow
        Waiting[id] = waited + 1;
        _code = null; // nothing of it to time
        if (Counting.Hud) Deferred++;
        return Assert(_frame > 0) && Assert(Waiting.Count <= MaxWaiting);
    }

    // Around the TesselateShape() call in BeforeRender. One that throws leaves _began set; the next Begin overwrites it.
    internal static void Begin()
    {
        _began = Stopwatch.GetTimestamp();
        _ = Assert(_began > 0);
    }

    internal static void End()
    {
        if (_began == 0) return;
        var spent = Stopwatch.GetTimestamp() - _began;
        _began = 0;
        if (!Assert(spent >= 0)) return;
        _spent += spent;
        var code = _code;
        _code = null;
        if (!Counting.Hud) return;
        (Tesselations, Ticks) = (Tesselations + 1, Ticks + spent);
        var ms = ToMs(spent);
        if (ms > SlowestMs) (SlowestMs, SlowestCode) = (ms, code?.ToShortString() ?? "");
    }

    private static double ToMs(long ticks)
    {
        return Assert(ticks >= 0) && Assert(Stopwatch.Frequency > 0) ? ticks * 1000.0 / Stopwatch.Frequency : 0;
    }

    // The one ldstr "esr-tesseleateshape", the one read of ShapeFresh and the one TesselateShape() call, read before call before
    // mark: the read becomes Fresh (the same Entity in, the same bool out) in place, so labels and blocks stay where they were, and
    // the call is bracketed by Begin and End. Any other count or order: the engine's IL, untouched.
    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        Substituted = false;
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        var fresh = AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.ShapeFresh));
        var tesselate = AccessTools.TypeByName(Renderer) is { } type
            ? AccessTools.DeclaredMethod(type, "TesselateShape", Type.EmptyTypes)
            : null;
        var self = typeof(EntityTessBudget);
        var (mine, begin, end) = (AccessTools.DeclaredMethod(self, nameof(Fresh)),
            AccessTools.DeclaredMethod(self, nameof(Begin)),
            AccessTools.DeclaredMethod(self, nameof(End)));
        if (fresh is null || tesselate is null || !NotNull(mine) || !NotNull(begin) || !NotNull(end)) return code;
        var mark = Il.Single(code, c => c.opcode == OpCodes.Ldstr && c.operand is MarkName);
        var read = Il.Single(code, c => c.Calls(fresh));
        var call = Il.Single(code, c => c.Calls(tesselate));
        if (read < 0 || call <= read || mark <= call || code[call].labels.Count > 0 ||
            !Il.Substitute(code, read, mine)) return code;
        code.Insert(call + 1, new CodeInstruction(OpCodes.Call, end));
        code.Insert(call,
            new CodeInstruction(OpCodes.Call, begin)); // after the ldarg.0 the call consumes: stack neutral
        Substituted = true;
        return code;
    }
}

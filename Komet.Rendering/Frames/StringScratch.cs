using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Rendering;

// Strings the engine makes anew at every call, the most made per frame on the main thread after the profiler's (trace
// 2026-10-31, user world, 20 s): the player's inventory names ("hotbar-" + uid at every ActiveHotbarSlot, the gear's
// "character-" + uid for the night vision's look, ~10 MB), every ClientSettings read lowering its key (vsyncMode,
// ssaoQuality, gammaLevel: ~3 MB), the held animations' "-fp" names, the render stage's GL check text and the slot
// backgrounds' texture keys. In these methods each String.Concat of two or three strings becomes Joined, kept per
// instances joined (literals and fields: the same ones every frame), and the settings' ToLowerInvariant becomes Lowered,
// kept per key instance. An entry is one immutable object, so a thread reads either the old one or the new; a slot
// another join takes is overwritten, a miss costs the join the engine made anyway. AnimationManager.StartAnimation
// builds the entity debug view's strings at every animation start, shown or not: there only in the debug mode that
// shows them (Entity.OnGameTick refreshes them every tick then). Switches with FrameScratch.
internal static class StringScratch
{
    private const int Slots = 1024, MaxInstructions = 4096;

    private static readonly Join?[] Joins = new Join?[Slots];
    private static readonly Join?[] Lowers = new Join?[Slots];

    private static readonly Rewrites Done = new();

    public static int Rewritten => Done.Count; // methods

    internal static MethodBase?[] Seams() =>
    [
        AccessTools.DeclaredPropertyGetter(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.ActiveHotbarSlot)),
        AccessTools.DeclaredPropertyGetter(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.OffhandHotbarSlot)),
        AccessTools.DeclaredPropertyGetter(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.MouseItemSlot)),
        AccessTools.DeclaredMethod(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.GetInventoryName), [typeof(string)]),
        AccessTools.DeclaredMethod(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.GetHotbarItemstack), [typeof(int)]),
        AccessTools.DeclaredMethod(typeof(PlayerInventoryManager), nameof(PlayerInventoryManager.GetHotbarInventory), []),
        AccessTools.DeclaredMethod(typeof(SettingsBase), nameof(SettingsBase.GetStringSetting), [typeof(string), typeof(string)]),
        AccessTools.DeclaredMethod(typeof(SettingsBase), nameof(SettingsBase.GetStringListSetting), [typeof(string), typeof(List<string>)]),
        AccessTools.DeclaredMethod(typeof(SettingsBase), nameof(SettingsBase.GetIntSetting), [typeof(string)]),
        AccessTools.DeclaredMethod(typeof(SettingsBase), nameof(SettingsBase.GetFloatSetting), [typeof(string)]),
        AccessTools.DeclaredMethod(typeof(SettingsBase), nameof(SettingsBase.GetBoolSetting), [typeof(string)]),
        AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage), [typeof(EnumRenderStage), typeof(float)]),
        AccessTools.DeclaredMethod(typeof(GuiElementItemSlotGridBase), nameof(GuiElementItemSlotGridBase.RenderInteractiveElements), [typeof(float)]),
        AccessTools.DeclaredMethod(typeof(PlayerAnimationManager), nameof(PlayerAnimationManager.IsAnimationActiveOrRunning), [typeof(string), typeof(float)]),
        AccessTools.DeclaredMethod(typeof(PlayerAnimationManager), nameof(PlayerAnimationManager.IsAnimationActive), [typeof(string[])]),
        AccessTools.DeclaredMethod(typeof(PlayerAnimationManager), nameof(PlayerAnimationManager.GetAnimationState), [typeof(string)]),
        AccessTools.DeclaredMethod(typeof(HudDialogChat), nameof(HudDialogChat.OnRenderGUI), [typeof(float)]),
        AccessTools.DeclaredMethod(typeof(AnimationManager), nameof(AnimationManager.StartAnimation), [typeof(AnimationMetaData)])
    ];

    public static void Install(Harmony harmony)
    {
        Done.Clear();
        if (!NotNull(harmony)) return;
        var rewrite = new HarmonyMethod(AccessTools.Method(typeof(StringScratch), nameof(Rewrite)));
        foreach (var seam in Seams().Bounded(32))
            if (seam is MethodInfo method)
                _ = harmony.Patch(method, transpiler: rewrite);
    }

    // Each join of two or three strings becomes Joined, each ToLowerInvariant Lowered, each UpdateDebugAttributes Debugged
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var (two, three) = (Concat(2), Concat(3));
        var lower = AccessTools.DeclaredMethod(typeof(string), nameof(string.ToLowerInvariant), []);
        var debug = AccessTools.DeclaredMethod(typeof(Entity), nameof(Entity.UpdateDebugAttributes), []);
        (MethodInfo? Engine, string Mine)[] swaps = [(two, nameof(Joined)), (three, nameof(Joined3)), (lower, nameof(Lowered)), (debug, nameof(Debugged))];
        var changed = false;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (code[i].operand is not MethodInfo called) continue;
            foreach (var (engine, mine) in swaps.Bounded(4))
                if (engine is not null && called == engine && Il.Substitute(code, i, AccessTools.Method(typeof(StringScratch), mine)))
                    changed = true;
        }

        Done.Mark(original, changed);
        return code;
    }

    private static MethodInfo? Concat(int parts) =>
        AccessTools.DeclaredMethod(typeof(string), nameof(string.Concat), [.. Enumerable.Repeat(typeof(string), parts)]);

    internal static string Joined(string? a, string? b)
    {
        if (!FrameScratch.Enabled) return string.Concat(a, b);
        var slot = (uint)HashCode.Combine(RuntimeHelpers.GetHashCode(a), RuntimeHelpers.GetHashCode(b)) % Slots;
        if (Joins[slot] is { } kept && ReferenceEquals(kept.A, a) && ReferenceEquals(kept.B, b) && kept.C is null)
            return kept.Text;
        var joined = string.Concat(a, b);
        Joins[slot] = new Join(a, b, null, joined);
        return joined;
    }

    // Concat(a, b, null) joins as Concat(a, b): an entry of either answers both
    internal static string Joined3(string? a, string? b, string? c)
    {
        if (!FrameScratch.Enabled) return string.Concat(a, b, c);
        var slot = (uint)HashCode.Combine(RuntimeHelpers.GetHashCode(a), RuntimeHelpers.GetHashCode(b),
            RuntimeHelpers.GetHashCode(c)) % Slots;
        if (Joins[slot] is { } kept && ReferenceEquals(kept.A, a) && ReferenceEquals(kept.B, b) && ReferenceEquals(kept.C, c))
            return kept.Text;
        var joined = string.Concat(a, b, c);
        Joins[slot] = new Join(a, b, c, joined);
        return joined;
    }

    // A key already lower is its own lowered string (ToLowerInvariant makes none for it): only others are kept. A null key
    // fails in the settings' lookup as it did.
    internal static string Lowered(string key)
    {
        if (!FrameScratch.Enabled || key is null) return key?.ToLowerInvariant()!;
        var slot = (uint)RuntimeHelpers.GetHashCode(key) % Slots;
        if (Lowers[slot] is { } kept && ReferenceEquals(kept.A, key)) return kept.Text;
        var lowered = key.ToLowerInvariant();
        if (!ReferenceEquals(lowered, key)) Lowers[slot] = new Join(key, null, null, lowered);
        return lowered;
    }

    // StartAnimation's `entity?.UpdateDebugAttributes()`, as StopAnimation does it: only in the entity debug mode
    internal static void Debugged(Entity entity)
    {
        if (!FrameScratch.Enabled || entity.World is not { EntityDebugMode: false }) entity.UpdateDebugAttributes();
    }

    private sealed class Join(string? a, string? b, string? c, string joined)
    {
        public string? A { get; } = a;
        public string? B { get; } = b;
        public string? C { get; } = c;
        public string Text { get; } = joined;
    }
}

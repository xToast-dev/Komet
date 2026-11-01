using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Rendering;

// Garbage the engine makes every frame for nothing kept, each small but together ~2 % of all allocations in flight: every
// GlTranslate a new double[3] (each 2D texture drawn translates twice: the hotbar, the status bars, the map), every
// dialog's OnFinalizeFrame an iterator over its composers. The same here without them: GlTranslate's overload taking
// x, y, z; the composers' table read up to its count as the iterator did. EngineShape pins the two bodies.
internal static class FrameScratch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xD274FF5C4754F8BFUL;

    private const int MaxComposers = 256;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dialogComposers")]
    private static extern ref ConcurrentSmallDictionary<string, GuiComposer>? Composers(GuiDialog.DlgComposers composers);

    // An accessor into a generic type is declared on the open definition
    private static class Table<TKey, TValue>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "contents")]
        public static extern ref DTable<TKey, TValue>? Contents(ConcurrentSmallDictionary<TKey, TValue> dictionary);
    }

    public static bool Enabled { get; set; } = true;
    public static int Patched { get; private set; } // of the two

    internal static MethodBase?[] Seams() =>
    [
        AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.GlTranslate), [typeof(double), typeof(double), typeof(double)]),
        AccessTools.DeclaredMethod(typeof(GuiDialog), nameof(GuiDialog.OnFinalizeFrame), [typeof(float)])
    ];

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        Patched = 0;
        var seams = Seams();
        if (!NotNull(harmony) || !EngineShape.Matches(seams, fingerprint, nameof(FrameScratch), logger)) return;
        string[] prefixes = [nameof(Translated), nameof(Finalized)];
        for (var i = 0; i < Math.Min(seams.Length, 2); i++)
            if (seams[i] is MethodInfo seam &&
                harmony.Patch(seam, new HarmonyMethod(AccessTools.Method(typeof(FrameScratch), prefixes[i]))) is not null)
                Patched++;
    }

    // Prefix on ClientMain.GlTranslate(x, y, z): the top of the current stack translated as the engine did
    internal static bool Translated(ClientMain __instance, double x, double y, double z)
    {
        if (!Enabled || __instance is null || (__instance.CurrentMatrixModeProjection ? __instance.PMatrix : __instance.MvMatrix) is not
            { } stack) return true;
        var top = stack.Top;
        if (!NotNull(top)) return true;
        _ = Mat4d.Translate(top, top, x, y, z);
        return false; // translated: the engine's body must not translate again
    }

    // Prefix on GuiDialog.OnFinalizeFrame (the base; overrides keep theirs): each composer's PostRender up to the count
    // the table had as it began, as the engine's iterator
    internal static bool Finalized(GuiDialog __instance, float dt)
    {
        if (!Enabled || __instance?.Composers is not { } composers) return true;
        if (Composers(composers) is not { } dictionary) return true;
        if (Table<string, GuiComposer>.Contents(dictionary) is not { } table) return false; // none: the engine's loop ran nothing
        var count = table.count;
        if (!Assert(count <= table.values.Length) || count > MaxComposers) return true;
        for (var i = 0; i < Math.Min(count, MaxComposers); i++) table.values[i].PostRender(dt);
        return false;
    }
}

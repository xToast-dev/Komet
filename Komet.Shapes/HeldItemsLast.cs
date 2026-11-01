using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// Held items are drawn in the entities' opaque pass, before any body (the batched pass after it), with blending and
// depth writes on: a pane of glass in a hand wrote its depth over the body behind it, which then failed the depth test
// and was missing through the glass (vanilla too). Here each held item outside first person is noted instead and drawn
// once all bodies are, in the opaque pass's state. First person keeps its hand field of view and draws as before. The
// model matrices are still those of the opaque pass: the batched pass reads them, it changes none.
internal static class HeldItemsLast
{
    private const string Renderer = "Vintagestory.GameContent.EntityShapeRenderer";
    private const string PlayerRenderer = "Vintagestory.GameContent.EntityPlayerShapeRenderer";
    private const int MaxHeld = 512, Sites = 2, FirstPerson = 0; // RenderMode.FirstPerson

    private static readonly List<(object Renderer, float Dt, bool Right)> Held = [];

    // RenderHeldItem calls replaced in each DoRender3DOpaque, the base's and the player's: Harmony reruns a transpiler
    // whenever another mod patches the method
    private static readonly int[] Substituted = new int[Sites];

    private static Action<object, float, bool, bool>? _render; // RenderHeldItem, virtual
    private static System.Func<object, int>? _mode; // EntityPlayerShapeRenderer.renderMode
    private static Type? _player;
    private static bool _opaque; // within OnRenderOpaque3D

    public static bool Enabled { get; set; } = true;
    public static bool Complete => Substituted[0] == Sites && Substituted[1] == Sites;

    public static void Install(Harmony harmony, ILogger? logger)
    {
        (_render, _mode, _player, _opaque) = (null, null, null, false);
        Array.Clear(Substituted);
        Held.Clear();
        if (!NotNull(harmony) || AccessTools.TypeByName(Renderer) is not { } renderer ||
            AccessTools.TypeByName(PlayerRenderer) is not { } player) return;
        var held = AccessTools.DeclaredMethod(renderer, "RenderHeldItem", [typeof(float), typeof(bool), typeof(bool)]);
        var mode = AccessTools.DeclaredField(player, "renderMode");
        var pass = AccessTools.DeclaredMethod(typeof(SystemRenderEntities), nameof(SystemRenderEntities.OnRenderOpaque3D), [typeof(float)]);
        if (held is null || mode is null || !mode.FieldType.IsEnum || pass is null) return;
        (_render, _mode, _player) = (Caller(held, renderer), Reader(mode, player), player);
        foreach (var type in new[] { renderer, player }.Bounded(Sites))
            if (AccessTools.DeclaredMethod(type, "DoRender3DOpaque", [typeof(float), typeof(bool)]) is { } opaque)
                _ = harmony.Patch(opaque, transpiler: new HarmonyMethod(AccessTools.Method(typeof(HeldItemsLast), nameof(Rewrite))));
        if (Complete) // the transpilers count what they replaced
            _ = harmony.Patch(pass, new HarmonyMethod(AccessTools.Method(typeof(HeldItemsLast), nameof(Begin))),
                new HarmonyMethod(AccessTools.Method(typeof(HeldItemsLast), nameof(Flush))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(HeldItemsLast), nameof(Settle))));
        else
            logger?.Warning("Komet HeldItemsLast: DoRender3DOpaque is not the method it was written for; held items draw before " +
                            "bodies, as in the engine");
    }

    // Each RenderHeldItem(dt, isShadowPass, right) call becomes Drawn(this, dt, isShadowPass, right)
    // Both renderers' DoRender3DOpaque, which the transpiler rewrites
    internal static MethodBase?[] Replaced =>
    [
        AccessTools.TypeByName(Renderer) is { } renderer
            ? AccessTools.DeclaredMethod(renderer, "DoRender3DOpaque", [typeof(float), typeof(bool)]) : null,
        AccessTools.TypeByName(PlayerRenderer) is { } player
            ? AccessTools.DeclaredMethod(player, "DoRender3DOpaque", [typeof(float), typeof(bool)]) : null
    ];

    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var (slot, found) = (original?.DeclaringType?.FullName == PlayerRenderer ? 1 : 0, 0);
        var drawn = AccessTools.Method(typeof(HeldItemsLast), nameof(Drawn));
        if (Assert(Il.Take(instructions, Il.MaxInstructions, out var code)))
            for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
                if (code[i].operand is MethodInfo { Name: "RenderHeldItem" } && Il.Substitute(code, i, drawn))
                    found++;
        Substituted[slot] = found;
        return code;
    }

    internal static void Begin()
    {
        Held.Clear();
        _opaque = Enabled;
    }

    // In the opaque pass, outside first person and shadows: noted for after the bodies; else drawn now
    internal static void Drawn(object renderer, float dt, bool isShadowPass, bool right)
    {
        if (_render is not { } render) return;
        if (_opaque && !isShadowPass && Held.Count < MaxHeld && !Hands(renderer))
        {
            Held.Add((renderer, dt, right));
            return;
        }

        render(renderer, dt, isShadowPass, right);
    }

    // First person draws held items in its hand field of view, within its own DoRender3DOpaque
    private static bool Hands(object renderer) =>
        _player is { } player && player.IsInstanceOfType(renderer) && _mode is { } mode && mode(renderer) == FirstPerson;

    // Postfix: the bodies are drawn; the held items in the state the opaque pass gave them, left as they found it
    internal static void Flush(ClientMain ___game)
    {
        _opaque = false;
        if (Held.Count == 0 || _render is not { } render || ___game?.api?.Render is not { } gl) return;
        gl.GlMatrixModeModelView();
        gl.GlDisableCullFace();
        gl.GlToggleBlend(true);
        gl.GLEnableDepthTest();
        try
        {
            for (var i = 0; i < Math.Min(Held.Count, MaxHeld); i++)
                render(Held[i].Renderer, Held[i].Dt, false, Held[i].Right);
        }
        finally
        {
            Held.Clear();
            gl.GlDisableCullFace(); // a held item's renderer may turn culling on
            gl.GlToggleBlend(false);
        }
    }

    // Finalizer: a throwing pass skips Flush, and the noted renderers would hold the world past its leave; void rethrows
    internal static void Settle()
    {
        _opaque = false;
        Held.Clear();
    }

    private static Action<object, float, bool, bool> Caller(MethodInfo held, Type renderer)
    {
        var method = new DynamicMethod("KometRenderHeldItem", typeof(void), [typeof(object), typeof(float), typeof(bool), typeof(bool)],
            typeof(HeldItemsLast).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, renderer);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt, held);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object, float, bool, bool>>();
    }

    private static System.Func<object, int> Reader(FieldInfo mode, Type player)
    {
        var method = new DynamicMethod("KometRenderMode", typeof(int), [typeof(object)], typeof(HeldItemsLast).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, player);
        il.Emit(OpCodes.Ldfld, mode);
        il.Emit(OpCodes.Ret);
        _ = Assert(Enum.GetUnderlyingType(mode.FieldType) == typeof(int));
        return method.CreateDelegate<System.Func<object, int>>();
    }
}

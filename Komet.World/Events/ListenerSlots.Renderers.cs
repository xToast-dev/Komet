using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// Renderers: a stage proven sorted with fixed RenderOrders takes one above its last at the end
internal static partial class ListenerSlots
{
    // Main thread: per render stage, its list's last proof; per renderer type, whether its RenderOrder is fixed
    private static readonly Order?[] Orders = new Order?[MaxStages];
    private static readonly Dictionary<Type, bool> Types = [];

    private static AccessTools.FieldRef<List<RenderHandler>, int>? _renderVersion;
    private static MethodInfo? _orderGetter, _dummyGetter;
    private static int _epoch; // moved by every write of a DummyRenderer's RenderOrder

    internal static MethodBase?[] RendererSeams()
    {
        var (manager, dummy) = (typeof(ClientEventManager), typeof(DummyRenderer));
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(manager, nameof(ClientEventManager.RegisterRenderer),
                [typeof(IRenderer), typeof(EnumRenderStage), typeof(string)]),
            AccessTools.DeclaredMethod(manager, nameof(ClientEventManager.UnregisterRenderer),
                [typeof(IRenderer), typeof(EnumRenderStage)]),
            AccessTools.DeclaredPropertyGetter(dummy, nameof(DummyRenderer.RenderOrder)),
            AccessTools.DeclaredPropertySetter(dummy, nameof(DummyRenderer.RenderOrder)),
            AccessTools.DeclaredMethod(typeof(RenderOrderReservation), nameof(RenderOrderReservation.Conflicts),
                [typeof(IRenderer), typeof(double)])
        ];
        return seams;
    }

    private static bool PatchRenderers(Harmony harmony)
    {
        (_renderVersion, _orderGetter) = (Versioned<RenderHandler>(),
            AccessTools.DeclaredPropertyGetter(typeof(IRenderer), nameof(IRenderer.RenderOrder)));
        if (Seams[Renderers] is not [{ } register, { } unregister, MethodInfo getter, { } setter, _] ||
            !NotNull(_renderVersion) || !NotNull(_orderGetter) ||
            !Binds(register, nameof(Rendering), nameof(Rendered)) ||
            !Binds(unregister, nameof(Unrendering), nameof(Unrendered)))
        {
            return false;
        }

        if (!Binds(setter, nameof(Moved))) return false;

        _dummyGetter = getter;
        _ = harmony.Patch(setter, postfix: Own(nameof(Moved)));
        _ = harmony.Patch(register, Own(nameof(Rendering)), Own(nameof(Rendered)));
        _ = harmony.Patch(unregister, Own(nameof(Unrendering)), Own(nameof(Unrendered)));
        return true;
    }

    // Postfix on DummyRenderer's RenderOrder setter: every stage is proven again
    private static void Moved() => _ = Interlocked.Increment(ref _epoch);

    private static List<RenderHandler>? StageList(ClientEventManager? manager, EnumRenderStage stage)
    {
        var at = (int)stage;
        if (manager?.renderersByStage is not { } lists || at < 0 || at >= Math.Min(lists.Length, MaxStages))
        {
            return null;
        }

        return Assert(lists.Length <= MaxStages) ? lists[at] : null;
    }

    // The stage's proof, renewed when its list changed under another hand or a DummyRenderer moved
    private static Order? Proof(List<RenderHandler> list, EnumRenderStage stage)
    {
        if (_renderVersion is not { } version) return null;

        var order = Orders[(int)stage] ??= new Order();
        var epoch = Volatile.Read(ref _epoch);
        if (ReferenceEquals(order.List, list) && order.Version == version(list) && order.Epoch == epoch) return order;

        var (sorted, unfixed, previous) = (list.Count <= MaxRenderers, 0, double.NegativeInfinity);
        for (var i = 0; i < Math.Min(list.Count, MaxRenderers); i++)
        {
            if (list[i] is not { Renderer: { } renderer } || !Fixed(renderer))
            {
                unfixed++;
                continue;
            }

            var value = renderer.RenderOrder;
            sorted &= value >= previous; // a NaN never is
            previous = value;
        }

        (order.List, order.Version, order.Epoch, order.Proven, order.Unfixed) =
            (list, version(list), epoch, sorted && unfixed == 0, unfixed);
        return order;
    }

    // Prefix on RegisterRenderer: a fixed renderer above a proven stage goes on the end unless a reservation refuses
    private static bool Rendering(ClientEventManager __instance, IRenderer? handler, EnumRenderStage stage,
        string profilingName,
        out (Order? Order, int Version, int Count, double Value, bool Fixed) __state)
    {
        __state = (null, 0, 0, double.NaN, false);
        if (!On(Renderers) || handler is null || _renderVersion is not { } version || !Main ||
            StageList(__instance, stage) is not { } list || Proof(list, stage) is not { } order)
        {
            return true;
        }

        var isFixed = Fixed(handler);
        var value = isFixed
            ? handler.RenderOrder
            : double.NaN; // only a fixed order is read here: it reads the same again
        __state = (order, version(list), list.Count, value, isFixed);
        if (!isFixed || !order.Proven || (list.Count > 0 && (double.IsNaN(value) ||
                                                             double.IsNaN(list[^1].Renderer.RenderOrder) ||
                                                             value <= list[^1].Renderer.RenderOrder)) ||
            Conflicts(__instance.renderOrderReservationsByStage, stage, handler, value))
        {
            return true;
        }

        list.Add(new RenderHandler { Renderer = handler, ProfilingName = $"{stage}-{profilingName}" });
        (order.Version, __state.Order) = (version(list), null);
        return false;
    }

    // Whether the engine would throw for a reservation, or one cannot be read; no side effects
    private static bool Conflicts(List<RenderOrderReservation?>[]? reservations, EnumRenderStage stage,
        IRenderer handler,
        double value)
    {
        var at = (int)stage;
        if (reservations is null || at < 0 || at >= reservations.Length || reservations[at] is not { } held ||
            held.Count > MaxReservations)
        {
            return true;
        }

        var all = held.Bounded(MaxReservations);
        for (var i = 0; i < Math.Min(all.Length, MaxReservations); i++)
        {
            if (all[i] is not { } reservation || reservation.Conflicts(handler, value)) return true;
        }

        return false;
    }

    // Postfix: one renderer inserted. A fixed one keeps the proof unless NaN (goes first); an unfixed one is counted.
    private static void Rendered(ClientEventManager __instance, EnumRenderStage stage,
        (Order? Order, int Version, int Count, double Value, bool Fixed) __state)
    {
        if (__state.Order is not { } order || _renderVersion is not { } version ||
            StageList(__instance, stage) is not { } list ||
            !ReferenceEquals(order.List, list) || order.Version != __state.Version ||
            version(list) != __state.Version + 1 || list.Count != __state.Count + 1)
        {
            return;
        }

        order.Version = version(list);
        if (__state.Fixed)
        {
            order.Proven &= !double.IsNaN(__state.Value);
        }
        else
        {
            (order.Unfixed, order.Proven) = (order.Unfixed + 1, false);
        }
    }

    // Unregistering keeps a stage's proof; the last unfixed one out has it proven again
    private static void Unrendering(ClientEventManager __instance, EnumRenderStage stage,
        out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (_renderVersion is not { } version || !Main || StageList(__instance, stage) is not { } list) return;

        __state = (true, version(list), list.Count);
    }

    private static void Unrendered(ClientEventManager __instance, IRenderer? handler, EnumRenderStage stage,
        (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _renderVersion is not { } version || StageList(__instance, stage) is not { } list ||
            Orders[(int)stage] is not { } order || !ReferenceEquals(order.List, list) ||
            order.Version != __state.Version || version(list) != __state.Version + 1 ||
            list.Count != __state.Count - 1)
        {
            return;
        }

        order.Version = version(list);
        if ((handler is null || !Fixed(handler)) && --order.Unfixed <= 0) order.List = null;
    }

    // RenderOrder is fixed: a constant or static readonly getter, or DummyRenderer's (Moved watches it)
    private static bool Fixed(IRenderer renderer)
    {
        var type = renderer.GetType();
        if (Types.TryGetValue(type, out var known)) return known;

        var getter = Implementation(type);
        var result = getter is not null &&
                     ((_dummyGetter is { } dummy && getter.MethodHandle == dummy.MethodHandle) || Returns(getter));
        if (Assert(Types.Count < MaxTypes)) Types[type] = result;

        return result;
    }

    // The RenderOrder getter the type dispatches to, for a virtual one its final override
    private static MethodInfo? Implementation(Type type)
    {
        if (_orderGetter is not { } wanted) return null;

        InterfaceMapping map;
        try
        {
            map = type.GetInterfaceMap(typeof(IRenderer));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        for (var i = 0; i < Math.Min(map.InterfaceMethods.Length, MaxMembers); i++)
        {
            if (map.InterfaceMethods[i].MethodHandle != wanted.MethodHandle) continue;

            var target = map.TargetMethods[i];
            return target is { IsVirtual: true, IsFinal: false } ? Override(type, target.GetBaseDefinition()) : target;
        }

        return null;
    }

    private static MethodInfo? Override(Type type, MethodInfo root)
    {
        var at = type;
        for (var depth = 0; depth < MaxDepth && at is not null; depth++)
        {
            var declared = AccessTools.GetDeclaredMethods(at);
            if (!Assert(declared.Count <= MaxMembers)) return null;

            foreach (var method in declared.Bounded(MaxMembers))
            {
                if (method.GetBaseDefinition().MethodHandle == root.MethodHandle) return method;
            }

            at = at.BaseType;
        }

        return null;
    }

    // ldc.r8 <constant>; ret, or ldsfld <static readonly field>; ret
    private static bool Returns(MethodInfo getter)
    {
        try
        {
            var il = getter.GetMethodBody()?.GetILAsByteArray();
            if (il is not { Length: > 0 } || il[^1] != (byte)OpCodes.Ret.Value) return false;

            if (il.Length == ConstantGetter && il[0] == (byte)OpCodes.Ldc_R8.Value) return true;

            return il.Length == FieldGetter && il[0] == (byte)OpCodes.Ldsfld.Value &&
                   getter.Module.ResolveField(BitConverter.ToInt32(il, 1), getter.DeclaringType?.GetGenericArguments(),
                           null)
                       is { IsStatic: true, IsInitOnly: true };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or BadImageFormatException
                                      or MissingMemberException or TypeLoadException)
        {
            return false;
        }
    }

    private sealed class Order
    {
        public List<RenderHandler>? List;
        public bool Proven;
        public int Version, Epoch, Unfixed;
    }
}

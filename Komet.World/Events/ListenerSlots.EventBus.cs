using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.World;

// Event bus: a list proven sorted by falling priority takes one at most its last's priority at the end
internal static partial class ListenerSlots
{
    // Main thread: the event bus list last looked at, its version then, and whether it was sorted
    private static List<EventBusListener>? _bus;
    private static int _busSeen;
    private static bool _busProven;

    private static AccessTools.FieldRef<List<EventBusListener>, int>? _busVersion;

    internal static MethodBase?[] BusSeams()
    {
        var api = typeof(ClientEventAPI);
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.RegisterEventBusListener),
                [typeof(EventBusListenerDelegate), typeof(double), typeof(string)]),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.UnregisterEventBusListener),
                [typeof(EventBusListenerDelegate)])
        ];
        return seams;
    }

    private static bool PatchBus(Harmony harmony)
    {
        _busVersion = Versioned<EventBusListener>();
        if (Seams[Bus] is not [{ } register, { } unregister] || !NotNull(_busVersion) ||
            !Binds(register, nameof(Listening), nameof(Listened)) ||
            !Binds(unregister, nameof(Unlistening), nameof(Unlistened)))
        {
            return false;
        }

        _ = harmony.Patch(register, Own(nameof(Listening)), Own(nameof(Listened)));
        _ = harmony.Patch(unregister, Own(nameof(Unlistening)), Own(nameof(Unlistened)));
        return true;
    }

    // Reproves the list sorted by falling priority when it changed under another hand
    private static void Look(List<EventBusListener> list, AccessTools.FieldRef<List<EventBusListener>, int> version)
    {
        if (ReferenceEquals(list, _bus) && _busSeen == version(list)) return;

        var (sorted, previous) = (list.Count <= MaxListeners, double.PositiveInfinity);
        for (var i = 0; i < Math.Min(list.Count, MaxListeners) && sorted; i++)
        {
            var priority = list[i] is { } listener ? listener.priority : double.NaN;
            sorted = previous >= priority; // a NaN never is
            previous = priority;
        }

        (_bus, _busSeen, _busProven) = (list, version(list), sorted);
    }

    // Prefix on RegisterEventBusListener: on a proven list, a priority at most the last one's goes on the end
    // ReSharper disable once InconsistentNaming - Harmony binds the engine's parameter name
    private static bool Listening(ClientMain? ___game, EventBusListenerDelegate OnEvent, double priority,
        string filterByEventName,
        out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (!On(Bus) || _busVersion is not { } version || ___game?.eventManager?.EventBusListeners is not { } list ||
            !Main)
        {
            return true;
        }

        Look(list, version);
        __state = (true, version(list), list.Count);
        if (!_busProven || (list.Count > 0 &&
                            (double.IsNaN(list[^1].priority) || double.IsNaN(priority) ||
                             list[^1].priority < priority)))
        {
            return true;
        }

        list.Add(new EventBusListener { handler = OnEvent, priority = priority, filterByName = filterByEventName });
        (_busSeen, __state) = (version(list), (false, 0, 0));
        return false;
    }

    // Postfix: one listener inserted; the proof holds unless the priority is NaN (goes first)
    private static void Listened(ClientMain? ___game, double priority, (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _busVersion is not { } version ||
            ___game?.eventManager?.EventBusListeners is not { } list ||
            !ReferenceEquals(list, _bus) || _busSeen != __state.Version || version(list) != __state.Version + 1 ||
            list.Count != __state.Count + 1)
        {
            return;
        }

        (_busSeen, _busProven) = (version(list), _busProven && !double.IsNaN(priority));
    }

    // Removal keeps the proof; from an unproven list it reproves
    private static void Unlistening(ClientMain? ___game, out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (_busVersion is not { } version || !Main || ___game?.eventManager?.EventBusListeners is not { } list) return;

        __state = (true, version(list), list.Count);
    }

    private static void Unlistened(ClientMain? ___game, (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _busVersion is not { } version ||
            ___game?.eventManager?.EventBusListeners is not { } list ||
            !ReferenceEquals(list, _bus) || _busSeen != __state.Version || version(list) != __state.Version + 1 ||
            list.Count != __state.Count - 1)
        {
            return;
        }

        if (_busProven)
        {
            _busSeen = version(list);
        }
        else
        {
            _bus = null;
        }
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace Komet.World;

// WeatherSystemClient.OnAssetsPacket walks weatherSimByMapRegion on the main thread without the lock its writers take
// (WeatherSystemBase.getOrCreateWeatherSimForRegion, on whichever thread asks a region's climate: the tesselation threads among
// them). A region made meanwhile crashed the client as it joined ("Collection was modified"); more tesselation threads made it
// likelier. The walk now holds that same lock.
//
// The same lookup was the most contended lock of a world join: the cloud renderer's thread (FluffyClouds, part of the game) asks for
// the four regions around its tiles over and over, the tesselation threads and the main thread ask too, and each lookup takes that
// lock for one dictionary read (trace, 2026-10-08). The client's weather system only ever adds regions, so the regions it handed out
// are kept in a concurrent table of its own and answered without the lock. A region not in it yet is asked from the engine one
// thread at a time: the engine makes a missing region outside its lock and stores whichever thread's comes last, so two threads could
// hand out two regions for one; one at a time, the second finds the first's, and the table never holds another than the engine's.
internal static class WeatherLock
{
    private const string Client = "Vintagestory.GameContent.WeatherSystemClient";
    private const string Base = "Vintagestory.GameContent.WeatherSystemBase";

    private const int MaxRegions = 1 << 16;
    private static readonly Lock Making = new();
    private static Regions? _regions; // the regions of the client weather system that asked last

    public static bool Patched { get; private set; }

    // The region lookup answered from the table
    private static bool Cached { get; set; }

    internal static MethodInfo? Target() =>
        AccessTools.TypeByName(Client) is { } type && Assert(Client.Length > 0) ? AccessTools.DeclaredMethod(type, "OnAssetsPacket") : null;

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || !Assert(Client.Length > 0) || Target() is not { } target || !NotNull(target.DeclaringType)) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Taking), finalizer: new HarmonyMethod(Leaving)));
        _regions = null;
        Cached = Lookup() is { } lookup && NotNull(harmony.Patch(lookup, new HarmonyMethod(Known), new HarmonyMethod(Kept),
            finalizer: new HarmonyMethod(Made)));
    }

    // WeatherSystemBase.getOrCreateWeatherSimForRegion(long, IMapRegion), which the (int, int) overload and every reader go through
    internal static MethodInfo? Lookup()
    {
        var method = AccessTools.TypeByName(Base) is { } type
            ? AccessTools.DeclaredMethod(type, "getOrCreateWeatherSimForRegion", [typeof(long), typeof(IMapRegion)])
            : null;
        return method is null || Assert(!method.ReturnType.IsValueType) ? method : null;
    }

    // A region the client's weather system handed out already: answered without its lock. Else the engine's lookup, one at a time.
    private static bool Known(object __instance, long index2d, ref object? __result, out bool __state)
    {
        __state = false;
        if (!NotNull(__instance) || __instance.GetType().FullName != Client) return true; // the server's removes regions
        var regions = Volatile.Read(ref _regions);
        if (regions is not null && ReferenceEquals(regions.Owner, __instance) && regions.Map.TryGetValue(index2d, out var known))
        {
            __result = known;
            return false;
        }

        _ = Assert(!Making.IsHeldByCurrentThread); // making a region never asks for another one
        Making.Enter();
        __state = true;
        return true;
    }

    // The engine's region, kept for the weather system that made it
    private static void Kept(object __instance, long index2d, object? __result, bool __state)
    {
        if (!__state || __result is null || !Assert(Making.IsHeldByCurrentThread)) return;
        var regions = Volatile.Read(ref _regions);
        if (regions is null || !ReferenceEquals(regions.Owner, __instance))
        {
            regions = new Regions(__instance); // another world's: let it go
            Volatile.Write(ref _regions, regions);
        }

        regions.Map[index2d] = __result;
        _ = Assert(regions.Map.Count <= MaxRegions); // the regions a client walks through, a few hundred
    }

    private static Exception? Made(Exception? __exception, bool __state)
    {
        _ = Assert(!__state || Cached); // only the patched lookup enters
        if (__state && Assert(Making.IsHeldByCurrentThread)) Making.Exit();
        return __exception;
    }

    private static void Taking(object ___weatherSimByMapRegionLock, out bool __state)
    {
        __state = false;
        if (!NotNull(___weatherSimByMapRegionLock) || !Assert(WeatherLock.Patched)) return;
        Monitor.Enter(___weatherSimByMapRegionLock, ref __state);
    }

    private static Exception? Leaving(Exception? __exception, object ___weatherSimByMapRegionLock, bool __state)
    {
        if (__state && NotNull(___weatherSimByMapRegionLock) && Assert(Monitor.IsEntered(___weatherSimByMapRegionLock)))
            Monitor.Exit(___weatherSimByMapRegionLock);
        return __exception;
    }

    private sealed class Regions(object owner)
    {
        public readonly ConcurrentDictionary<long, object> Map = new();
        public readonly object Owner = owner;
    }
}

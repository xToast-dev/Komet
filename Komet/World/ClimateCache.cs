using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// Interpolated climate maps are cached per map region, ten entries deep (ClientWorldMap.LerpedClimateMaps). A region is 512 blocks
// and its map holds one int per block, so every miss allocates a megabyte straight into the large object heap. The tesselator reaches
// (2 * viewDistance / 512 + 1)^2 regions - 49 at view distance 1536 - and the cache evicts in insertion order, not by use, so past ten
// regions every lookup misses and the megabytes become a steady stream. Sizing the cache after the view distance turns that into a
// one time cost. The engine's dictionary itself says what it was sized for, so nothing here outlives the world, and switched off the
// next call puts the engine's ten back.
internal static class ClimateCache
{
    public const int EngineCapacity = 10, MaxCapacity = 144; // one entry is a MiB, so the ceiling is the memory budget

    public static bool Enabled { get; set; } = true;
    public static int Capacity { get; private set; } // what the last call wanted, for the HUD

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LerpedClimateMaps")]
    private static extern ref LimitedDictionary<long, int[]> Maps(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LerpedClimateMapsLock")]
    private static extern ref object MapsLock(ClientWorldMap map);

    public static void Install(Harmony harmony)
    {
        var load = AccessTools.Method(typeof(ClientWorldMap),
            nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread));
        var (maps, gate) = (AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMaps"),
            AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMapsLock"));
        var capacity = AccessTools.Field(typeof(LimitedDictionary<long, int[]>), "capacity");
        if (!NotNull(harmony) || !NotNull(load) || !NotNull(maps) || !NotNull(gate) || !NotNull(capacity) ||
            !Assert(maps.FieldType == typeof(LimitedDictionary<long, int[]>) && capacity.FieldType == typeof(int)))
            return; // the accessors would throw at first use
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(typeof(ClimateCache), nameof(Sized))));
    }

    // Regions the tesselator can reach along one axis, squared; clamped so an extreme view distance cannot eat the heap
    internal static int CapacityFor(int region, int distance)
    {
        if (!Assert(region > 0) || !Assert(distance > 0)) return EngineCapacity;
        var perAxis = 2 * distance / region + 1;
        return Assert(perAxis > 0) ? Math.Clamp(perAxis * perAxis, EngineCapacity, MaxCapacity) : EngineCapacity;
    }

    // The engine's ten while switched off, else room for every region in view
    internal static int Wanted(bool enabled, int region, int distance)
    {
        return enabled ? CapacityFor(region, distance) : EngineCapacity;
    }

    // Harmony prefix, on the tesselation thread; the view distance is read only while on, and once: each read lower-cases its key
    // (SettingsBase)
    private static void Sized(ClientWorldMap __instance)
    {
        var enabled = Enabled;
        if (NotNull(__instance))
            Fit(__instance, Wanted(enabled, __instance.RegionSize, enabled ? ClientSettings.ViewDistance : 0));
    }

    // A dictionary of the wanted capacity in place of one of another, swapped under the engine's own lock since
    // TryLoadLerpedClimateMap reads the field under it. Entries are dropped: LimitedDictionary cannot be enumerated.
    internal static void Fit(ClientWorldMap map, int wanted)
    {
        var gate = MapsLock(map);
        if (!NotNull(gate) || !Assert(wanted is >= EngineCapacity and <= MaxCapacity)) return;
        Capacity = wanted;
        if (Maps(map) is { } maps && Limited<long, int[]>.CapacityOf(maps) == wanted) return;
        lock (gate)
        {
            ref var current = ref Maps(map);
            if (current is null || Limited<long, int[]>.CapacityOf(current) != wanted)
                current = new LimitedDictionary<long, int[]>(wanted);
        }
    }

    // LimitedDictionary's capacity: an accessor into a generic type is declared on the open definition
    private static class Limited<TKey, TValue>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capacity")]
        public static extern ref int CapacityOf(LimitedDictionary<TKey, TValue> dictionary);
    }
}

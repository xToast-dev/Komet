using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
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
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(Sized)));
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

// The cloud thread refreshes every cloud tile each 40 ms and builds a fresh Vec3d for each tile's world position: with ~34k tiles
// that is a steady 33 MB/s of garbage, half of what the client still allocates once the world has finished loading. The vector
// never leaves the loop body, so one vector per thread serves every tile: its one local is only read for its fields or handed to the
// weather readers (Readers), which only read X, Y and Z and keep nothing. Checked on the IL.
// The method also runs once on the main thread for the first cloud tick, which is why the scratch is per thread and not per map.
internal static class CloudTileScratch
{
    private const string Renderer = "FluffyClouds.CloudRendererMap", Method = "UpdateCloudTilesOffThread";
    private const string Reader = "Vintagestory.GameContent.WeatherDataReaderBase";
    private const string PreLoad = "Vintagestory.GameContent.WeatherDataReaderPreLoad";

    private const int MaxInstructions = 4096;

    [ThreadStatic] private static Vec3d? _scratch;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Substitute)));
    }

    // VSEssentials is a mod assembly Komet does not reference, so the renderer is found by name once the game has loaded it
    internal static MethodInfo? Target()
    {
        var renderer = AccessTools.TypeByName(Renderer);
        if (!NotNull(renderer)) return null;
        var method = AccessTools.Method(renderer, Method, [typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    internal static ConstructorInfo? Constructor()
    {
        var ctor = AccessTools.Constructor(typeof(Vec3d), [typeof(double), typeof(double), typeof(double)]);
        return NotNull(ctor) && Assert(ctor.DeclaringType == typeof(Vec3d)) ? ctor : null;
    }

    // The methods the loop hands its vector to, as their position: each reads only its X, Y and Z and keeps no reference to it
    private static MethodInfo?[] Readers()
    {
        var (reader, preload) = (AccessTools.TypeByName(Reader), AccessTools.TypeByName(PreLoad));
        if (!NotNull(reader) || !NotNull(preload)) return [];
        return
        [
            AccessTools.Method(reader, "LoadAdjacentSims", [typeof(Vec3d)]),
            AccessTools.Method(reader, "LoadLerp",
                [typeof(Vec3d), typeof(bool), typeof(float), typeof(float), typeof(float)]),
            AccessTools.Method(preload, "LoadLerp", [typeof(Vec3d)])
        ];
    }

    // Exactly one new Vec3d(x, y, z), stored to a local that is only read for Vec3d's fields or handed to Readers as their position.
    // Anything else means the method changed shape, and the engine's own IL is handed back.
    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var (ctor, mine, readers) =
            (Constructor(), AccessTools.Method(typeof(CloudTileScratch), nameof(At)), Readers());
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor));
        if (!Assert(at >= 0) || !Index(at + 1, code.Count) || !Assert(code[at + 1].IsStloc())) return code;
        if (!Assert(Il.Confined(code, at + 1, (c, operand) => operand == 0
                ? c.opcode == OpCodes.Ldfld && c.operand is FieldInfo field && field.DeclaringType == typeof(Vec3d)
                : operand == 1 && c.operand is MethodInfo method && Array.IndexOf(readers, method) >= 0)))
            return code;
        Rewritten = Assert(Il.Substitute(code, at, mine));
        return code;
    }

    // Stands in for new Vec3d(x, y, z): the caller reads the vector before the next tile asks for it again
    internal static Vec3d At(double x, double y, double z)
    {
        if (!Enabled || !Finite(x) || !Finite(z)) return new Vec3d(x, y, z);
        var scratch = _scratch ??= new Vec3d();
        return scratch.Set(x, y, z);
    }
}

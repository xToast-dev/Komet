using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace Komet.Test.World;

// The engine caches one interpolated climate map per region, ten deep, and evicts in insertion order. Past ten regions every lookup
// misses and allocates a megabyte, so the capacity has to follow the view distance rather than a constant.
public sealed class ClimateCacheTests
{
    private const int Region = 512; // MagicNum.ServerChunkSize * ChunkRegionSizeInChunks

    private static readonly FieldInfo Maps = AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMaps");
    private static readonly FieldInfo Capacity = AccessTools.Field(typeof(LimitedDictionary<long, int[]>), "capacity");

    [TestCase(256, ClimateCache.EngineCapacity)] // 2 per axis, below the engine's own ten
    [TestCase(512, ClimateCache.EngineCapacity)] // 3 per axis = 9, still below
    [TestCase(768, 16)]
    [TestCase(1024, 25)]
    [TestCase(1536, 49)] // the view distance this was found at
    [TestCase(1_000_000, ClimateCache.MaxCapacity)] // the memory ceiling
    public void CapacityCoversEveryRegionInView(int distance, int expected)
    {
        Assert.That(ClimateCache.CapacityFor(Region, distance), Is.EqualTo(expected));
    }

    [TestCase(0, 512)]
    [TestCase(512, 0)]
    [TestCase(-1, -1)]
    public void NonsenseArgumentsFallBackToTheEngineCapacity(int region, int distance)
    {
        Assert.That(ClimateCache.CapacityFor(region, distance), Is.EqualTo(ClimateCache.EngineCapacity));
    }

    [TestCase(true, 1536, 49)]
    [TestCase(false, 1536, ClimateCache.EngineCapacity)]
    [TestCase(false, 0, ClimateCache.EngineCapacity)] // switched off the view distance is not read
    public void SwitchedOffItWantsTheEnginesTen(bool enabled, int distance, int expected)
    {
        Assert.That(ClimateCache.Wanted(enabled, Region, distance), Is.EqualTo(expected));
    }

    // The prefix is on the engine method and reaches the engine's private fields: the dictionary is swapped only for another capacity,
    // and back to the engine's ten when switched off
    [Test]
    public void InstallsAndSwapsTheEnginesDictionaryForAnotherCapacityOnly()
    {
        var load = AccessTools.Method(typeof(ClientWorldMap),
            nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread));
        var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMapsLock").SetValue(map, new object());
        Maps.SetValue(map, new LimitedDictionary<long, int[]>(ClimateCache.EngineCapacity));
        var harmony = new Harmony("komet-test-climatecache");
        try
        {
            ClimateCache.Install(harmony);
            ClimateCache.Fit(map, ClimateCache.Wanted(true, Region, 1536));
            var sized = Maps.GetValue(map);
            ClimateCache.Fit(map, ClimateCache.Wanted(true, Region, 1536));
            var kept = Maps.GetValue(map);
            ClimateCache.Fit(map, ClimateCache.Wanted(false, Region, 1536));
            Assert.Multiple(() =>
            {
                Assert.That(Harmony.GetPatchInfo(load)?.Prefixes.Select(p => p.owner), Is.EqualTo([harmony.Id]));
                Assert.That(Capacity.GetValue(sized), Is.EqualTo(49));
                Assert.That(kept, Is.SameAs(sized), "the same capacity keeps the dictionary and its maps");
                Assert.That(Capacity.GetValue(Maps.GetValue(map)), Is.EqualTo(ClimateCache.EngineCapacity));
                Assert.That(ClimateCache.Capacity, Is.EqualTo(ClimateCache.EngineCapacity));
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }
}

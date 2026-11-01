using Komet.Api;
using Komet.Core;
using Vintagestory.GameContent;

namespace Komet.Test.Core;

// Each feature whose install needs only Harmony and a logger finds the engine code it patches on this game version: installed
// through its registry entry, after the features it requires, it reports Active, not EngineChanged, StoodDown or Failed
[NonParallelizable]
public sealed class FeatureStatesTests
{
    private static readonly string[] HarmonyOnly =
    [
        "ChunkPacking", "BushShapes", "ProfilerPool", "WeatherLock", "EntityCasters", "HelpCompose", "ListenerSlots"
    ];

    private const int MaxRequires = 16;

    [TestCaseSource(nameof(Installable))]
    public void TheFeatureIsActiveOnThisEngine(string id)
    {
        // the libraries features look up by name, loaded as in a running game: the survival mod and protobuf-net
        Assert.That(typeof(BlockSoil).Assembly.GetName().Name, Is.EqualTo("VSSurvivalMod"));
        Assert.That(Assembly.Load("protobuf-net").GetName().Name, Is.EqualTo("protobuf-net"));
        var index = Features.Find(id);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), id);
        var requires = Features.All[index].Requires;
        var required = new List<FeatureRig>(requires.Length);
        try
        {
            for (var i = 0; i < Math.Min(requires.Length, MaxRequires); i++) required.Add(new FeatureRig(requires[i]));
            using var rig = new FeatureRig(id);
            // a switch that ships at the engine's value reports Off: matched, just not wanted
            var engine = Features.All[index].Knobs is { Length: > 0 } knobs && Array.TrueForAll(knobs, k => k.Get() == k.Engine);
            Assert.That(rig.State, Is.EqualTo(engine ? FeatureState.Off : FeatureState.Active));
        }
        finally
        {
            for (var i = Math.Min(required.Count, MaxRequires) - 1; i >= 0; i--) required[i].Dispose();
        }
    }

    // the server-only features join the hand-written list; each id appears in exactly one of the two sources
    [Test]
    public void TheHandWrittenListHoldsNoServerOnlyFeature()
    {
        Assert.That(HarmonyOnly.Where(id => Features.Find(id) < 0), Is.Empty);
        Assert.That(HarmonyOnly.Where(id => Features.All[Features.Find(id)].ServerOnly), Is.Empty);
    }

    private static IEnumerable<string> Installable() =>
        HarmonyOnly.Concat(Features.All.ToArray().Where(f => f.ServerOnly).Select(f => f.Id));
}

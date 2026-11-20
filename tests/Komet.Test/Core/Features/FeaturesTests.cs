using Komet.Core;
using Komet.Hud;
using Komet.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Core;

// The built-in table pins the install order FeatureOrder makes of the features' declarations (also the knobs' order in komet.json, the
// settings pages and bench.json) and the file's keys; the rest are invariants of the modules' lists.
[NonParallelizable]
public sealed class FeaturesTests
{
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "RenderCost", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "ProgramCache", "DistantShadows",
        "LeanVaryings", "SharpUpscale", "LeanGodRays", "LeanPost", "ModShaders", "FrustumSweep", "FarShadowReuse",
        "NearShadowReuse", "EntityCasters", "IndirectDraw", "OcclusionCulling", "GlQueryCache",
        "VulkanCore", "VulkanShaderCache", "VulkanGpuTimes", "TerrainRebuild", "PoolTrim",
        "VulkanTerrain", "AnimatableCulling", "PotCulling", "HearthCulling", "HelpCompose", "MapTileCulling",
        "MapTileUpload",
        "IdleAnimators", "PoolScale", "WindowSize", "ChunkLookup", "MeshRecycle",
        "FacePacking", "FaceSorting", "ClimateCache", "PartitionReuse", "DirtyBlocks", "CookingMatch", "AlloyMatch",
        "AnimationFrames", "ShapeInitMemo", "KeyframeShare", "HeldItemsLast", "EntityTimes", "ChunkBudget",
        "ChunkLoadBudget", "JoinPacer", "ChunkPacking", "JsonValues", "EventBusSweep",
        "ListenerSlots", "BlockEntityCaches", "ChunkThreadClosure", "CloudTileScratch", "CloudUploads",
        "DecompressScratch", "DirectChunks", "PackedTicks", "ChunkQueueWalk", "BushShapes", "MapPixels", "TreeKeys", "MapPieceBudget",
        "MapSaveScratch", "LightScratch", "LightRepair", "ParticleLight", "RelightRedraws",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "NeighbourReads", "FaceLight", "OwnTessellation",
        "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers", "JitWarm", "OccludedChunks", "DeepCulling",
        "FarLod", "FarFoliage", "BackgroundPriority", "GcLatency", "InventoryShapes", "WorldGenScratch", "IconBudget",
        "SnowPeek", "TickScratch", "SoundScratch", "SoundsOnDemand", "TickerCommit", "ProfilerPool", "WeatherLock",
        "FramePacer", "GcIdle", "PointLights", "FrameScratch", "FrameClock", "HangWatch"
    ];

    // komet.json as Komet writes it without a knob set by the player: the display settings, the preset and the layout
    private static readonly string[] SettingsFile =
    [
        "Visible:Boolean", "ShowDetails:Boolean", "Corner:String", "Opacity:Float", "Interval:Float", "BenchSeconds:Float", "ShowFps:Boolean",
        "ShowLows:Boolean", "ShowFrametime:Boolean", "ShowGraph:Boolean", "ShowMods:Boolean",
        "ToastMs:Float", "Preset:String", "Chosen:Array", "Profiled:Integer", "WindowTab:String", "OptionsPage:String",
        "OptionsFavorites:Array",
        "WindowX:String", "WindowY:String", "DetailX:String", "DetailY:String", "FramesX:String", "FramesY:String", "OverlayX:String", "OverlayY:String", "DebugLanguage:String",
        "DebugOnJoin:Boolean", "UpdateCheck:Boolean",
        "UpdateAsked:Boolean", "FontScale:Float", "ShowAdvanced:Boolean", "MenuStyle:String",
        "MenuOpacity:Float", "MenuMotion:Integer"
    ];

    [OneTimeSetUp]
    public void LoadALanguage() => GameLang.EnsureLoaded();

    [TearDown]
    public void Close() => FeatureLifecycle.Close();

    private static Feature[] All() => BuiltIns.All();

    [Test]
    public void TheFeaturesInstallInTheirOrder() => Assert.That(All().Select(f => f.Id), Is.EqualTo(InstallOrder));

    [Test]
    public void EveryRequiredFeatureComesEarlier()
    {
        var ids = All().Select(f => f.Id).ToList();
        var requires = All().SelectMany((f, i) => f.Requires.Select(r => (f.Id, Required: r, At: i))).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(requires, Is.Not.Empty);
            foreach (var (id, required, at) in requires)
            {
                Assert.That(ids.IndexOf(required), Is.InRange(0, at - 1), $"{id} requires {required}");
            }
        });
    }

    // One key and one saved name a knob, each in its owner's list and starting at a value in its range
    [Test]
    public void EveryKnobIsOneOfItsOwners()
    {
        var knobs = Knobs.BuiltIn.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(knobs.Select(k => k.Key), Is.Unique);
            Assert.That(knobs.Select(k => k.Persisted), Is.Unique);
            Assert.That(knobs.Select(k => All()[k.Owner].Knobs.Any(o => o.Key == k.Key)), Is.All.True);
            Assert.That(knobs.Where(k => k.Engine < k.Min || k.Engine > k.Max).Select(k => k.Key), Is.Empty);
            Assert.That(Knobs.Count, Is.EqualTo(Knobs.BuiltInCount));
        });
    }

    // Each feature pins the bodies it reproduces, skips or replays to those of Vintage Story 1.22.7: a game update that fails here
    // needs the feature's proof checked again or its golden tests re-run (the rebuilt graphics tabs looked at again: new controls,
    // other handlers) and the constant renewed
    [TestCaseSource(nameof(Fingerprints))]
    public void TheFingerprintIsThatOfTheInstalledEngine(string id, int index)
    {
        var bodies = All()[Features.Find(id)].Shapes[index];
        var shape = EngineShape.Of(bodies.Methods());
        Assert.That(shape, Is.EqualTo(bodies.Expected), $"{id} changed: 0x{shape:X16}UL");
    }

    private static IEnumerable<TestCaseData> Fingerprints() =>
        All().SelectMany(f => f.Shapes.Select((_, i) =>
            new TestCaseData(f.Id, i).SetArgDisplayNames(i == 0 ? f.Id : f.Id + i)));

    // A value that is not the engine's
    private static int Other(Knob knob) => knob.Engine == knob.Max ? knob.Min : knob.Max;

    // Two holders: the knobs stay at the engine's values until the second lets go, a write meanwhile is what comes back, and the
    // settings still show what was wanted
    [TestCaseSource(nameof(Holdable))]
    public void AHoldKeepsTheEngineUntilTheLastRelease(string id)
    {
        var feature = Features.Find(id);
        var knobs = All()[feature].Knobs.Select(k => Knobs.Find(k.Key)).ToArray();
        var before = Knobs.Snapshot();
        try
        {
            foreach (var k in knobs)
            {
                _ = Knobs.Write(k, Other(Knobs.At(k)));
            }

            Assert.That((FeatureHolds.Hold(feature), FeatureHolds.Hold(feature)), Is.EqualTo((true, true)));
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)));
                Assert.That(knobs.Select(k => Knobs.Snapshot()[k]), Is.EqualTo(knobs.Select(k => Other(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Not.Null);
            });
            foreach (var k in knobs)
            {
                Assert.That(Knobs.Write(k, Knobs.At(k).Engine), Is.False);
                Assert.That(Knobs.Snapshot()[k], Is.EqualTo(Knobs.At(k).Engine), "recorded");
                Assert.That(Knobs.Write(k, Later(Knobs.At(k))), Is.False);
            }

            Assert.That(FeatureHolds.Release(feature), Is.True);
            Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)),
                "one holds");
            Assert.That(FeatureHolds.Release(feature), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Later(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Null);
                Assert.That(FeatureHolds.Release(feature), Is.False, "nothing left to release");
            });
        }
        finally
        {
            FeatureLifecycle.Close();
            _ = Knobs.Apply(before);
        }
    }

    private static IEnumerable<string> Holdable() => All().Where(f => f.Knobs.Length > 0).Select(f => f.Id);

    // Another value than the engine's, below the first one where the range has room
    private static int Later(Knob knob)
    {
        var other = Other(knob);
        return other - 1 > knob.Engine ? other - 1 : other;
    }

    // Replaces: another mod's prefix on a method the feature skips stands it down; on a method it only rewrites (Body) a
    // prefix is harmless and a second transpiler is not. Gone again, the feature is back.
    [TestCase("FrustumSweep", false)]
    [TestCase("PoolScale", true)]
    public void AnotherModOnAReplacedMethodStandsTheFeatureDown(string id, bool rewrites)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var rig = new FeatureRig(id);
        Assert.That(rig.State, Is.EqualTo(KapiFeatureState.Active));
        var method = All()[rig.Feature].Replaces!()[0]!;
        using (var foreign = new TestHarmony("komet-test-feature-foreign"))
        {
            _ = foreign.Patch(method, Foreign.Prefix);
            Features.Recheck();
            Assert.That(rig.State, Is.EqualTo(rewrites ? KapiFeatureState.Active : KapiFeatureState.StoodDown), "a prefix");
            _ = foreign.Patch(method, transpiler: Foreign.Transpiler);
            Features.Recheck();
            Assert.That(rig.State, Is.EqualTo(KapiFeatureState.StoodDown), "a transpiler");
        }

        Features.Recheck();
        Assert.That(rig.State, Is.EqualTo(KapiFeatureState.Active));
    }

    // Only the knobs the player set, under their saved names, a switch as a bool; a file read back and saved again is the
    // same file
    [Test]
    public void TheSettingsFileKeepsItsKeys()
    {
        var json = JsonConvert.SerializeObject(new HudSettings());
        var loaded = JsonConvert.DeserializeObject<HudSettings>(json)!;
        var logger = new CapturingLogger();
        loaded.Knobs.Apply(logger, HudSettings.FileName);
        var (packing, shadows) = (Knobs.Find("ChunkPacking"), Knobs.Find("DistantShadows"));
        loaded.Knobs.SetKnob(packing, 3);
        loaded.Knobs.SetKnob(shadows, 1 - KnobSettings.KnobDefault(shadows));
        var chosen = JObject.Parse(JsonConvert.SerializeObject(loaded));
        Assert.Multiple(() =>
        {
            Assert.That(JObject.Parse(json).Properties().Select(p => p.Name + ":" + p.Value.Type),
                Is.EqualTo(SettingsFile));
            Assert.That(logger.Lines, Is.Empty);
            Assert.That(chosen.Properties().Select(p => p.Name + ":" + p.Value.Type),
                Is.EqualTo(SettingsFile.Append("DistantShadows:Boolean").Append("ChunkPacking:Integer")),
                "in the knobs' order");
        });
        loaded.Knobs.SetKnob(packing, KnobSettings.KnobDefault(packing));
        loaded.Knobs.SetKnob(shadows, KnobSettings.KnobDefault(shadows));
        Assert.That(JsonConvert.SerializeObject(loaded), Is.EqualTo(json), "back to following: out of the file");
    }

    [Test]
    public void AnOldFileKeepsOnlyWhatThePlayerChanged()
    {
        var file = JObject.Parse(JsonConvert.SerializeObject(new HudSettings()));
        _ = file.Remove("Profiled");
        file["ChunkPacking"] = 3; // changed by the player (Komet's default is 1)
        var loaded = JsonConvert.DeserializeObject<HudSettings>(file.ToString())!;
        loaded.Knobs.Apply(new CapturingLogger(), HudSettings.FileName);
        var packing = Knobs.Find("ChunkPacking");
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Chosen, Is.EquivalentTo((string[])["ChunkPacking"]));
            Assert.That(loaded.Knobs.Knob(packing), Is.EqualTo(3));
            Assert.That(loaded.Profiled, Is.EqualTo(KnobSettings.Layout));
        });
        loaded.Knobs.SetKnob(packing,
            KnobSettings.KnobDefault(packing)); // back to the default: follows the computer again
        Assert.That(loaded.Chosen, Is.Empty);
    }

}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Core;

// The feature registry. The built-in table pins what KometModSystem hardcoded before it: the install order, the knob table in dialog
// order (komet-hud.json, the settings pages and bench.json read it) and the stops. The bodies the features rely on are those of the
// installed engine, a knob write reaches exactly its static, a hold keeps a feature at the engine's behaviour until its last release,
// and a probe tells a feature another mod stood down. Every test closes the registry again.
[NonParallelizable]
public sealed class FeaturesTests
{
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "DistantShadows", "FrustumSweep", "IndirectDraw", "SunOcclusion",
        "AnimatableCulling", "IdleAnimators", "WindowSizeCache", "PoolScale", "GlErrorPoll", "ChunkLookup", "MeshPool",
        "MeshRecycle", "ClimateCache", "PartitionReuse", "HandlerLists", "CookingMatch",
        "AnimationFrames", "InitOnce", "ShapeInitMemo", "EntityTessBudget", "ChunkBudget",
        "ChunkThreadClosure", "CloudTileScratch", "DecompressScratch", "LightScratch", "LightRepair", "ParticleLight",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "ExtendedRows", "VisibleFaces", "FaceLight", "TessBlockPos",
        "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers",
        "OccludedChunks", "FrameClock", "Benchmark", "PreJit"
    ];

    // Key, komet-hud.json name, page/group, range and engine value, owner
    private static readonly string[] KnobTable =
    [
        "FrustumSweep FrustumSweep render/culling 0..1 engine 0 FrustumSweep",
        "SunOcclusion SunOcclusion render/culling 0..1 engine 0 SunOcclusion",
        "ShaderUseCache ShaderUseCache render/drawing 0..1 engine 0 ShaderUseCache",
        "IndirectDraw IndirectDraw render/drawing 0..1 engine 0 IndirectDraw",
        "WindowSizeCache WindowSizeCache render/drawing 0..1 engine 0 WindowSizeCache",
        "MeshPool PoolFragments render/upload 0..1 engine 0 MeshPool",
        "MeshRecycle MeshRecycle render/upload 0..1 engine 0 MeshRecycle",
        "UploadCap UploadCap render/upload 0..20ms engine 0 ChunkBudget",
        "ChunkLookup ChunkLookup chunks/arrival 0..1 engine 0 ChunkLookup",
        "DecompressScratch DecompressScratch chunks/arrival 0..1 engine 0 DecompressScratch",
        "TessSchedule NearFirst chunks/tessellation 0..1 engine 0 TessSchedule",
        "ExtendedRows ExtendedRows chunks/tessellation 0..1 engine 0 ExtendedRows",
        "VisibleFaces VisibleFaces chunks/tessellation 0..1 engine 0 VisibleFaces",
        "FaceLight FaceLight chunks/tessellation 0..1 engine 0 FaceLight",
        "OccludedChunks OccludedChunks chunks/tessellation 0..1 engine 0 OccludedChunks",
        "WorkerThreads WorkerThreads chunks/threads 0..8 engine 0 WorkerPool",
        "TessJobs TessJobs chunks/threads 0..8 engine 0 TessWorkers",
        "LightScratch LightScratch chunks/light 0..1 engine 0 LightScratch",
        "ParticleLight ParticleLight chunks/light 0..1 engine 0 ParticleLight",
        "AnimationFrames AnimationFrames misc/entities 0..1 engine 0 AnimationFrames",
        "InitOnce InitOnce misc/entities 0..1 engine 0 InitOnce",
        "ShapeInitMemo ShapeInitMemo misc/entities 0..1 engine 0 ShapeInitMemo",
        "EntityTessBudget EntityTessBudget misc/entities 0..50ms engine 0 EntityTessBudget",
        "ClimateCache ClimateCache misc/garbage 0..1 engine 0 ClimateCache",
        "ColumnNoiseScratch ColumnNoiseScratch misc/garbage 0..1 engine 0 ColumnNoiseScratch",
        "CloudTileScratch CloudTileScratch -/- 0..1 engine 0 CloudTileScratch",
        "PreJit PreJit misc/garbage 0..1 engine 0 PreJit",
        "GraphicsMenu GraphicsMenu misc/menu 0..1 engine 0 GraphicsMenu",
        "MenuBlur MenuBlur misc/menu 0..1 engine 0 MenuBlur",
        "PartitionReuse PartitionReuse misc/garbage 0..1 engine 0 PartitionReuse",
        "AnimatableCulling AnimatableCulling render/culling 0..1 engine 0 AnimatableCulling",
        "TessBlockPos TessBlockPos misc/garbage 0..1 engine 0 TessBlockPos",
        "FrustumStages FrustumStages render/culling 0..1 engine 0 FrustumSweep",
        "GlErrorPoll GlErrorPoll render/drawing 0..1 engine 0 GlErrorPoll",
        "IdleAnimators IdleAnimators render/culling 0..1 engine 0 IdleAnimators",
        "CookingMatch CookingMatch misc/garbage 0..1 engine 0 CookingMatch",
        "HandlerLists HandlerLists misc/garbage 0..1 engine 0 HandlerLists",
        "PoolScale PoolScale render/drawing 1..8x engine 1 PoolScale",
        "LightRepair LightRepair chunks/light 0..1 engine 0 LightRepair",
        "DistantShadows DistantShadows render/drawing 0..1 engine 0 DistantShadows"
    ];

    // Each knob's static, read without the table
    private static readonly Dictionary<string, Func<int>> Statics = new()
    {
        ["FrustumSweep"] = () => On(FrustumSweep.Enabled), ["SunOcclusion"] = () => On(SunOcclusion.Enabled),
        ["ShaderUseCache"] = () => On(ShaderUseCache.Enabled), ["IndirectDraw"] = () => On(IndirectDraw.Enabled),
        ["WindowSizeCache"] = () => On(WindowSizeCache.Enabled), ["MeshPool"] = () => On(MeshPool.Enabled),
        ["MeshRecycle"] = () => On(MeshRecycle.Enabled), ["UploadCap"] = () => ChunkBudget.CapMillis,
        ["ChunkLookup"] = () => On(ChunkLookup.Enabled), ["DecompressScratch"] = () => On(DecompressScratch.Enabled),
        ["TessSchedule"] = () => On(TessSchedule.Enabled), ["ExtendedRows"] = () => On(ExtendedRows.Enabled),
        ["VisibleFaces"] = () => On(VisibleFaces.Enabled), ["FaceLight"] = () => On(FaceLight.Enabled),
        ["OccludedChunks"] = () => On(OccludedChunks.Enabled), ["WorkerThreads"] = () => WorkerPool.Wanted,
        ["TessJobs"] = () => TessWorkers.Jobs, ["LightScratch"] = () => On(LightScratch.Enabled),
        ["ParticleLight"] = () => On(ParticleLight.Enabled), ["AnimationFrames"] = () => On(AnimationFrames.Enabled),
        ["InitOnce"] = () => On(InitOnce.Enabled), ["ShapeInitMemo"] = () => On(ShapeInitMemo.Enabled),
        ["EntityTessBudget"] = () => EntityTessBudget.Millis, ["ClimateCache"] = () => On(ClimateCache.Enabled),
        ["ColumnNoiseScratch"] = () => On(ColumnNoiseScratch.Enabled),
        ["CloudTileScratch"] = () => On(CloudTileScratch.Enabled), ["PreJit"] = () => On(PreJit.Enabled),
        ["GraphicsMenu"] = () => On(GraphicsMenu.Enabled), ["MenuBlur"] = () => On(Backdrop.Enabled),
        ["PartitionReuse"] = () => On(PartitionReuse.Enabled),
        ["AnimatableCulling"] = () => On(AnimatableCulling.Enabled),
        ["TessBlockPos"] = () => On(TessBlockPos.Enabled), ["FrustumStages"] = () => On(FrustumSweep.Stages),
        ["GlErrorPoll"] = () => On(GlErrorPoll.Enabled), ["IdleAnimators"] = () => On(IdleAnimators.Enabled),
        ["CookingMatch"] = () => On(CookingMatch.Enabled), ["HandlerLists"] = () => On(HandlerLists.Enabled),
        ["PoolScale"] = () => PoolScale.Scale, ["LightRepair"] = () => On(LightRepair.Enabled),
        ["DistantShadows"] = () => On(DistantShadows.Enabled)
    };

    // komet-hud.json as Komet 1.x wrote it: the display settings, then every knob under its saved name, a switch as a bool
    private static readonly string[] SettingsFile =
    [
        "Visible:Boolean", "Corner:String", "Opacity:Float", "Interval:Float", "BenchSeconds:Float",
        "ShowGraph:Boolean", "ShowSystem:Boolean", "ShowPasses:Boolean", "ShowModTimes:Boolean", "Detail:Boolean",
        "ShowMods:Boolean", "ShowCounters:Boolean", "ShowLog:Boolean", "ShowDebugLog:Boolean", "UpdateCheck:Boolean",
        "UpdateAsked:Boolean", "Pinned:Object", "FontScale:Float", "FrustumSweep:Boolean", "SunOcclusion:Boolean",
        "ShaderUseCache:Boolean", "IndirectDraw:Boolean", "WindowSizeCache:Boolean", "PoolFragments:Boolean",
        "MeshRecycle:Boolean", "UploadCap:Integer", "ChunkLookup:Boolean", "DecompressScratch:Boolean",
        "NearFirst:Boolean", "ExtendedRows:Boolean", "VisibleFaces:Boolean", "FaceLight:Boolean",
        "OccludedChunks:Boolean", "WorkerThreads:Integer", "TessJobs:Integer", "LightScratch:Boolean",
        "ParticleLight:Boolean", "AnimationFrames:Boolean", "InitOnce:Boolean", "ShapeInitMemo:Boolean",
        "EntityTessBudget:Integer", "ClimateCache:Boolean", "ColumnNoiseScratch:Boolean", "CloudTileScratch:Boolean",
        "PreJit:Boolean", "GraphicsMenu:Boolean", "MenuBlur:Boolean", "PartitionReuse:Boolean",
        "AnimatableCulling:Boolean", "TessBlockPos:Boolean", "FrustumStages:Boolean",
        "GlErrorPoll:Boolean", "IdleAnimators:Boolean", "CookingMatch:Boolean",
        "HandlerLists:Boolean", "PoolScale:Integer", "LightRepair:Boolean", "DistantShadows:Boolean"
    ];

    private static readonly string[] Stops =
    [
        "PreJit", "TessWorkers", "WorkerPool", "TessSchedule", "TessAccounting", "ParticleLight",
        "AnimationFrames", "HandlerLists", "ChunkLookup", "DistantShadows", "GraphicsMenu", "Hud"
    ];

    private static readonly string[] ServerOnly = ["ChunkThreadClosure", "LightRepair", "ColumnNoiseScratch"],
        Tail = ["Benchmark"],
        Last = ["PreJit"], Unpatched = ["TessSafety"];

    private static readonly string[] Shaped =
    [
        "ModTimes", "GraphicsMenu", "DistantShadows", "AnimatableCulling", "IdleAnimators", "CookingMatch", "AnimationFrames", "InitOnce",
        "ShapeInitMemo", "LightRepair", "ExtendedRows", "VisibleFaces", "FaceLight", "FaceLight", "OccludedChunks"
    ];

    // Lang.Get throws until a language is loaded: the game's English, unless one is loaded already
    [OneTimeSetUp]
    public void LoadALanguage()
    {
        if (Lang.CurrentLocale is { } locale && Lang.AvailableLanguages.ContainsKey(locale)) return;
        GameLang.LoadEnglish();
    }

    [TearDown]
    public void Close()
    {
        Features.Close();
    }

    private static int On(bool on)
    {
        return on ? 1 : 0;
    }

    private static Feature[] All()
    {
        return Features.All.ToArray();
    }

    [Test]
    public void TheFeaturesInstallInTheirOrder()
    {
        Assert.That(All().Select(f => f.Id), Is.EqualTo(InstallOrder));
    }

    [Test]
    public void EveryRequiredFeatureComesEarlier()
    {
        var ids = All().Select(f => f.Id).ToList();
        var requires = All().SelectMany((f, i) => f.Requires.Select(r => (f.Id, Required: r, At: i))).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(requires, Is.Not.Empty);
            foreach (var (id, required, at) in requires)
                Assert.That(ids.IndexOf(required), Is.InRange(0, at - 1), $"{id} requires {required}");
        });
    }

    // Server code only where the server runs; the bench after every feature it measures, PreJit after the HUD's renderers
    [Test]
    public void ServerFeaturesAndTheLateStagesAreFew()
    {
        var all = All();
        Assert.Multiple(() =>
        {
            Assert.That(all.Where(f => f.ServerOnly).Select(f => f.Id), Is.EqualTo(ServerOnly));
            Assert.That(all.Where(f => f.Stage == FeatureStage.Tail).Select(f => f.Id), Is.EqualTo(Tail));
            Assert.That(all.Where(f => f.Stage == FeatureStage.Last).Select(f => f.Id), Is.EqualTo(Last));
            Assert.That(all.Select(f => f.Stage), Is.Ordered, "the stages install in table order");
        });
    }

    [Test]
    public void TheKnobTableKeepsItsOrderNamesAndRanges()
    {
        var knobs = Knobs.BuiltIn.ToArray().Select(k => string.Create(CultureInfo.InvariantCulture,
            $"{k.Key} {k.Persisted} {k.Page ?? "-"}/{k.Group ?? "-"} {k.Min}..{k.Max}{k.Unit}") +
            string.Create(CultureInfo.InvariantCulture, $" engine {k.Engine} {All()[k.Owner].Id}"));
        Assert.Multiple(() =>
        {
            Assert.That(knobs, Is.EqualTo(KnobTable));
            Assert.That(Knobs.BuiltIn.ToArray().Select(k => k.Order), Is.EqualTo(Enumerable.Range(0, Knobs.Count)));
            Assert.That(Knobs.Count, Is.EqualTo(Knobs.BuiltInCount));
        });
    }

    // Stops run in reverse install order before UnpatchAll: PreJit's first, TessWorkers' before the pool's, GraphicsMenu's before
    // the HUD's window goes; TessSafety clears after the unpatch
    [Test]
    public void TheStopsRunInReverseInstallOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enumerable.Reverse(All()).Where(f => f.Stop is not null).Select(f => f.Id), Is.EqualTo(Stops));
            Assert.That(All().Where(f => f.Unpatched is not null).Select(f => f.Id), Is.EqualTo(Unpatched));
        });
    }

    [Test]
    public void AFeatureIsFoundByItsIdOrItsKnob()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Find("UploadCap"), Is.EqualTo(Features.Find("ChunkBudget")).And.GreaterThan(0));
            Assert.That(Features.Find("MenuBlur"), Is.EqualTo(Array.IndexOf(InstallOrder, "MenuBlur")));
            Assert.That(Features.Find("Nothing"), Is.EqualTo(-1));
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

    private static IEnumerable<TestCaseData> Fingerprints()
    {
        return All().SelectMany(f => f.Shapes.Select((_, i) =>
            new TestCaseData(f.Id, i).SetArgDisplayNames(i == 0 ? f.Id : f.Id + i)));
    }

    [Test]
    public void EveryPinnedFeatureIsTested()
    {
        Assert.That(All().SelectMany(f => f.Shapes.Select(_ => f.Id)), Is.EqualTo(Shaped));
    }

    // Write reaches the knob's own static, and only a difference is written
    [TestCaseSource(nameof(KnobKeys))]
    public void AWriteReachesTheKnobsStatic(string key)
    {
        var (index, read) = (Knobs.Find(key), Statics[key]);
        var knob = Knobs.At(index);
        var before = knob.Get();
        var other = before == knob.Engine ? Other(knob) : knob.Engine;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(read(), Is.EqualTo(before));
                Assert.That(Knobs.Write(index, before), Is.False, "the same value is not written");
                Assert.That(Knobs.Write(index, other), Is.True);
                Assert.That((knob.Get(), read(), Knobs.Snapshot()[index]), Is.EqualTo((other, other, other)));
            });
        }
        finally
        {
            _ = Knobs.Write(index, before);
        }
    }

    private static IEnumerable<string> KnobKeys()
    {
        return KnobTable.Select(line => line.Split(' ')[0]);
    }

    // A value that is not the engine's
    private static int Other(Knob knob)
    {
        return knob.Engine == knob.Max ? knob.Min : knob.Max;
    }

    // Two holders: the knobs stay at the engine's values until the second lets go, a write meanwhile is what comes back, and the
    // settings still show what was wanted
    [TestCaseSource(nameof(Holdable))]
    public void AHoldKeepsTheEngineUntilTheLastRelease(string id)
    {
        var feature = Features.Find(id);
        var knobs = All()[feature].Knobs.Select(k => k.Order).ToArray();
        var before = Knobs.Snapshot();
        try
        {
            foreach (var k in knobs) _ = Knobs.Write(k, Other(Knobs.At(k)));
            Assert.That((Features.Hold(feature), Features.Hold(feature)), Is.EqualTo((true, true)));
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

            Assert.That(Features.Release(feature), Is.True);
            Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)),
                "one holds");
            Assert.That(Features.Release(feature), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Later(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Null);
                Assert.That(Features.Release(feature), Is.False, "nothing left to release");
            });
        }
        finally
        {
            Features.Close();
            _ = Knobs.Apply(before);
        }
    }

    private static IEnumerable<string> Holdable()
    {
        return All().Where(f => f.Knobs.Length > 0).Select(f => f.Id);
    }

    // Another value than the engine's, below the first one where the range has room
    private static int Later(Knob knob)
    {
        var other = Other(knob);
        return other - 1 > knob.Engine ? other - 1 : other;
    }

    [Test]
    public void AFeatureWithoutAKnobCannotBeHeld()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Hold(Features.Find("TessSafety")), Is.False);
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        });
    }

    // Closing the world ends every hold and writes the wanted values back
    [Test]
    public void ClosingEndsTheHolds()
    {
        var (feature, knob) = (Features.Find("FrustumSweep"), Knobs.Find("FrustumSweep"));
        Assert.That(FrustumSweep.Enabled, Is.True);
        _ = Features.Hold(feature);
        _ = Features.Hold(feature);
        Assert.Multiple(() =>
        {
            Assert.That((FrustumSweep.Enabled, Features.Held(knob)), Is.EqualTo((false, true)));
            Assert.That(Features.HoldsText(), Is.EqualTo("FrustumSweep (2)"));
        });
        Features.Close();
        Assert.That((FrustumSweep.Enabled, Features.Held(knob), Features.HoldsText()),
            Is.EqualTo((true, false, "none")));
    }

    // The settings page shows the player's value, dimmed, with the reason above the hint
    [Test]
    public void AHeldKnobsRowIsLocked()
    {
        var pages = new KometPages(new HudSettings(), () => { }, _ => { }, () => { }).Build();
        var render = pages.Single(p => p.Id == "komet-render");
        var row = Enumerable.Range(0, render.Count).Select(i => render[i])
            .Single(r => r.Label == KometPages.T("frustumsweep"));
        Assert.That((row.IsEnabled, row.Locked?.Invoke()), Is.EqualTo((true, (string?)null)));
        _ = Features.Hold(Features.Find("FrustumSweep"));
        Assert.Multiple(() =>
        {
            Assert.That(row.IsEnabled, Is.False);
            Assert.That(row.Locked?.Invoke(), Is.EqualTo(HudText.Translate("settings-held")));
            Assert.That(row.Get(), Is.EqualTo(1), "the player's value");
        });
    }

    // An installed feature at its knob's engine value is off, held it is held off; the HUD lists what is not active
    [Test]
    public void TheHudListsWhatIsNotActive()
    {
        using var rig = new FeatureRig("MenuBlur");
        var before = Backdrop.Enabled;
        try
        {
            Backdrop.Enabled = true;
            Features.Poll();
            var active = Features.NotActive;
            Assert.That((rig.State, Row("MenuBlur")), Is.EqualTo((FeatureState.Active, -1)));
            Backdrop.Enabled = false;
            Features.Poll();
            Assert.That((rig.State, Features.NotActive), Is.EqualTo((FeatureState.Off, active + 1)));
            _ = Features.Hold(rig.Feature);
            Assert.That(Features.ShownState(Row("MenuBlur")), Is.EqualTo(FeatureState.HeldOff));
        }
        finally
        {
            Features.Close();
            Backdrop.Enabled = before;
        }
    }

    private static int Row(string id)
    {
        return Enumerable.Range(0, Features.MaxShown)
            .FirstOrDefault(i => Features.Shown(i).StartsWith(id + ":", StringComparison.Ordinal), -1);
    }

    // A transpiler of another mod on the body the feature replaces stands it down at the recheck, and it comes back once that is gone
    [TestCase("AnimationFrames")]
    [TestCase("InitOnce")]
    [TestCase("ShapeInitMemo")]
    [TestCase("AnimatableCulling")]
    [TestCase("IdleAnimators")]
    public void AProbeSeesAnotherModOnTheFirstSeam(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var rig = new FeatureRig(id);
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
        var seam = All()[rig.Feature].Shapes[0].Methods()[0];
        using (var foreign = new TestHarmony("komet-test-feature-foreign"))
        {
            _ = foreign.Patch(seam, transpiler: Foreign.Transpiler);
            Features.Recheck();
            Assert.That(rig.State, Is.EqualTo(FeatureState.StoodDown));
        }

        Features.Recheck();
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
    }

    // A server feature on a remote server is installed without its patches and not applicable
    [Test]
    public void AServerFeatureDoesNotApplyOnARemoteServer()
    {
        using var harmony = new TestHarmony("komet-test-feature-remote");
        var feature = Features.Find("ColumnNoiseScratch");
        Features.InstallAt(new FeatureContext(harmony, null!, new QuietLogger(), false), feature);
        Assert.Multiple(() =>
        {
            Assert.That(Features.Evaluate(feature), Is.EqualTo(FeatureState.NotApplicable));
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
        });
    }

    // Every knob under the name it is saved as, a switch as a bool; a file read back and saved again is the same file
    [Test]
    public void TheSettingsFileKeepsItsKeys()
    {
        var json = JsonConvert.SerializeObject(new HudSettings());
        var loaded = JsonConvert.DeserializeObject<HudSettings>(json)!;
        var logger = new CapturingLogger();
        loaded.ApplyKnobs(logger);
        Assert.Multiple(() =>
        {
            Assert.That(JObject.Parse(json).Properties().Select(p => p.Name + ":" + p.Value.Type),
                Is.EqualTo(SettingsFile));
            Assert.That(JsonConvert.SerializeObject(loaded), Is.EqualTo(json));
            Assert.That(logger.Lines, Is.Empty);
        });
    }
}

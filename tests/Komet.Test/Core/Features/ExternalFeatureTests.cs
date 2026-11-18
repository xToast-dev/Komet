using Komet.Bench;
using Komet.Core;
using Komet.Hud;
using Komet.Api;
using Komet.Options;
using Komet.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Core;

[NonParallelizable]
public sealed class ExternalFeatureTests
{
    private TestHarmony _komet = null!;
    private CapturingLogger _log = null!;
    private int _mainThread;
    private Water _water = null!;

    private static MethodInfo Seam => AccessTools.Method(typeof(ExternalFeatureTests), nameof(Patched));
    private static MethodInfo Skipped => AccessTools.Method(typeof(ExternalFeatureTests), nameof(Replaced));

    [SetUp]
    public void Open()
    {
        (_log, _komet, _water) = (new CapturingLogger(), new TestHarmony("komet-test-external"), new Water());
        ModEvents.Logger = _log;
        (_mainThread, RuntimeEnv.MainThreadId) = (RuntimeEnv.MainThreadId, Environment.CurrentManagedThreadId);
    }

    [TearDown]
    public void Close()
    {
        FeatureLifecycle.Close();
        OptionPages.Clear();
        ModEvents.Logger = null;
        _komet.Dispose();
        RuntimeEnv.MainThreadId = _mainThread;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Patched() => 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Replaced() => 2;

    private FeatureContext Start(bool localServer = true)
    {
        var context = new FeatureContext(_komet, null!, _log, localServer);
        FeatureLifecycle.InstallQueued(context);
        return context;
    }

    private static string Owners(MethodBase method) => string.Join(" ", Harmony.GetPatchInfo(method)?.Owners ?? []);

    private static void Patch(Harmony harmony, MethodBase method, HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null) => _ = harmony.Patch(method, prefix, postfix);

    private string Applied() => string.Join(" ", _water.Applied);

    [Test]
    public void RegisteredBeforeKometStartsItInstallsAfterKometsMainStage()
    {
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        Assert.That((_water.Installs, FeatureHub.StateOf("mymod:water")), Is.EqualTo((0, KapiFeatureState.Pending)));
        _ = Start();
        Assert.Multiple(() =>
        {
            Assert.That((_water.Installs, FeatureHub.StateOf("mymod:water")), Is.EqualTo((1, KapiFeatureState.Active)));
            Assert.That(Applied(), Is.EqualTo("True"), "the player's value, once");
            Assert.That(Owners(Seam), Is.EqualTo("mymod:water"), "under its own Harmony id");
            Assert.That(FeatureHub.Snapshot()[^1].Owner, Is.EqualTo("mymod"));
        });
    }

    [Test]
    public void RegisteredWhileKometRunsItInstallsAtOnce()
    {
        _ = Start();
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        Assert.That((_water.Installs, FeatureHub.StateOf("mymod:water")), Is.EqualTo((1, KapiFeatureState.Active)));
    }

    [Test]
    public void ARegistrationIsRefusedWithItsReason()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.Register("MyMod", new KapiFeature("water", "Water")), Is.False);
            Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "")), Is.False);
            Assert.That(FeatureHub.Register("mymod", new KapiFeature("range", "Range")
                { Knob = KapiKnob.Range(1, 5, 0, () => 1, _ => { }, _ => { }) }), Is.False);
            Assert.That(
                FeatureHub.Register("mymod", new KapiFeature("watcher", "Watcher") { Watched = () => [Seam] }),
                Is.False, "a stand-down without a knob would leave its patches in");
            Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
            Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.False, "taken");
        });
        _ = Start();
        Features.Recheck();
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.Register("mymod", _water.Definition("ice")), Is.False);
            Assert.That(_log.Lines, Has.Count.EqualTo(6).And.All.Contain("is not registered"));
            Assert.That(_log.Lines[^1], Does.Contain("the world has loaded"));
        });
    }

    // In singleplayer a mod's Start runs again on the server's thread: that call neither registers nor holds, and warns not
    [Test]
    public void ACallOffTheMainThreadIsIgnored()
    {
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId + 1;
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.False);
            Assert.That(FeatureHub.HoldOff("MenuBlur", "mymod", "test").IsHolding, Is.False);
            Assert.That(_log.Lines, Has.Count.EqualTo(2).And.All.StartWith("Debug Komet: "));
        });
    }

    [Test]
    public void AFingerprintMismatchLeavesItUninstalled()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "Water")
        {
            Install = (_, _) => _water.Installs++, Shaped = () => [Seam], Fingerprint = 1
        }), Is.True);
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("ice", "Ice")
        {
            Install = (_, _) => _water.Installs++, Shaped = () => [Seam],
            Fingerprint = FeatureHub.Fingerprint([Seam])
        }), Is.True);
        _ = Start();
        Assert.Multiple(() =>
        {
            Assert.That(_water.Installs, Is.EqualTo(1), "only the matching one");
            Assert.That(FeatureHub.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.EngineChanged));
            Assert.That(FeatureHub.StateOf("mymod:ice"), Is.EqualTo(KapiFeatureState.Active));
            Assert.That(_log.Lines.Single(), Does
                .Contain("mymod:water is not installed, the engine methods it replaces")
                .And.Contain("are not the bodies mymod pinned"));
        });
    }

    // An install that throws halfway: its patches go, it failed, and the next feature still installs
    [Test]
    public void AThrowingInstallFailsAndLeavesNoPatch()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("broken", "Broken")
        {
            Install = (harmony, _) =>
            {
                Patch(harmony, Seam, postfix: Foreign.Postfix);
                throw new InvalidOperationException("the mod's bug");
            }
        }), Is.True);
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.StateOf("mymod:broken"), Is.EqualTo(KapiFeatureState.Failed));
            Assert.That(FeatureHub.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.Active));
            Assert.That(Owners(Seam), Is.EqualTo("mymod:water"));
            Assert.That(_log.Lines.Single(), Does.Contain("mymod:broken threw and is taken out"));
        });
    }

    [Test]
    public void APartialInstallIsCleanedAtOnceAndOnlyOnce()
    {
        var cleanups = 0;
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("partial", "Partial")
        {
            Install = (harmony, _) =>
            {
                Patch(harmony, Seam, postfix: Foreign.Postfix);
                throw new InvalidOperationException("partial install");
            },
            Uninstall = () => cleanups++
        }), Is.True);
        _ = Start();
        Assert.That(cleanups, Is.EqualTo(1));
        Assert.That(Owners(Seam), Is.Empty);
        FeatureLifecycle.Close();
        Assert.That(cleanups, Is.EqualTo(1));
    }

    [Test]
    public void AThrowingUninstallStillRemovesPatchesAndCleansOtherFeatures()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("broken", "Broken")
        {
            Install = (harmony, _) => Patch(harmony, Seam, postfix: Foreign.Postfix),
            Uninstall = () => throw new InvalidOperationException("cleanup failed")
        }), Is.True);
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        FeatureLifecycle.Close();
        Assert.That(Owners(Seam), Is.Empty);
        Assert.That(_water.Uninstalls, Is.EqualTo(1));
        Assert.That(_log.Lines, Has.Some.Contains("threw in its uninstall"));
    }

    [Test]
    public void ANullWatchedListStandsDownInsteadOfIgnoringTheGuard()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("watched", "Watched")
        {
            Knob = _water.Definition().Knob,
            Watched = () => null!
        }), Is.True);
        _ = Start();
        Assert.That(FeatureHub.StateOf("mymod:watched"), Is.EqualTo(KapiFeatureState.StoodDown));
        Assert.That(Applied(), Does.EndWith("False"));
    }

    [Test]
    public void RuntimeShaderModChangesRefreshPatchCompatibility()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("watched", "Watched")
        {
            Knob = _water.Definition().Knob,
            Watched = () => [Skipped]
        }), Is.True);
        _ = Start();
        using (var other = new TestHarmony("late-shader-mod"))
        {
            _ = other.Patch(Skipped, Foreign.Prefix);
            Links.ShaderUi.ModStarted(Answers.NullClient, "late-shader-mod");
            Assert.That(FeatureHub.StateOf("mymod:watched"), Is.EqualTo(KapiFeatureState.StoodDown));
        }

        Links.ShaderUi.ModStopped("late-shader-mod");
        Assert.That(FeatureHub.StateOf("mymod:watched"), Is.EqualTo(KapiFeatureState.Active));
    }

    [Test]
    public void AnOversizedWatchedListDoesNotHideConflictsPastTheLimit()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("watched", "Watched")
        {
            Knob = _water.Definition().Knob,
            Watched = () => [.. Enumerable.Repeat<MethodBase?>(Seam, EngineShape.MaxSeams + 1)]
        }), Is.True);
        _ = Start();
        Assert.That(FeatureHub.StateOf("mymod:watched"), Is.EqualTo(KapiFeatureState.StoodDown));
    }

    // The feature's own patch there is no reason to stand down.
    [Test]
    public void AnotherModsPatchOnAWatchedMethodStandsItDown()
    {
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "Water")
        {
            Knob = _water.Definition().Knob, Watched = () => [Skipped],
            Install = (harmony, _) => Patch(harmony, Skipped, Foreign.Prefix)
        }), Is.True);
        _ = Start();
        Assert.That(FeatureHub.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.Active));
        using (var other = new TestHarmony("komet-test-external-other"))
        {
            _ = other.Patch(Skipped, Foreign.Prefix);
            Features.Recheck();
            Assert.That(FeatureHub.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.StoodDown));
        }

        Features.Recheck();
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.Active));
            Assert.That(Applied(), Is.EqualTo("True False True"));
            Assert.That(_log.Lines, Has.Count.EqualTo(2).And.All.StartWith("Notification Komet mymod:water: "));
        });
    }

    [Test]
    public void AServerFeatureDoesNotApplyOnARemoteServer()
    {
        var water = _water.Definition();
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "Water")
            { Install = water.Install, ServerOnly = true }), Is.True);
        _ = Start(false);
        Assert.That((_water.Installs, FeatureHub.StateOf("mymod:water")),
            Is.EqualTo((0, KapiFeatureState.NotApplicable)));
    }

    [Test]
    public void ClosingTheWorldUninstallsItAndOpensTheIdAgain()
    {
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        Features.Recheck();
        FeatureLifecycle.Close();
        Assert.Multiple(() =>
        {
            Assert.That((_water.Installs, _water.Uninstalls), Is.EqualTo((1, 1)));
            Assert.That(Owners(Seam), Is.Empty);
            Assert.That((Knobs.Count, FeatureHub.StateOf("mymod:water")),
                Is.EqualTo((Knobs.BuiltInCount, KapiFeatureState.Unknown)));
        });
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        Assert.That((_water.Installs, Knobs.Count), Is.EqualTo((2, Knobs.BuiltInCount + 1)));
    }

    // On Komet's render page under its group, locked while another mod holds it; the player's choice goes to the mod and applies
    [Test]
    public void ItsRowStandsOnKometsPageAndIsLockedWhileHeld()
    {
        GameLang.EnsureLoaded();
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "Water")
        {
            Knob = _water.Definition().Knob, Page = FeatureHub.RenderPage, Group = "My mod", Hint = "Faster water"
        }), Is.True);
        _ = Start();
        var render = new KometPages(new HudSettings(),
                new KometActions(() => { }, _ => { }, () => { }, () => { }, () => { })).Build()
            .Single(p => p.Id == FeatureHub.RenderPage);
        var rows = Enumerable.Range(0, render.Count).Select(i => render[i]).ToList();
        var row = rows.Single(r => r.Label == "Water");
        Assert.That((rows[rows.IndexOf(row) - 1].Label, row.Hint, row.Get()),
            Is.EqualTo(("My mod", "Faster water", 1.0)));
        row.Set(0);
        Assert.That((_water.Saved, _water.Applied[^1]), Is.EqualTo((false, false)));
        using var hold = FeatureHub.HoldOff("mymod:water", "othermod", "measuring");
        row.Set(1);
        Assert.Multiple(() =>
        {
            Assert.That(row.IsEnabled, Is.False);
            Assert.That(row.Locked?.Invoke(), Does.EndWith("othermod: measuring"));
            Assert.That((_water.Saved, _water.Applied[^1]), Is.EqualTo((true, false)), "saved, applied once free");
        });
        hold.Release();
        Assert.That(_water.Applied[^1], Is.True);
    }

    // On the mod's own page, after the rows it added itself; the page it registered stays as it built it
    [Test]
    public void ItsRowStandsOnTheModsOwnPage()
    {
        var page = OptionPages.Page("mymod", "My mod").Switch("Rain", () => true, _ => { });
        Assert.That(FeatureHub.Register("mymod", new KapiFeature("water", "Water")
            { Knob = _water.Definition().Knob, Page = "mymod" }), Is.True);
        var shown = Features.Placed(page);
        Assert.Multiple(() =>
        {
            Assert.That((page.Count, shown.Count, shown.Id), Is.EqualTo((1, 2, "mymod")));
            Assert.That((shown[0].Label, shown[1].Label), Is.EqualTo(("Rain", "Water")));
            Assert.That(Features.Placed(OptionPages.Page("other", "Other")).Count, Is.Zero);
        });
    }

    // modid:name in a bench arm resolves to the registered knob; one no mod registered ends the run
    [Test]
    public void ABenchArmSetsItByModIdAndName()
    {
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        var arms = new[] { new BenchArm("dry", false, [new BenchSetting("mymod:water", 0)]) };
        var values = BenchDriver.ArmValues(Knobs.Snapshot(), arms, out var error);
        var unknown = new[] { new BenchArm("ice", false, [new BenchSetting("mymod:ice", 0)]) };
        _ = BenchDriver.ArmValues(Knobs.Snapshot(), unknown, out var missing);
        Assert.Multiple(() =>
        {
            Assert.That((error, values[0][Knobs.Find("mymod:water")]), Is.EqualTo(((string?)null, 0)));
            Assert.That(missing, Is.EqualTo("bench.json: arms[0].set.mymod:ice names no registered knob"));
            Assert.That(Knobs.Apply(values[0]), Is.EqualTo(1));
            Assert.That(_water.Applied[^1], Is.False);
        });
    }

    // komet.json saves Komet's knobs alone: the mod saves its own
    [Test]
    public void TheSettingsFileKeepsKometsKnobsAlone()
    {
        var before = Keys();
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        _ = Start();
        Assert.That(Keys(), Is.EqualTo(before).And.No.Contain("mymod:water"));
    }

    private static string[] Keys() =>
        [.. JObject.Parse(JsonConvert.SerializeObject(new HudSettings())).Properties().Select(p => p.Name)];

    // A mod's switch as a mod writes it: the player's value in its own config, its patch reading what Komet applies
    private sealed class Water
    {
        public bool Saved { get; private set; } = true;
        public List<bool> Applied { get; } = [];
        public int Installs { get; set; }
        public int Uninstalls { get; private set; }

        public KapiFeature Definition(string name = "water") =>
            new(name, "Water")
            {
                Knob = KapiKnob.Switch(() => Saved, on => Saved = on, Applied.Add),
                Install = (harmony, _) =>
                {
                    Installs++;
                    Patch(harmony, Seam, postfix: Foreign.Postfix);
                },
                Uninstall = () => Uninstalls++
            };
    }
}

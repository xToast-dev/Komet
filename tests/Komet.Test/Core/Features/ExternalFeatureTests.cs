using Komet.Core;
using Komet.Api;

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

    private static void Patch(Harmony harmony, MethodBase method, HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null) => _ = harmony.Patch(method, prefix, postfix);

    private string Applied() => string.Join(" ", _water.Applied);

    [Test]
    public void RegisteredWhileKometRunsItInstallsAtOnce()
    {
        _ = Start();
        Assert.That(FeatureHub.Register("mymod", _water.Definition()), Is.True);
        Assert.That((_water.Installs, FeatureHub.StateOf("mymod:water")), Is.EqualTo((1, KapiFeatureState.Active)));
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

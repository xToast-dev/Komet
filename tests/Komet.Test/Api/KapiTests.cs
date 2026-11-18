using Komet.Core;
using Komet.Api;
using Komet.Rendering;
using GameMod = Vintagestory.API.Common.Mod;

namespace Komet.Test.Api;

// KAPI: versioning, the connection's lifetime (Dispose, world end) and what it forwards to Komet's internal API
[NonParallelizable]
public sealed class KapiTests
{
    private CapturingLogger _log = null!;
    private int _mainThread;

    [SetUp]
    public void Open()
    {
        _log = new CapturingLogger();
        ModEvents.Logger = _log;
        (_mainThread, RuntimeEnv.MainThreadId) = (RuntimeEnv.MainThreadId, Environment.CurrentManagedThreadId);
    }

    [TearDown]
    public void Close()
    {
        WorldEnd.Run();
        FeatureLifecycle.Close();
        ModShaders.Clear();
        ModEvents.Logger = null;
        RuntimeEnv.MainThreadId = _mainThread;
    }

    [Test]
    public void ADisposedRegistrationFreesItsSlot()
    {
        using var komet = Connect();
        for (var i = 0; i < KapiMod.MaxRegistrations + 8; i++) komet.Shaders.Uniform("mymodWarmth", () => 1f).Dispose();

        using var last = komet.Shaders.Uniform("mymodWarmth", () => 1f);
        Assert.That(last.Active, Is.True, last.Error);
    }

    private static KapiMod Connect(string id = "mymod") => Kapi.Connect(id, "1.0")!;

    private static string PageIds() =>
        string.Join(" ", Enumerable.Range(0, OptionPages.Count).Select(i => OptionPages.At(i).Id));

    private static string StyleIds() =>
        string.Join(" ", Enumerable.Range(0, StyleHub.Count).Select(i => StyleHub.At(i).Id));

    [TestCase("1.0", true)]
    [TestCase("1.1", false)]
    [TestCase("2.0", false)]
    [TestCase("0.9", false)]
    [TestCase("", false)]
    [TestCase("x.y", false)]
    [TestCase("1", false)]
    public void OnlyVersionOneZeroIsSupported(string version, bool supported)
    {
        Assert.That(Kapi.Supports(version), Is.EqualTo(supported));
        Assert.That(Kapi.Version, Is.EqualTo("1.0"));
    }

    [Test]
    public void AnUnsupportedVersionOrABadIdIsRefusedAndLogged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Kapi.Connect("mymod", "2.0"), Is.Null);
            Assert.That(Kapi.Connect("", "1.0"), Is.Null);
            Assert.That(Kapi.Connect("my:mod", "1.0"), Is.Null);
            Assert.That(_log.Lines, Has.Count.EqualTo(3).And.All.StartWith("Warning Komet: KAPI did not connect"));
            Assert.That(_log.Lines[0], Does.Contain("KAPI 2.0"));
        });
    }

    [Test]
    public void AModSystemConnectsUnderItsModsId()
    {
        var mod = new TestMod();
        AccessTools.Property(typeof(GameMod), nameof(GameMod.Info)).SetValue(mod, new ModInfo { ModID = "mymod" });
        var system = new TestSystem();
        AccessTools.Property(typeof(ModSystem), nameof(ModSystem.Mod)).SetValue(system, mod);
        using var komet = Kapi.Connect(system, "1.0");
        Assert.That((komet?.ModId, komet?.Version, komet?.IsConnected), Is.EqualTo(("mymod", "1.0", true)));
        Assert.That(Kapi.Connect(new TestSystem(), "1.0"), Is.Null, "a system without a mod has no id");
    }

    [Test]
    public void DisposeTakesEverythingTheConnectionRegisteredAway()
    {
        var komet = Connect();
        var calls = 0;
        EventHandler counted = (_, _) => calls++;
        _ = komet.Options.Page("My mod");
        Assert.That(komet.Options.Page("look", "Look", null).Id, Is.EqualTo("mymod:look"));
        Assert.That(komet.Styles.Register(new KapiStyle("mymod-night", "Night")), Is.True);
        var uniform = komet.Shaders.Uniform("mymodWarmth", () => 1f);
        var hold = komet.Features.HoldOff("FrustumSweep", "test");
        komet.Options.Applied += counted;
        komet.Styles.Changed += counted;
        komet.Shaders.RendererChanged += counted;
        komet.Features.StateChanged += (_, _) => calls++;
        Assert.That((uniform.Active, hold.IsHolding, ModShaders.Count("mymod").Uniforms), Is.EqualTo((true, true, 1)));
        Assert.That(PageIds(), Is.EqualTo("mymod mymod:look"));

        komet.Dispose();
        komet.Dispose();
        OptionPages.RaiseApplied();
        _ = StyleHub.Register(new StyleData { Id = "other" });
        ShaderHub.RaiseRendererChanged();
        FeatureHub.Raise("x", KapiFeatureState.Pending, KapiFeatureState.Active);
        Assert.Multiple(() =>
        {
            Assert.That(komet.IsConnected, Is.False);
            Assert.That(PageIds(), Is.Empty);
            Assert.That(StyleIds(), Is.EqualTo("other"));
            Assert.That((uniform.Active, hold.IsHolding, ModShaders.Count("mymod").Uniforms),
                Is.EqualTo((false, false, 0)));
            Assert.That(calls, Is.Zero, "every handler unsubscribed");
            Assert.That(_log.Lines, Is.Empty);
        });
    }

    [Test]
    public void RegistrationsAfterDisposeAreRefusedOrUndoneAtOnce()
    {
        var komet = Connect();
        komet.Dispose();
        var calls = 0;
        komet.Options.Applied += (_, _) => calls++;
        _ = komet.Options.Page("My mod");
        var uniform = komet.Shaders.Uniform("mymodWarmth", () => 1f);
        var hold = komet.Features.HoldOff("FrustumSweep", "test");
        OptionPages.RaiseApplied();
        Assert.Multiple(() =>
        {
            Assert.That((uniform.Active, uniform.Error), Is.EqualTo((false, "the connection is closed")));
            Assert.That(ModShaders.Count("mymod").Uniforms, Is.Zero);
            Assert.That(hold.IsHolding, Is.False);
            Assert.That(komet.Styles.Register(new KapiStyle("mymod-night", "Night")), Is.False);
            Assert.That(komet.Features.Register(new KapiFeature("water", "Water")), Is.False);
            Assert.That((PageIds(), StyleIds(), calls), Is.EqualTo(("", "", 0)));
        });
    }

    [Test]
    public void WorldEndDisconnectsEveryMod()
    {
        var (one, two) = (Connect("one"), Connect("two"));
        _ = one.Options.Page("One");
        WorldEnd.Run();
        Assert.That((one.IsConnected, two.IsConnected, PageIds()), Is.EqualTo((false, false, "")));
        Assert.That(Connect("one").IsConnected, Is.True, "the next world connects anew");
    }

    [Test]
    public void ARefusedRegistrationSaysWhy()
    {
        using var komet = Connect();
        var empty = komet.Shaders.TryAddPasses();
        var withNull = komet.Shaders.TryAddPasses(null!);
        var noRenderer = komet.Shaders.TryAddPasses(new KapiPass("grade", "void main() {}"));
        var invalid = komet.Shaders.AddPass(new KapiPass("", ""));
        var backend = Services.Swap<IShaderBackend>(null);
        KapiRegistration noBackend;
        try
        {
            noBackend = komet.Shaders.TryAddPasses(new KapiPass("grade", "void main() {}"));
        }
        finally
        {
            _ = Services.Swap(backend);
        }

        Assert.Multiple(() =>
        {
            Assert.That((empty.Active, empty.Error), Is.EqualTo((false, "one or more passes are needed")));
            Assert.That((withNull.Active, withNull.Error), Is.EqualTo((false, "one or more passes are needed")));
            Assert.That((noRenderer.Active, noRenderer.Error), Is.EqualTo((false, (string?)
                "No active fullscreen renderer or current graphics context")));
            Assert.That((invalid.Active, invalid.Error is not null), Is.EqualTo((false, true)));
            Assert.That((noBackend.Active, noBackend.Error), Is.EqualTo((false, "Komet shader backend unavailable")));
        });
    }

    [Test]
    public void MeasureBooksUnderTheModsIdOnlyDuringACapture()
    {
        using var komet = Connect();
        Assert.That(komet.Debug.Measure("grade"), Is.EqualTo(default(KapiTiming)), "no capture: nothing");
        DebugTimings.Capturing = true;
        using (komet.Debug.Measure("grade"))
        {
            Assert.That(KapiDebug.Capturing, Is.True);
        }

        Assert.That(string.Join(" ", DebugTimings.TakeTimings().Select(t => t.Name + "×" + t.Calls)), Is.EqualTo("mymod:grade×1"));
    }

    [Test]
    public void FeaturesCarryTheModsIdAndTheirState()
    {
        using var komet = Connect();
        var on = true;
        Assert.That(komet.Features.Register(new KapiFeature("water", "Water")
        {
            Knob = KapiKnob.Switch(() => on, v => on = v, _ => { }), Install = (_, _) => { }
        }), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(KapiFeatures.StateOf("mymod:water"), Is.EqualTo(KapiFeatureState.Pending));
            Assert.That(KapiFeatures.StateOf("Nothing"), Is.EqualTo(KapiFeatureState.Unknown));
            Assert.That((KapiFeatures.Snapshot()[^1].Id, KapiFeatures.Snapshot()[^1].Owner),
                Is.EqualTo(("mymod:water", "mymod")));
        });
    }

    private sealed class TestMod : GameMod;

    private sealed class TestSystem : ModSystem;
}

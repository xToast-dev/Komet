using Komet.Api;
using Komet.Core;
using Komet.Rendering;
using GameMod = Vintagestory.API.Common.Mod;

namespace Komet.Test.Api;

// KAPI: versioning, the connection's lifetime (Dispose, world end) and what it forwards to Komet's internal API
[NonParallelizable]
public sealed class KapiTests
{
    private string _assets = "";

    private CapturingLogger _log = null!;
    private int _mainThread;

    // Option pages name their section in Komet's language
    [OneTimeSetUp]
    public void Language() => _assets = GameLang.LoadKomet();

    [OneTimeTearDown]
    public void RestoreLanguage()
    {
        GameLang.LoadEnglish();
        Directory.Delete(_assets, true);
    }

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

    private static KapiMod Connect(string id = "mymod") => Kapi.Connect(id, "1.0")!;

    private static string PageIds() =>
        string.Join(" ", Enumerable.Range(0, OptionPages.Count).Select(i => OptionPages.At(i).Id));

    private static string StyleIds() =>
        string.Join(" ", Enumerable.Range(0, StyleHub.Count).Select(i => StyleHub.At(i).Id));

    [TestCase("1.0", true)]
    [TestCase("0.9", false)]
    public void OnlyVersionOneZeroIsSupported(string version, bool supported)
    {
        Assert.That(Kapi.Supports(version), Is.EqualTo(supported));
        Assert.That(Kapi.Version, Is.EqualTo("1.0"));
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
    public void WorldEndDisconnectsEveryMod()
    {
        var (one, two) = (Connect("one"), Connect("two"));
        _ = one.Options.Page("One");
        WorldEnd.Run();
        Assert.That((one.IsConnected, two.IsConnected, PageIds()), Is.EqualTo((false, false, "")));
        Assert.That(Connect("one").IsConnected, Is.True, "the next world connects anew");
    }

    private sealed class TestMod : GameMod;

    private sealed class TestSystem : ModSystem;
}

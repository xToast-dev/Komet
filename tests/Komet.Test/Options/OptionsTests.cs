using Komet.Core;
using Komet.Hud;
using Komet.Options;

namespace Komet.Test.Options;

public sealed class OptionsTests
{
    // the API's page ids for other mods' rows are these pages
    private static readonly string[] Areas =
        [FeatureHub.RenderPage, FeatureHub.ChunksPage, FeatureHub.MiscPage, "komet-vulkan"];

    private static readonly KometActions Actions = new(() => { }, _ => { }, () => { }, () => { }, () => { });

    [OneTimeSetUp]
    public void LoadTheGamesLanguage() => GameLang.LoadEnglish();

    [TearDown]
    public void Forget() => OptionPages.Clear();

    // The Overview first, the areas, then HUD and tools; every knob page is one of the areas; "advanced" adds rows, no pages
    [Test]
    public void KometHasItsHudPagesAndOnePagePerKnobPage()
    {
        var basic = new KometPages(new HudSettings(), Actions).Build();
        var pages = new KometPages(new HudSettings { ShowAdvanced = true }, Actions)
            .Build();
        var knobPages = Knobs.BuiltIn.ToArray().Select(k => k.Page).OfType<string>().Distinct()
            .Select(p => "komet-" + p);
        var rows = pages.SelectMany(p => Enumerable.Range(0, p.Count).Select(i => p[i].Label)).ToArray();
        string[] ids = [KometPages.Overview, .. Areas, KometPages.Hud, KometPages.Tools];
        Assert.Multiple(() =>
        {
            Assert.That(basic.Select(p => p.Id), Is.EqualTo(ids));
            Assert.That(pages.Select(p => p.Id),
                Is.EqualTo([
                    KometPages.Overview, .. Areas, KometPages.ExactId, KometPages.Hud,
                    KometPages.Tools
                ]));
            Assert.That(knobPages, Is.SubsetOf(Areas));
            Assert.That(pages, Has.All.Matches<OptionPage>(p => p.Count is > 0 and <= OptionPage.MaxRows));
            Assert.That(pages.Sum(p => p.Count), Is.GreaterThan(basic.Sum(p => p.Count) + 60),
                "advanced lists every exact switch");
            Assert.That(rows, Does.Not.Contain(""), "every row has a label");
        });
    }

    // A knob's Needs names another built-in knob, and a row whose needed knob is off takes no input
    [Test]
    public void EveryNeededKnobExists()
    {
        var knobs = Knobs.BuiltIn.ToArray();
        var settings = new HudSettings { ShowAdvanced = true };
        var vulkan = new KometPages(settings, Actions).Build().Single(p => p.Id == "komet-vulkan");
        var occlusion = Enumerable.Range(0, vulkan.Count).Select(i => vulkan[i])
            .Single(r => r.Label == OptionsText.T("occlusionculling"));
        settings.Knobs.SetKnob(Knobs.Find("VulkanOnly"), 0);
        Assert.Multiple(() =>
        {
            Assert.That(
                knobs.Where(k => k.Needs is { } needs && (Knobs.Find(needs) < 0 || needs == k.Key)).Select(k => k.Key),
                Is.Empty);
            Assert.That(knobs.Count(k => k.Needs is not null), Is.GreaterThanOrEqualTo(9));
            Assert.That(occlusion.IsEnabled, Is.False, "VulkanOnly, which it needs, is off");
        });
    }

    // Two choices, OpenGL and Vulkan: the mode is VulkanOnly alone, every other knob is left as it is
    [TestCase(0)]
    [TestCase(1)]
    public void TheVulkanModeIsTheVulkanOnlySwitch(int mode)
    {
        var values = Enumerable.Repeat(7, Knobs.Count).ToArray();
        VulkanMode.Set(mode, (knob, value) => values[knob] = value);
        var only = Knobs.Find("VulkanOnly");
        Assert.Multiple(() =>
        {
            Assert.That(VulkanMode.Of(knob => values[knob]), Is.EqualTo(mode));
            Assert.That(values[only], Is.EqualTo(mode));
            Assert.That(values.Where((_, i) => i != only), Is.All.EqualTo(7), "no other knob written");
            Assert.That(string.Join(" ", VulkanMode.Choices.Select(c => c.Name)), Is.EqualTo("opengl vulkan"));
            Assert.That(VulkanMode.Choice(mode), Is.EqualTo(mode));
        });
    }
}

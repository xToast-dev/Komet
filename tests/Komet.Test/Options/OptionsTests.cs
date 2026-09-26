using Newtonsoft.Json.Linq;

namespace Komet.Test.Options;

// The options window's model: the pages a mod registers, the game's graphics tab as pages, Komet's own, and the engine bodies the
// replacement of the graphics tab was written against.
public sealed class OptionsTests
{
    private static readonly string[] GameTabs =
        ["vs-general", "vs-quality", "vs-performance", "vs-mouse", "vs-accessibility", "vs-sound", "vs-interface"];

    // The game's own language file, as the client loads it: what the labels of the graphics tab come from
    [OneTimeSetUp]
    public void LoadTheGamesLanguage()
    {
        GameLang.LoadEnglish();
    }

    [TearDown]
    public void Forget()
    {
        KometOptions.Clear();
    }

    // Labels, hints and choice names are the game's lang keys (translated where the game's language is loaded), or Komet's
    [Test]
    public void EveryGameOptionShowsWhatTheGameTranslates()
    {
        var lang = JObject.Parse(File.ReadAllText(Path.Combine(GameInstall.Assets, "game", "lang", "en.json")));
        var komet = JObject.Parse(File.ReadAllText(Path.Combine(Paths.KometDir, "assets", "komet", "lang", "en.json")));
        var known = lang.Properties().Concat(komet.Properties()).SelectMany(p => new[] { p.Name, p.Value.ToString() }).ToHashSet();
        var pages = EngineOptions.Pages(Answers.NullClient, _ => { });
        var missing = new List<string>();
        foreach (var page in pages)
            for (var i = 0; i < page.Count; i++)
            {
                var row = page[i];
                missing.AddRange(new[] { row.Label, row.Hint ?? row.Label }.Concat(row.Names).Where(t => !known.Contains(t)));
            }

        Assert.Multiple(() =>
        {
            Assert.That(pages.Select(p => p.Id), Is.SupersetOf(GameTabs));
            Assert.That(pages.Sum(p => p.Count), Is.GreaterThanOrEqualTo(28 + 6 + 6 + 9 + 12), "every control of the game's tabs");
            Assert.That(missing, Is.Empty);
        });
    }

    // The controls page: every hotkey of the game once, as a key row under the heading of its kind, the mouse modifiers dimmed
    // while they follow sneak and sprint; two keys bound alike both say so
    [Test]
    public void EveryHotkeyHasItsRowOnTheControlsPage()
    {
        var before = ScreenManager.hotkeyManager;
        var saved = ClientSettings.KeyMapping.GetValueOrDefault("walkforward");
        try
        {
            ScreenManager.hotkeyManager = new HotkeyManager();
            ScreenManager.hotkeyManager.RegisterDefaultHotKeys();
            var keys = ScreenManager.hotkeyManager.HotKeys;
            var page = EngineOptions.Pages(Answers.NullClient).Single(p => p.Id == "vs-controls");
            var rows = Enumerable.Range(0, page.Count).Select(i => page[i]).Where(r => r.Kind == OptionKind.Key).ToArray();
            EngineOptions.Bind("walkforward", keys["jump"].CurrentMapping.Clone()); // the same way the screen binds
            var forward = rows.Single(r => r.Code == "walkforward");
            Assert.Multiple(() =>
            {
                Assert.That(rows.Select(r => r.Code), Is.EquivalentTo(keys.Keys));
                Assert.That(forward.Warn!(), Is.True);
                Assert.That(EngineOptions.Clash("jump"), Is.EqualTo(keys["walkforward"].Name));
                Assert.That(rows.Single(r => r.Code == "sprint").Warn!(), Is.False);
            });
        }
        finally
        {
            ScreenManager.hotkeyManager = before;
            if (saved is null) _ = ClientSettings.KeyMapping.Remove("walkforward");
            else ClientSettings.KeyMapping["walkforward"] = saved;
        }
    }

    // The name a language gives itself where the game's font can draw it, the English one after a tab, no completeness figure
    [TestCase("Deutsch / German (100% complete)", "Deutsch\tGerman")]
    [TestCase("English / English", "English")]
    [TestCase("\u0627\u0644\u0639\u0631\u0628\u064a\u0629 / Arabic (100% complete)", "Arabic")]
    [TestCase("Chinese Simplified / \u7b80\u4f53\u4e2d\u6587 (95% complete)", "Chinese Simplified")]
    [TestCase("Espa\u00f1ol, latinoamericano / Spanish, Latin American (80% complete)",
        "Espa\u00f1ol, latinoamericano\tSpanish, Latin American")]
    public void LanguagesAreNamedReadably(string name, string shown)
    {
        Assert.That(EngineOptions.Readable(name), Is.EqualTo(shown));
    }

    [Test]
    public void KometHasItsHudPagesAndOnePagePerKnobPage()
    {
        var pages = new KometPages(new HudSettings(), () => { }, _ => { }, () => { }).Build();
        var knobPages = Knobs.BuiltIn.ToArray().Select(k => k.Page).OfType<string>().Distinct().Select(p => "komet-" + p);
        var rows = pages.SelectMany(p => Enumerable.Range(0, p.Count).Select(i => p[i].Label)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(pages.Select(p => p.Id), Is.EqualTo(new[] { KometPages.Hud, KometPages.Tools }.Concat(knobPages)));
            Assert.That(pages, Has.All.Matches<OptionPage>(p => p.Count is > 0 and <= OptionPage.MaxOptions));
            Assert.That(rows, Does.Not.Contain(""), "every row has a label");
        });
    }

    [Test]
    public void APageRegisteredAgainStartsOver()
    {
        _ = KometOptions.Page("mod", "Mod").Switch("a", () => true, _ => { }).Switch("b", () => true, _ => { });
        var again = KometOptions.Page("mod", "Mod").Switch("c", () => true, _ => { });
        Assert.Multiple(() =>
        {
            Assert.That(KometOptions.Count, Is.EqualTo(1));
            Assert.That(KometOptions.At(0), Is.SameAs(again));
            Assert.That(again.Count, Is.EqualTo(1));
            Assert.That(again.Section, Is.EqualTo("Mod"), "the title stands in for a missing section");
        });
    }

    [Test]
    public void TheWorldClosingDropsEveryPage()
    {
        _ = KometOptions.Page("one", "One");
        _ = KometOptions.Page("two", "Two", "Mods");
        KometOptions.Clear();
        Assert.That(KometOptions.Count, Is.Zero);
    }

    [Test]
    public void RowsCarryTheirValuesAsNumbers()
    {
        var (on, level, pick) = (false, 0.0, 0);
        var page = KometOptions.Page("mod", "Mod").Group("Group")
            .Switch("switch", () => on, v => on = v)
            .Slider("slider", 0, 10, 2.5, () => level, v => level = v, " u")
            .Choice("choice", ["a", "b", "c"], () => pick, i => pick = i);
        page[1].Set(1);
        page[2].Set(page[2].At(0.49));
        page[3].Set(2);
        Assert.Multiple(() =>
        {
            Assert.That(page[0].Kind, Is.EqualTo(OptionKind.Group));
            Assert.That((on, page[1].Get()), Is.EqualTo((true, 1.0)));
            Assert.That(level, Is.EqualTo(5), "a slider snaps to its step");
            Assert.That(page[2].ValueText(level), Is.EqualTo("5 u"));
            Assert.That((pick, page[3].Max), Is.EqualTo((2, 2.0)));
        });
    }

    [Test]
    public void FormatAndEnabledApplyToTheRowAddedLast()
    {
        var enabled = false;
        var page = KometOptions.Page("mod", "Mod")
            .Slider("first", 0, 1, 1, () => 0, _ => { })
            .Slider("second", 0, 1, 1, () => 0, _ => { })
            .Format(v => v > 0 ? "all" : "none").EnabledWhen(() => enabled);
        Assert.Multiple(() =>
        {
            Assert.That(page[0].ValueText(1), Is.EqualTo("1"));
            Assert.That(page[1].ValueText(1), Is.EqualTo("all"));
            Assert.That((page[0].IsEnabled, page[1].IsEnabled), Is.EqualTo((true, false)));
        });
    }

    [Test]
    public void ARowPastTheBoundIsDropped()
    {
        var page = KometOptions.Page("mod", "Mod");
        for (var i = 0; i < OptionPage.MaxOptions + 5; i++) _ = page.Switch("s" + i, () => false, _ => { });
        Assert.That(page.Count, Is.EqualTo(OptionPage.MaxOptions));
    }
}

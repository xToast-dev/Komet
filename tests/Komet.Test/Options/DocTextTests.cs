using Komet.Core;
using Komet.Options;

namespace Komet.Test.Options;

// The in-game docs' Markdown subset: every block kind the reader draws comes out of the parser as written
public sealed class DocTextTests
{

    // Help's pages ship with Komet, Modding's with the Komet SDK
    private static string Docs(string name, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), KometDocs.Help.Contains(name)
            ? "../../../Komet/assets/komet/config/komet-docs"
            : "../../../Komet.Sdk/assets/kometsdk/config/komet-docs"));

    // Every shipped page in every language: a title, headings to jump to, closed fences, and the same outline in both
    [Test]
    public void TheShippedDocsReadWhole()
    {
        foreach (var name in KometDocs.Names)
        {
            var (en, de) = (Read("en", name), Read("de", name));
            Assert.Multiple(() =>
            {
                Assert.That(en.Count(b => b.Kind == DocText.Kind.Title), Is.EqualTo(1), name);
                Assert.That(DocText.Outline(en), Is.Not.Empty, name);
                Assert.That(DocText.Outline(de).Count, Is.EqualTo(DocText.Outline(en).Count), name);
                Assert.That(en.Count(b => b.Kind == DocText.Kind.Code),
                    Is.EqualTo(de.Count(b => b.Kind == DocText.Kind.Code)), name);
                Assert.That(en.Count(b => b.Kind == DocText.Kind.Flow),
                    Is.EqualTo(de.Count(b => b.Kind == DocText.Kind.Flow)), name);
                foreach (var link in en.Concat(de).Where(b => b.Kind == DocText.Kind.PageLink))
                {
                    Assert.That(link.Lines, Has.Length.EqualTo(1), name);
                    Assert.That(KometDocs.Names, Does.Contain(link.Lines![0][5..]), name);
                }
            });
        }

        foreach (var language in new[] { "de", "en" }) // no page left over that nothing shows
        {
            Assert.That(
                Directory.GetFiles(Path.Join(Docs(KometDocs.Help[0]), language), "*.md")
                    .Concat(Directory.GetFiles(Path.Join(Docs(KometDocs.Modding[0]), language), "*.md"))
                    .Select(Path.GetFileNameWithoutExtension),
                Is.EquivalentTo(KometDocs.Names), language);
        }
    }

    // Hotkeys the docs may name: the game's escape menu and Komet's own
    private static readonly string[] KnownKeys =
        ["escapemenudialog", "komet-hud-toggle", "komet-hud-details", "komet-hud-window", "komet-hud-debug"];

    // Every clickable word leads somewhere: a page that exists, a term the glossary of that language explains
    [Test]
    public void EveryLinkTermAndKeyInTheDocsResolves()
    {
        foreach (var language in new[] { "de", "en" })
        {
            var terms = DocText.Terms(Read(language, "glossary"));
            foreach (var name in KometDocs.Names)
            {
                var texts = Read(language, name).SelectMany(b => (b.Rows ?? []).SelectMany(r => r).Append(b.Text))
                    .ToArray();
                Assert.Multiple(() =>
                {
                    foreach (var target in texts.SelectMany(DocText.Targets))
                    {
                        Assert.That(target.StartsWith(DocText.TermTarget, StringComparison.Ordinal)
                                ? terms.ContainsKey(target[DocText.TermTarget.Length..])
                                : KometDocs.Names.Contains(target[5..]), Is.True, $"{language}/{name}: {target}");
                    }

                    foreach (var key in texts.SelectMany(DocText.Keys))
                    {
                        Assert.That(KnownKeys, Does.Contain(key), $"{language}/{name}");
                    }
                });
            }
        }
    }

    private static List<DocText.Block> Read(string language, string name)
    {
        var file = Path.Join(Docs(name), language, name + ".md");
        var text = File.ReadAllText(file);
        Assert.That(text.Split("```").Length % 2, Is.EqualTo(1), file + ": a code fence is not closed");
        Assert.That(text, Does.Not.Contain("—").And.Not.Contain(" – "), file + ": no dashes as punctuation");
        return DocText.Parse(text);
    }
}

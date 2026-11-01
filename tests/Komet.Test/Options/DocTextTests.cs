using Komet.Api;
using Komet.Options;
using Komet.Core;

namespace Komet.Test.Options;

// The in-game docs' Markdown subset: every block kind the reader draws comes out of the parser as written
public sealed class DocTextTests
{
    private static readonly DocText.Kind[] Kinds =
    [
        DocText.Kind.Title, DocText.Kind.Paragraph, DocText.Kind.Heading, DocText.Kind.Bullet, DocText.Kind.Bullet,
        DocText.Kind.Numbered, DocText.Kind.Code, DocText.Kind.Table, DocText.Kind.Rule, DocText.Kind.Subheading
    ];

    private static readonly string[] Code = ["var x = 1;", "  indented();"], Row = ["a1", "link"], Headings = ["Heading", "Sub"];

    private const string Sample = """
        # Title
        Intro **bold** and `code`
        goes on.

        ## Heading
        - one
          - nested
        1. first
          continued
        ```csharp
            var x = 1;
              indented();
        ```
        | A | B |
        |---|---|
        | a1 | [link](https://x) |
        ---
        ### Sub
        """;

    [Test]
    public void EveryBlockKindIsRead()
    {
        var blocks = DocText.Parse(Sample);
        Assert.Multiple(() =>
        {
            Assert.That(blocks.Select(b => b.Kind), Is.EqualTo(Kinds));
            Assert.That(DocText.Strip(blocks[1].Text), Is.EqualTo("Intro bold and code goes on."));
            Assert.That(blocks[1].Text, Does.Contain(DocText.CodeOpen + "code" + DocText.CodeClose));
            Assert.That(blocks[4].Level, Is.EqualTo(1));
            Assert.That(blocks[5].Text, Is.EqualTo("first continued"));
            Assert.That(blocks[6].Lines, Is.EqualTo(Code));
            Assert.That(blocks[7].Rows![1], Is.EqualTo(Row));
            Assert.That(DocText.Outline(blocks).Select(o => o.Text), Is.EqualTo(Headings));
        });
    }

    private static readonly string[] FlowRows = ["A -> B -> C", "v", "D"];

    [Test]
    public void AFlowBlockKeepsItsRows()
    {
        var blocks = DocText.Parse("```flow\nA -> B -> C\n\nv\n  D\n```\nafter");
        Assert.Multiple(() =>
        {
            Assert.That(blocks[0].Kind, Is.EqualTo(DocText.Kind.Flow));
            Assert.That(blocks[0].Lines, Is.EqualTo(FlowRows));
            Assert.That(blocks[1].Text, Is.EqualTo("after"));
        });
    }

    private static string Docs([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), "../../../Komet/assets/komet/config/komet-docs"));

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
                Assert.That(en.Count(b => b.Kind == DocText.Kind.Code), Is.EqualTo(de.Count(b => b.Kind == DocText.Kind.Code)), name);
                Assert.That(en.Count(b => b.Kind == DocText.Kind.Flow), Is.EqualTo(de.Count(b => b.Kind == DocText.Kind.Flow)), name);
            });
        }

        foreach (var language in new[] { "de", "en" }) // no page left over that nothing shows
            Assert.That(Directory.GetFiles(Path.Join(Docs(), language), "*.md").Select(Path.GetFileNameWithoutExtension),
                Is.EquivalentTo(KometDocs.Names), language);
    }

    // Modding's Templates page (rows) right after Modding's first page, the Help pages before it
    [Test]
    public void TheTemplatesFollowModdingsStart()
    {
        var docs = KometDocs.Names.Select(n => new OptionPage("docs-" + n, n, KometDocs.Help.Contains(n) ? "help" : "modding")).ToArray();
        var pages = KometDocs.With(docs, new OptionPage("templates", "Templates", "modding"));
        Assert.That(Array.FindIndex(pages, p => p.Id == "templates"), Is.EqualTo(KometDocs.Help.Length + 1));
    }

    private static List<DocText.Block> Read(string language, string name)
    {
        var file = Path.Join(Docs(), language, name + ".md");
        var text = File.ReadAllText(file);
        Assert.That(text.Split("```").Length % 2, Is.EqualTo(1), file + ": a code fence is not closed");
        Assert.That(text, Does.Not.Contain("—").And.Not.Contain(" – "), file + ": no dashes as punctuation");
        return DocText.Parse(text);
    }
}

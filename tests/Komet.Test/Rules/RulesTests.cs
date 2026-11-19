using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Komet.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Rules;

public sealed class RulesTests
{
    private const string Prefix = "using System; using System.Linq; using static Komet.Contracts;\nnamespace Komet;\n";

    private const string Contracts = """
                                     using System;
                                     namespace Komet;
                                     internal static partial class Contracts
                                     {
                                         public static bool Assert(bool condition) => condition;
                                         public static ReadOnlySpan<T> Bounded<T>(this T[] items, int max) => items.AsSpan(0, Math.Min(items.Length, max));
                                     }
                                     """;

    private static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers = [new KometAnalyzer()];

    // The shared framework only: the test output also holds Komet.dll with its own Komet.Contracts
    private static readonly MetadataReference[] References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
        .Select(path => MetadataReference.CreateFromFile(path))
    ];

    private static Diagnostic[] Analyze(string code, params (string Path, string Json)[] langs) =>
        Analyze(Prefix + code, "Snippet.cs", null, langs);

    // The snippet at `path` next to the Contracts stub; `projectDir` is what MSBuild passes as build_property.ProjectDir
    private static Diagnostic[] Analyze(string source, string path, string? projectDir,
        params (string Path, string Json)[] langs)
    {
        var compilation = CSharpCompilation.Create("Snippet",
            [
                CSharpSyntaxTree.ParseText(source, path: path),
                CSharpSyntaxTree.ParseText(Contracts, path: "Contracts.cs")
            ],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty,
            "the snippet must compile");
        var options = new AnalyzerOptions([.. langs.Select(lang => (AdditionalText)new Text(lang.Path, lang.Json))],
            new Options(projectDir));
        return [.. compilation.WithAnalyzers(Analyzers, options).GetAnalyzerDiagnosticsAsync().Result];
    }

    // As sorted "id message" lines
    private static string Report(string code, params (string Path, string Json)[] langs) =>
        string.Join("\n", Analyze(code, langs)
            .Select(d => d.Id + " " + d.GetMessage(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

    private static string Ids(Diagnostic[] diagnostics) =>
        string.Join(" ", diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal));

    private static string Ids(string code) => Ids(Analyze(code));

    private static string Lines(int statements) => string.Concat(Enumerable.Repeat("        n++;\n", statements));

    private static string Method(string body) =>
        $$"""
                 static class C
                 {
                     const int Max = 8;
                     static readonly int[] Table = [1, 2];
                     static int Count => Table.Length;
                     static int Settable { get; set; } = 4;
                     static Action? Reset;
                     static bool Keep(Action a) { Reset = a; return true; }
                     static void Set(ref int v) => v = 0;
                     static void Out(out int v) => v = 0;
                     static int M(int n, int[] items) { {{body}} return n; }
                 }
                 """;

    [Test]
    public void WhileDoGotoAndDirectivesAreForbidden()
    {
        var ids = Ids(Method("while (n > 0) n--; do n++; while (n < 0); goto end; end: n++;") +
                      "\n#region x\n#endregion\n#pragma warning disable CS0168\n#nullable enable\n");
        Assert.That(ids, Is.EqualTo("KR0001 KR0001 KR0002 KR0003 KR0003 KR0003 KR0003"));
    }

    [TestCase("for (var i = 0; i < Max; i++) n++;")]
    [TestCase("for (var i = 0; i <= Math.Min(n, 9); i += 2) n++;")]
    public void ForLoopsWithAConstantBoundPass(string loop) => Assert.That(Ids(Method(loop)), Is.Empty);

    [TestCase("for (var i = 0; i < n; i++) n++;", TestName = "bound is a parameter")]
    [TestCase("for (var i = 0; i < Math.Min(n, items.Length); i++) n++;", TestName = "Math.Min without a constant")]
    public void ForLoopsWithoutAConstantBoundFail(string loop) => Assert.That(Ids(Method(loop)), Is.EqualTo("KR0004"));

    [TestCase("foreach (var x in items.Bounded(Max)) n += x;", "")]
    [TestCase("foreach (var x in (ReadOnlySpan<int>)[1, 2]) n += x;", "")]
    public void ForEachNeedsBoundedOrALiteral(string loop, string expected) =>
        Assert.That(Ids(Method(loop)), Is.EqualTo(expected));

    [Test]
    public void RecursionDirectAndMutualIsForbidden()
    {
        var report = Report("""
                            static class R
                            {
                                static int Direct(int n) => n <= 0 ? 0 : Direct(n - 1);
                                static int Local(int n) { return Inner(n); int Inner(int k) => k <= 0 ? 0 : Inner(k - 1); }
                                static int A(int n) => n <= 0 ? 0 : B(n - 1);
                                static int B(int n) => C(n);
                                static int C(int n) => A(n);
                                static int Chain(int n) => Leaf(n) + Leaf(n);
                                static int Leaf(int n) => n;
                                static Func<int, int> Deferred() => n => Deferred()(n);
                            }
                            """);
        Assert.That(report, Is.EqualTo("""
                                       KR0006 'R.Direct' calls itself: recursion is forbidden
                                       KR0006 'R.Local.Inner' calls itself: recursion is forbidden
                                       KR0007 Mutual recursion is forbidden: R.A -> R.B -> R.C -> R.A
                                       """));
    }

    [TestCase(56, false)]
    [TestCase(57, true)]
    public void FunctionsSpanAtMostSixtyLines(int statements, bool fails)
    {
        var body = Lines(statements);
        var report =
            Report($"static class L\n{{\n    static int F(int n)\n    {{\n{body}        return n;\n    }}\n}}\n");
        Assert.That(report, Is.EqualTo(fails ? "KR0008 'F' spans 61 lines, more than 60" : ""));
    }

    [Test]
    public void LangKeysExistInEveryFileWithoutPlaceholdersInLabels()
    {
        const string code = """
                            sealed record Knob(string Key, string? Page, string? Group);
                            sealed class Panel { public Panel Value(string key) => this; public Panel Section(string key) => this; public Panel Warn(string key) => this; }
                            static class Uses
                            {
                                static readonly Knob[] Knobs = [new("Frob", "render", "cull"), new("Other", "render", "draw"), new("Hidden", null, null)];
                                static string T(string key) => key;
                                static void Draw(Panel p) { _ = p.Value("a").Value("b").Section("b").Warn("c").Warn("b"); _ = T("hud-a") + T("hud-missing") + T("hud-") + T("vk-a") + T("vk-gone") + T("komet-test-id"); }
                            }
                            """;
        const string known =
            """ "hud-a": "A", "hud-b": "B {0}", "settings-frob": "F", "settings-frob-hint": "H", "settings-page-render": "R", "settings-cull": "C", "vk-a": "V" """;
        var report = Report(code, ("assets/komet/lang/en.json", "{" + known + "}"),
            ("assets/komet/lang/de.json", "{" + known + """, "hud-extra": "E" }"""));
        Assert.That(report, Is.EqualTo("""
                                       KR0010 Lang key 'hud-c' is missing in de.json, en.json
                                       KR0010 Lang key 'hud-missing' is missing in de.json, en.json
                                       KR0010 Lang key 'vk-gone' is missing in de.json, en.json
                                       KR0011 Lang key 'hud-extra' is in de.json but missing in en.json
                                       KR0012 Lang key 'hud-b' is rendered by Value(), which passes no arguments, but its text in de.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Value(), which passes no arguments, but its text in en.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Warn(), which passes no arguments, but its text in de.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Warn(), which passes no arguments, but its text in en.json has a placeholder: B {0}
                                       KR0013 Knob 'Other' is shown on settings page 'render' but 'settings-draw' is missing in de.json, en.json
                                       KR0013 Knob 'Other' is shown on settings page 'render' but 'settings-other' is missing in de.json, en.json
                                       KR0013 Knob 'Other' is shown on settings page 'render' but 'settings-other-hint' is missing in de.json, en.json
                                       """));
    }

    // The analyzer reads the lang files tolerantly; this is the real parse, with the game's JSON library
    [Test]
    public void AssetsAndModInfoAreValidJson()
    {
        string[] files =
        [
            .. Directory.GetFiles(Path.Combine(Paths.KometDir, "assets"), "*.json", SearchOption.AllDirectories),
            Path.Combine(Paths.KometDir, "modinfo.json")
        ];
        var strict = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Length.GreaterThan(1));
            foreach (var file in files)
            {
                Assert.DoesNotThrow(() => JToken.Parse(File.ReadAllText(file), strict), file);
            }
        });
    }

    private sealed class Text(string path, string json) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(json);
    }

    private sealed class Options(string? projectDir) : AnalyzerConfigOptionsProvider
    {
        private static readonly Values None = new(null);

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(projectDir);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => None;
    }

    private sealed class Values(string? projectDir) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            value = KeyComparer.Equals(key, "build_property.ProjectDir") ? projectDir : null;
            return value is not null;
        }
    }
}

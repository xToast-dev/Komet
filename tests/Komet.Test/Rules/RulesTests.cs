using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Komet.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Rules;

// The build rules (Komet.Rules) on short snippets: every rule fires on a bad one and stays quiet on a good one
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

    private static Diagnostic[] Analyze(string code, params (string Path, string Json)[] langs)
    {
        return Analyze(Prefix + code, "Snippet.cs", null, langs);
    }

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

    // Every diagnostic but the density, which a snippet cannot reach
    private static IEnumerable<Diagnostic> Rules(Diagnostic[] diagnostics)
    {
        return diagnostics.Where(d => d.Id != "KR0009");
    }

    // As sorted "id message" lines
    private static string Report(string code, params (string Path, string Json)[] langs)
    {
        return string.Join("\n", Rules(Analyze(code, langs))
            .Select(d => d.Id + " " + d.GetMessage(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
    }

    private static string Ids(Diagnostic[] diagnostics)
    {
        return string.Join(" ", Rules(diagnostics).Select(d => d.Id).Order(StringComparer.Ordinal));
    }

    private static string Ids(string code)
    {
        return Ids(Analyze(code));
    }

    // "id line:column text" of each, in source order: where it points, not only what it says
    private static string Where(string code)
    {
        var source = Prefix + code;
        return string.Join("\n", Rules(Analyze(code)).OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d =>
            {
                var start = d.Location.GetLineSpan().StartLinePosition;
                return
                    $"{d.Id} {start.Line + 1}:{start.Character + 1} {source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)}";
            }));
    }

    // A function body of that many statements
    private static string Lines(int statements)
    {
        return string.Concat(Enumerable.Repeat("        n++;\n", statements));
    }

    private static string Method(string body)
    {
        return $$"""
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
    }

    [Test]
    public void WhileDoGotoAndDirectivesAreForbidden()
    {
        var ids = Ids(Method("while (n > 0) n--; do n++; while (n < 0); goto end; end: n++;") +
                      "\n#region x\n#endregion\n#pragma warning disable CS0168\n#nullable enable\n");
        Assert.That(ids, Is.EqualTo("KR0001 KR0001 KR0002 KR0003 KR0003 KR0003 KR0003"));
    }

    [Test]
    public void KeywordsInCommentsAndStringsAreNoStatements()
    {
        Assert.That(Ids(Method("var s = \"#region while (x) goto\"; n += s.Length; // while (n > 0) goto do\n")),
            Is.Empty);
    }

    // Roslyn would skip these as generated code; the rules skip only obj/, bin/ and paths outside the project directory
    [TestCase("/p/Komet/Sneaky.g.cs", "", "/p/Komet/", "KR0001 KR0003 KR0003")]
    [TestCase("/p/Komet/Form.Designer.cs", "", "/p/Komet/", "KR0001 KR0003 KR0003")]
    [TestCase("/p/Komet/Header.cs", "// <auto-generated/>\n", "/p/Komet/", "KR0001 KR0003 KR0003")]
    [TestCase("/p/Komet/obj/Debug/Komet.GlobalUsings.g.cs", "", "/p/Komet/", "")]
    [TestCase(
        "/p/Komet/obj/Debug/Microsoft.Interop.LibraryImportGenerator/Microsoft.Interop.LibraryImportGenerator/LibraryImports.g.cs",
        "",
        "/p/Komet/", "")]
    [TestCase("/p/Komet/bin/Release/Stray.cs", "", "/p/Komet/", "")]
    [TestCase("/elsewhere/Generated.cs", "", "/p/Komet/", "")]
    [TestCase("Sneaky.g.cs", "// <auto-generated/>\n", null, "KR0001 KR0003 KR0003")]
    [TestCase("obj/Debug/Komet.AssemblyInfo.cs", "", null, "")]
    public void GeneratedCodeHeuristicsDoNotExemptSources(string path, string header, string? projectDir,
        string expected)
    {
        var source = header + Prefix +
                     "static class G { static void F(int n) { while (n > 0) n--; } }\n#region r\n#endregion\n";
        Assert.That(Ids(Analyze(source, path, projectDir)), Is.EqualTo(expected));
    }

    [Test]
    public void DiagnosticsPointAtTheOffendingPart()
    {
        var body = Lines(57);
        var code = $$"""
                     static class P
                     {
                         static int F(int n, int[] items)
                         {
                             for (var i = 0; i < n; i++) n++;
                             for (var i = 0; i < 8; i++) { n++; i = 0; }
                             for (var i = 0; i < 8; i++) { n++; Set(ref i); }
                             foreach (var x in items) n += x;
                             return n;
                         }

                         static void Set(ref int v) => v = 0;

                         static int Long(int n)
                         {
                     {{body}}        return n;
                         }
                     }
                     """;
        Assert.That(Where(code), Is.EqualTo("""
                                            KR0004 7:25 i < n
                                            KR0004 8:44 i = 0
                                            KR0004 9:48 ref i
                                            KR0005 10:27 items
                                            KR0008 16:16 Long
                                            """));
    }

    [TestCase("for (var i = 0; i < Max; i++) n++;")]
    [TestCase("for (var i = 0; i <= Math.Min(n, 9); i += 2) n++;")]
    [TestCase("for (var i = 0; i < Table.Length && i < n; ++i) n++;")]
    [TestCase("for (var i = 0; i < Math.Min(items.Length, Table.Length); i++) items[i] = i;")]
    [TestCase("for (byte b = 0; b < 255; b++) n++;")]
    [TestCase("for (var i = 0; i < Count; i++) n++;")]
    [TestCase("for (var i = 0; i < Math.Min(items.Length, Count); i++) items[i] = i;")]
    [TestCase("for (var i = 0; i < Max; i += System.Numerics.Vector<int>.Count) n++;")]
    [TestCase("for (int i = 0, k = 2; i < Max; i++) n += k;")]
    [TestCase("for (var i = 0; i < Max; i++) { var j = i; j = 0; n += j; }")]
    public void ForLoopsWithAConstantBoundPass(string loop)
    {
        Assert.That(Ids(Method(loop)), Is.Empty);
    }

    [TestCase("for (var i = 0; i < n; i++) n++;", TestName = "bound is a parameter")]
    [TestCase("for (var i = 0; i < Math.Min(n, items.Length); i++) n++;", TestName = "Math.Min without a constant")]
    [TestCase("for (var i = 0; i < Max; i--) n++;", TestName = "counts down")]
    [TestCase("for (var i = 0; i < Max; i += n) n++;", TestName = "step is not a constant")]
    [TestCase("for (var i = 0; i < Max; i++, i++) n++;", TestName = "steps twice")]
    [TestCase("for (var i = 0; i < Max; i++) i = 0;", TestName = "body writes the variable")]
    [TestCase("var j = 0; for (; j < Max; j++) n++;", TestName = "variable declared outside")]
    [TestCase("for (byte b = 0; b <= 255; b++) n++;", TestName = "overflows before the bound")]
    [TestCase("for (var i = 0; i < int.MaxValue; i += System.Numerics.Vector<int>.Count) n++;",
        TestName = "a vector step overflows")]
    [TestCase("for (var i = 0; i < Settable; i++) n++;", TestName = "bound is a settable property")]
    [TestCase("var x = 0; for (ref var r = ref x; r < Max; r++) x = 0;", TestName = "ref loop variable")]
    [TestCase("for (int i = 0, k = Keep(() => i = 0) ? 1 : 0; i < Max; i++) { Reset!(); n += k; }",
        TestName = "another initializer writes it")]
    [TestCase("for (int i = Keep(() => i = 0) ? 0 : 1; i < Max; i++) Reset!();",
        TestName = "its own initializer writes it")]
    [TestCase("for (var i = 0; i < Max; i++) Set(ref i);", TestName = "body passes it by ref")]
    [TestCase("for (var i = 0; i < Max; i++) Out(out i);", TestName = "body passes it as out")]
    [TestCase("for (var i = 0; i < Max; i++) { ref var r = ref i; r = 0; }",
        TestName = "body writes it through a ref local")]
    [TestCase("for (var i = 0; i < Max; i++) { Zero(); void Zero() => i = 0; }",
        TestName = "a local function in the body writes it")]
    [TestCase("for (var i = 0; i < Max; i++) { Action a = () => i = 0; a(); }",
        TestName = "a lambda in the body writes it")]
    [TestCase("unsafe { for (var i = 0; i < Max; i++) { int* p = &i; *p = 0; } }", TestName = "its address is taken")]
    [TestCase("for (var i = 0; i < Max; i++) { var t = __makeref(i); __refvalue(t, int) = 0; }",
        TestName = "a typed reference writes it")]
    [TestCase("for (var i = 0; i < Max; i++) { (i, n) = (0, 1); }", TestName = "a deconstruction writes it")]
    [TestCase("for (var i = 0; i < Max && Keep(() => i = 0); i++) Reset!();", TestName = "the condition writes it")]
    public void ForLoopsWithoutAConstantBoundFail(string loop)
    {
        Assert.That(Ids(Method(loop)), Is.EqualTo("KR0004"));
    }

    [TestCase("foreach (var x in items.Bounded(Max)) n += x;", "")]
    [TestCase("foreach (var x in (ReadOnlySpan<int>)[1, 2]) n += x;", "")]
    [TestCase("foreach (var x in new[] { 1, 2 }) n += x;", "")]
    [TestCase("foreach (var x in items) n += x;", "KR0005")]
    [TestCase("foreach (var x in items.Bounded(Max).ToArray().Where(y => y > 0)) n += x;", "KR0005")]
    [TestCase("foreach (var (a, b) in new[] { (1, 2) }) n += a + b;", "")]
    [TestCase("foreach (var v in (ReadOnlySpan<int>)[.. items.Bounded(Max), 1]) n += v;", "")]
    [TestCase("foreach (var v in (System.Collections.Generic.IEnumerable<int>)[.. items]) n += v;", "KR0005")]
    [TestCase("foreach (var v in (ReadOnlySpan<int>)[.. items, 1]) n += v;", "KR0005")]
    [TestCase("var b = items.Bounded(Max); foreach (var v in b) n += v;", "KR0005")]
    public void ForEachNeedsBoundedOrALiteral(string loop, string expected)
    {
        Assert.That(Ids(Method(loop)), Is.EqualTo(expected));
    }

    // Contracts implements Bounded with the loops the rules forbid elsewhere, and its functions count on neither side of the density
    [Test]
    public void ContractsIsExemptFromLoopRulesAndDensity()
    {
        var diagnostics = Analyze("""
                                  static partial class Contracts
                                  {
                                      static int Loop(int n) { for (var i = 0; i < n; i++) n++; return n; }
                                      static int Each(System.Collections.Generic.List<int> items) { var n = 0; foreach (var x in items) n += x; return n; }
                                  }
                                  static class D { static int F(int n) { _ = Assert(n > 0); _ = Assert(n < 9); return n; } }
                                  """);
        Assert.That(diagnostics.Select(d => d.Id), Is.Empty);
    }

    [Test]
    public void LocalFunctionsCountForTheDensity()
    {
        var density = Analyze(
            "static class D { static int F(int n) { _ = Assert(n > 0); _ = Assert(n < 9); return G(n); static int G(int k) => k; } }");
        Assert.That(density.Select(d => d.GetMessage(CultureInfo.InvariantCulture)),
            Has.Exactly(1).StartsWith("Assertion density 2/2 = 1.00"));
    }

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

    // Accessors, events, operators and conversions are statically bound calls like any method
    [Test]
    public void RecursionThroughAccessorsOperatorsAndPartsIsForbidden()
    {
        var report = Report("""
                            sealed class V
                            {
                                private int _watched;
                                int Prop => Prop + 1;
                                int Loop => Back();
                                int Back() => Loop;
                                int this[int i] => this[i + 1];
                                event Action? E { add => E += value; remove { } }
                                int Setter { get => 0; set => Setter = value; }
                                public static V operator +(V a, V b) => a + b;
                                public static V operator -(V a, V b) { a -= b; return a; }
                                public static V operator !(V a) => !a;
                                public static V operator ++(V a) { var b = a; b++; return b; }
                                public static implicit operator int(V v) => v;
                                int Watched { get => _watched + nameof(Watched).Length; set { _watched = value; Changed(); } }
                                void Changed() => _ = Watched;
                                void Bump() => Watched++;
                                static V Make() => new() { Watched = 1 };
                            }
                            partial class Split { partial int P { get; } }
                            partial class Split { partial int P { get => P; } }
                            partial class Parts { partial void Go(int n); }
                            partial class Parts { partial void Go(int n) { if (n > 0) Go(n - 1); } }
                            """);
        Assert.That(report, Is.EqualTo("""
                                       KR0006 'Parts.Go' calls itself: recursion is forbidden
                                       KR0006 'Split.get_P' calls itself: recursion is forbidden
                                       KR0006 'V.add_E' calls itself: recursion is forbidden
                                       KR0006 'V.get_Item' calls itself: recursion is forbidden
                                       KR0006 'V.get_Prop' calls itself: recursion is forbidden
                                       KR0006 'V.op_Addition' calls itself: recursion is forbidden
                                       KR0006 'V.op_Implicit' calls itself: recursion is forbidden
                                       KR0006 'V.op_Increment' calls itself: recursion is forbidden
                                       KR0006 'V.op_LogicalNot' calls itself: recursion is forbidden
                                       KR0006 'V.op_Subtraction' calls itself: recursion is forbidden
                                       KR0006 'V.set_Setter' calls itself: recursion is forbidden
                                       KR0007 Mutual recursion is forbidden: V.get_Loop -> V.Back -> V.get_Loop
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

    [TestCase("static readonly Func<int, int> F = n =>")]
    [TestCase("static Func<int, int> P { get; } = (int n) =>")]
    [TestCase("static readonly Func<int, int> D = delegate (int n)")]
    public void LambdasInInitializersSpanAtMostSixtyLines(string header)
    {
        var body = Lines(57);
        var report = Report($"static class L\n{{\n    {header}\n    {{\n{body}        return n;\n    }};\n}}\n");
        Assert.That(report, Is.EqualTo("KR0008 'lambda' spans 61 lines, more than 60"));
    }

    [TestCase(1, "Assertion density 1/1 = 1.00 is below 2.0")]
    [TestCase(2, null)]
    public void AssertionDensityIsAtLeastTwo(int assertions, string? message)
    {
        var body = string.Concat(Enumerable.Repeat("_ = Assert(n > 0); ", assertions));
        var density = Analyze($"static class D {{ static int F(int n) {{ {body}return n; }} }}")
            .Where(d => d.Id == "KR0009");
        Assert.That(density.Select(d => d.GetMessage(CultureInfo.InvariantCulture)),
            message is null ? Is.Empty : Has.Exactly(1).StartsWith(message));
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
                                static void Draw(Panel p) { _ = p.Value("a").Value("b").Section("b").Warn("c").Warn("b"); _ = T("hud-a") + T("hud-missing") + T("hud-"); }
                            }
                            """;
        const string known =
            """ "hud-a": "A", "hud-b": "B {0}", "settings-frob": "F", "settings-page-render": "R", "settings-cull": "C" """;
        var report = Report(code, ("assets/komet/lang/en.json", "{" + known + "}"),
            ("assets/komet/lang/de.json", "{" + known + """, "hud-extra": "E" }"""));
        Assert.That(report, Is.EqualTo("""
                                       KR0010 Lang key 'hud-c' is missing in de.json, en.json
                                       KR0010 Lang key 'hud-missing' is missing in de.json, en.json
                                       KR0011 Lang key 'hud-extra' is in de.json but missing in en.json
                                       KR0012 Lang key 'hud-b' is rendered by Value(), which passes no arguments, but its text in de.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Value(), which passes no arguments, but its text in en.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Warn(), which passes no arguments, but its text in de.json has a placeholder: B {0}
                                       KR0012 Lang key 'hud-b' is rendered by Warn(), which passes no arguments, but its text in en.json has a placeholder: B {0}
                                       KR0013 Knob 'Other' is shown on settings page 'render' but 'settings-draw' is missing in de.json, en.json
                                       KR0013 Knob 'Other' is shown on settings page 'render' but 'settings-other' is missing in de.json, en.json
                                       """));
    }

    // The game reads lang files with Newtonsoft, which takes comments and single quotes; so does the analyzer
    [Test]
    public void LangFilesMayHoldCommentsAndSingleQuotes()
    {
        const string code =
            "static class Uses { static string T(string key) => key; static string All() => T(\"hud-a\") + T(\"hud-b\") + T(\"hud-c\"); }";
        const string en = """
                          { // HUD rows, "hud-x": "X"
                            "hud-a" /* between */ : /* key, and value */ "A",
                            "hud-n": 5 /* ", } { */, "hud-b": 'B, "quoted"', // "trailing"
                            /* "hud-y": "Y" */ 'hud-c': "C" }
                          """;
        var report = Report(code, ("assets/komet/lang/en.json", en),
            ("assets/komet/lang/de.json", """{ "hud-a": "A", "hud-b": "B", "hud-c": "C" }"""));
        Assert.That(report, Is.Empty);
    }

    // The analyzer reads the lang files tolerantly; this is the real parse, with the game's JSON library
    [Test]
    public void AssetsAndModInfoAreValidJson()
    {
        var files = Directory.GetFiles(Path.Combine(Paths.KometDir, "assets"), "*.json", SearchOption.AllDirectories)
            .Append(Path.Combine(Paths.KometDir, "modinfo.json"));
        var strict = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
        Assert.Multiple(() =>
        {
            Assert.That(files.Count(), Is.GreaterThan(1));
            foreach (var file in files) Assert.DoesNotThrow(() => JToken.Parse(File.ReadAllText(file), strict), file);
        });
    }

    private sealed class Text(string path, string json) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(json);
        }
    }

    private sealed class Options(string? projectDir) : AnalyzerConfigOptionsProvider
    {
        private static readonly Values None = new(null);

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(projectDir);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
        {
            return None;
        }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
        {
            return None;
        }
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

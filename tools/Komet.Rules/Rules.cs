using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Komet.Rules;

// Every rule is an error: dotnet build is the gate. Rules reported at compilation end carry the CompilationEnd tag.
internal static class Rules
{
    public const int MaxLines = 60;
    public const double MinDensity = 2.0;
    private const string Category = "Komet";
    private const string End = WellKnownDiagnosticTags.CompilationEnd;

    public static readonly DiagnosticDescriptor Unbounded = new("KR0001", "while and do loops are forbidden",
        "'{0}' loops are forbidden: use a for loop with a constant bound", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Goto = new("KR0002", "goto is forbidden", "goto is forbidden", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Directive = new("KR0003", "Preprocessor directives are forbidden",
        "Preprocessor directive '#{0}' is forbidden", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor For = new("KR0004", "for loop without a constant bound",
        "for loop without a constant bound: {0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ForEach = new("KR0005", "foreach over an unbounded collection",
        "foreach must iterate Contracts.Bounded(...), a collection expression (spreads only of Bounded(...)) or an array initializer, not '{0}'",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Recursion = new("KR0006", "Recursion is forbidden",
        "'{0}' calls itself: recursion is forbidden", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MutualRecursion = new("KR0007", "Mutual recursion is forbidden",
        "Mutual recursion is forbidden: {0}", Category, DiagnosticSeverity.Error, true, customTags: End);

    public static readonly DiagnosticDescriptor Length = new("KR0008", "Function too long",
        "'{0}' spans {1} lines, more than {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Density = new("KR0009", "Assertion density too low",
        "Assertion density {0}/{1} = {2} is below {3} (calls of Contracts.Assert/NotNull/Finite/Index/Bounded per method, constructor and local function)",
        Category, DiagnosticSeverity.Error, true, customTags: End);

    public static readonly DiagnosticDescriptor MissingKey = new("KR0010", "Lang key missing",
        "Lang key '{0}' is missing in {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor LangMismatch = new("KR0011", "Language files disagree",
        "Lang key '{0}' is in {1} but missing in {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Placeholder = new("KR0012", "Placeholder in a label",
        "Lang key '{0}' is rendered by {1}(), which passes no arguments, but its text in {2} has a placeholder: {3}",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor KnobKey = new("KR0013", "Settings knob without a lang key",
        "Knob '{0}' is shown on settings page '{1}' but '{2}' is missing in {3}",
        Category, DiagnosticSeverity.Error, true);
}

// Every rule in one analyzer, one registration per compilation over one Sources. The rule families stay classes of their own:
// Statements (KR0001-KR0005, KR0008), CallGraph (KR0006, KR0007), Density (KR0009) and Lang (KR0010-KR0013).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KometAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Rules.Unbounded, Rules.Goto, Rules.Directive, Rules.For, Rules.ForEach, Rules.Recursion, Rules.MutualRecursion,
        Rules.Length, Rules.Density, Rules.MissingKey, Rules.LangMismatch, Rules.Placeholder, Rules.KnobKey
    ];

    // Statements and Lang register their own actions; the call graph and the density are state per compilation, created here with
    // the end actions that report them
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var sources = Sources.Of(start.Compilation, start.Options);
            Statements.Register(start, sources);
            Lang.Register(start, sources);
            var (graph, density) = (new CallGraph(start.Compilation.Assembly), new Density());
            start.RegisterOperationAction(sources.Operation(graph.Add), CallGraph.Kinds);
            start.RegisterOperationAction(sources.Operation(density.Call), OperationKind.Invocation);
            start.RegisterSyntaxNodeAction(sources.Node(density.Function), SyntaxKind.MethodDeclaration,
                SyntaxKind.ConstructorDeclaration, SyntaxKind.LocalFunctionStatement);
            start.RegisterCompilationEndAction(graph.Report);
            start.RegisterCompilationEndAction(density.Report);
        });
    }
}

internal static class Symbols
{
    // Komet.Contracts holds the assertion helpers; the loop, foreach and density rules skip the class itself
    public static bool IsContracts(ITypeSymbol? type)
    {
        return type is
        {
            Name: "Contracts", ContainingType: null,
            ContainingNamespace: { Name: "Komet", ContainingNamespace.IsGlobalNamespace: true }
        };
    }

    public static bool InContracts(ISymbol? symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        while (type?.ContainingType is { } outer) type = outer;
        return IsContracts(type);
    }

    public static bool IsContractsCall(ISymbol? symbol, string name)
    {
        return symbol is IMethodSymbol method && method.Name == name && IsContracts(Normalize(method).ContainingType);
    }

    // One node per method in the call graph: extension calls, generic instances and partial parts map to the declared definition
    public static IMethodSymbol Normalize(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        return definition.PartialImplementationPart ?? definition;
    }

    public static ExpressionSyntax Unparenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
        return expression;
    }

    public static string Name(IMethodSymbol method)
    {
        var owner = method.ContainingSymbol is IMethodSymbol outer
            ? outer.ContainingType.Name + "." + outer.Name
            : method.ContainingType.Name;
        return method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? "new " + owner
            : owner + "." + method.Name;
    }
}

// The files the rules apply to: the project's own sources, whatever their name or header says. Roslyn's generated-code heuristic
// (*.g.cs, *.designer.cs, a `// <auto-generated/>` header) is not used, so the analyzer asks for generated code and filters here.
// Skipped are paths outside the project directory and under its obj/ or bin/: the SDK's GlobalUsings.g.cs and AssemblyInfo.cs and
// every source generator's output (csc roots generated trees at its output directory, obj/<cfg>/<generator>/...). Without a project
// directory (a compilation outside MSBuild), a path with an obj or bin segment is skipped.
internal sealed class Sources
{
    private readonly HashSet<SyntaxTree> _user;

    private Sources(HashSet<SyntaxTree> user)
    {
        _user = user;
    }

    public static Sources Of(Compilation compilation, AnalyzerOptions options)
    {
        _ = options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.projectdir", out var dir);
        var root = string.IsNullOrEmpty(dir) ? null : Normal(dir!).TrimEnd('/') + "/";
        return new Sources([.. compilation.SyntaxTrees.Where(tree => IsUser(Normal(tree.FilePath), root))]);
    }

    public Action<SyntaxTreeAnalysisContext> Tree(Action<SyntaxTreeAnalysisContext> action)
    {
        return c =>
        {
            if (_user.Contains(c.Tree)) action(c);
        };
    }

    public Action<SyntaxNodeAnalysisContext> Node(Action<SyntaxNodeAnalysisContext> action)
    {
        return c =>
        {
            if (_user.Contains(c.Node.SyntaxTree)) action(c);
        };
    }

    public Action<OperationAnalysisContext> Operation(Action<OperationAnalysisContext> action)
    {
        return c =>
        {
            if (_user.Contains(c.Operation.Syntax.SyntaxTree)) action(c);
        };
    }

    private static bool IsUser(string path, string? root)
    {
        if (root is null) return Array.TrueForAll(path.Split('/'), segment => segment is not ("obj" or "bin"));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith(root + "obj/", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith(root + "bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normal(string path)
    {
        return path.Replace('\\', '/');
    }
}

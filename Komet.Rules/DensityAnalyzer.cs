using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Komet.Rules;

// Assertion density over the whole compilation: calls of the Contracts helpers per method, constructor and local function with a body.
// Global, not per function, like the regex rule it replaces; the Contracts class itself counts on neither side.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DensityAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.Density];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var (counts, sources) = (new Counts(), Sources.Of(start.Compilation, start.Options));
            start.RegisterOperationAction(sources.Operation(counts.Call), OperationKind.Invocation);
            start.RegisterSyntaxNodeAction(sources.Node(counts.Function), SyntaxKind.MethodDeclaration,
                SyntaxKind.ConstructorDeclaration,
                SyntaxKind.LocalFunctionStatement);
            start.RegisterCompilationEndAction(counts.Report);
        });
    }

    private sealed class Counts
    {
        private int _assertions, _functions;

        public void Call(OperationAnalysisContext context)
        {
            var method = ((IInvocationOperation)context.Operation).TargetMethod;
            if (Array.IndexOf(Symbols.Assertions, method.Name) >= 0 &&
                Symbols.IsContracts(Symbols.Normalize(method).ContainingType) &&
                !Symbols.InContracts(context.ContainingSymbol))
                _ = Interlocked.Increment(ref _assertions);
        }

        public void Function(SyntaxNodeAnalysisContext context)
        {
            var body = context.Node switch
            {
                BaseMethodDeclarationSyntax member => (SyntaxNode?)member.Body ?? member.ExpressionBody,
                LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
                _ => null
            };
            if (body is not null && !Symbols.InContracts(context.ContainingSymbol))
                _ = Interlocked.Increment(ref _functions);
        }

        public void Report(CompilationAnalysisContext context)
        {
            if (_functions == 0) return;
            var density = (double)_assertions / _functions;
            if (density >= Rules.MinDensity) return; // csc prints no Info diagnostics, so success stays silent
            var contracts = context.Compilation.GetTypeByMetadataName("Komet.Contracts");
            var where = contracts?.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(Rules.Density, where, _assertions, _functions,
                density.ToString("F2", CultureInfo.InvariantCulture),
                Rules.MinDensity.ToString("F1", CultureInfo.InvariantCulture)));
        }
    }
}

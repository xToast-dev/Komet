using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Komet.Rules;

// Per-statement rules: no while/do/goto, no preprocessor directives, bounded for and foreach, functions (lambdas included) of at most
// MaxLines lines
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StatementAnalyzer : DiagnosticAnalyzer
{
    private static readonly SyntaxKind[] Functions =
    [
        SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration, SyntaxKind.DestructorDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration, SyntaxKind.LocalFunctionStatement, SyntaxKind.GetAccessorDeclaration,
        SyntaxKind.SetAccessorDeclaration, SyntaxKind.InitAccessorDeclaration, SyntaxKind.AddAccessorDeclaration,
        SyntaxKind.RemoveAccessorDeclaration, SyntaxKind.PropertyDeclaration, SyntaxKind.IndexerDeclaration,
        SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
        SyntaxKind.AnonymousMethodExpression
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Rules.Unbounded, Rules.Goto, Rules.Directive, Rules.For, Rules.ForEach, Rules.Length];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var sources = Sources.Of(start.Compilation, start.Options);
            start.RegisterSyntaxTreeAction(sources.Tree(Directives));
            start.RegisterSyntaxNodeAction(
                sources.Node(c => Report(c, Rules.Unbounded, ((WhileStatementSyntax)c.Node).WhileKeyword, "while")),
                SyntaxKind.WhileStatement);
            start.RegisterSyntaxNodeAction(
                sources.Node(c => Report(c, Rules.Unbounded, ((DoStatementSyntax)c.Node).DoKeyword, "do-while")),
                SyntaxKind.DoStatement);
            start.RegisterSyntaxNodeAction(
                sources.Node(c => Report(c, Rules.Goto, ((GotoStatementSyntax)c.Node).GotoKeyword)),
                SyntaxKind.GotoStatement, SyntaxKind.GotoCaseStatement, SyntaxKind.GotoDefaultStatement);
            start.RegisterSyntaxNodeAction(sources.Node(For), SyntaxKind.ForStatement);
            start.RegisterSyntaxNodeAction(sources.Node(ForEach), SyntaxKind.ForEachStatement,
                SyntaxKind.ForEachVariableStatement);
            start.RegisterSyntaxNodeAction(sources.Node(Length), Functions);
        });
    }

    private static void Report(SyntaxNodeAnalysisContext context, DiagnosticDescriptor rule, SyntaxToken at,
        params object[] args)
    {
        context.ReportDiagnostic(Diagnostic.Create(rule, at.GetLocation(), args));
    }

    private static void Directives(SyntaxTreeAnalysisContext context)
    {
        var directive = context.Tree.GetRoot(context.CancellationToken).GetFirstDirective();
        while (directive is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.Directive, directive.GetLocation(),
                directive.DirectiveNameToken.ValueText));
            directive = directive.GetNextDirective();
        }
    }

    private static void For(SyntaxNodeAnalysisContext context)
    {
        if (Symbols.InContracts(context.ContainingSymbol)) return;
        var problem = ForLoop.Check((ForStatementSyntax)context.Node, context.SemanticModel, context.CancellationToken);
        if (problem is var (reason, where)) context.ReportDiagnostic(Diagnostic.Create(Rules.For, where, reason));
    }

    private static void ForEach(SyntaxNodeAnalysisContext context)
    {
        if (Symbols.InContracts(context.ContainingSymbol)) return;
        var collection = ((CommonForEachStatementSyntax)context.Node).Expression;
        var inner = Symbols.Unparenthesized(collection);
        while (inner is CastExpressionSyntax cast) inner = Symbols.Unparenthesized(cast.Expression);
        var bounded = inner switch
        {
            CollectionExpressionSyntax literal => literal.Elements.All(e =>
                e is not SpreadElementSyntax spread || IsBounded(context, spread.Expression)),
            ImplicitArrayCreationExpressionSyntax => true,
            ArrayCreationExpressionSyntax array => array.Initializer is not null,
            _ => IsBounded(context, inner)
        };
        if (!bounded)
            context.ReportDiagnostic(Diagnostic.Create(Rules.ForEach, collection.GetLocation(), collection.ToString()));
    }

    // A call of Contracts.Bounded: the collection itself, or a spread `[.. items.Bounded(n)]` in a collection expression
    private static bool IsBounded(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        return Symbols.Unparenthesized(expression) is InvocationExpressionSyntax call &&
               Symbols.IsContractsCall(context.SemanticModel.GetSymbolInfo(call, context.CancellationToken).Symbol,
                   "Bounded");
    }

    private static void Length(SyntaxNodeAnalysisContext context)
    {
        var (name, at) = context.Node switch
        {
            MethodDeclarationSyntax m => (m.Identifier.ValueText, m.Identifier),
            ConstructorDeclarationSyntax c => (c.Identifier.ValueText, c.Identifier),
            DestructorDeclarationSyntax d => ("~" + d.Identifier.ValueText, d.Identifier),
            OperatorDeclarationSyntax o => ("operator " + o.OperatorToken.ValueText, o.OperatorToken),
            ConversionOperatorDeclarationSyntax c => ("operator " + c.Type, c.OperatorKeyword),
            LocalFunctionStatementSyntax l => (l.Identifier.ValueText, l.Identifier),
            AccessorDeclarationSyntax a => (Owner(a) + "." + a.Keyword.ValueText, a.Keyword),
            PropertyDeclarationSyntax { ExpressionBody: not null } p => (p.Identifier.ValueText, p.Identifier),
            IndexerDeclarationSyntax { ExpressionBody: not null } i => ("this[]", i.ThisKeyword),
            AnonymousFunctionExpressionSyntax f => ("lambda", f.GetFirstToken()),
            _ => ("", default)
        };
        if (name.Length == 0) return;
        var span = context.Node.GetLocation().GetLineSpan();
        var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
        if (lines > Rules.MaxLines)
            Report(context, Rules.Length, at, name, lines.ToString(CultureInfo.InvariantCulture),
                Rules.MaxLines.ToString(CultureInfo.InvariantCulture));
    }

    private static string Owner(AccessorDeclarationSyntax accessor)
    {
        return accessor.Parent?.Parent switch
        {
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            EventDeclarationSyntax e => e.Identifier.ValueText,
            _ => "this[]"
        };
    }
}

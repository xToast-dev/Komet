using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Komet.Rules;

internal static class Symbols
{
    public static readonly string[] Assertions = ["Assert", "NotNull", "Finite", "Index", "Bounded"];

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

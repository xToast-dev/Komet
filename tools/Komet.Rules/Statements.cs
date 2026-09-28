using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Komet.Rules;

// Per-statement rules: no while/do/goto, no preprocessor directives, bounded for and foreach, functions (lambdas included) of at most
// MaxLines lines
internal static class Statements
{
    private static readonly SyntaxKind[] Functions =
    [
        SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration, SyntaxKind.DestructorDeclaration,
        SyntaxKind.OperatorDeclaration, SyntaxKind.ConversionOperatorDeclaration, SyntaxKind.LocalFunctionStatement,
        SyntaxKind.GetAccessorDeclaration, SyntaxKind.SetAccessorDeclaration, SyntaxKind.InitAccessorDeclaration,
        SyntaxKind.AddAccessorDeclaration, SyntaxKind.RemoveAccessorDeclaration, SyntaxKind.PropertyDeclaration,
        SyntaxKind.IndexerDeclaration, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
        SyntaxKind.AnonymousMethodExpression
    ];

    public static void Register(CompilationStartAnalysisContext start, Sources sources)
    {
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

// A for loop is bounded when its integer variable, declared in the initializer and not a ref local, is compared with less-than or
// less-or-equal against a compile-time constant, the Length of a static readonly array field, a static get-only property whose body is
// one of those, or Math.Min of anything and one of those (in any conjunct of a top-level logical and), advances by an increment, a
// positive constant step or Vector<T>.Count exactly once per iteration, is written nowhere else (no other initializer, the condition or
// the body, lambdas and local functions included) and has no address taken, and cannot overflow before it passes the bound. Out of
// scope: writes through Unsafe.AsRef of a readonly reference.
internal static class ForLoop
{
    private const decimal MaxArrayLength = 0x7FFFFFC7; // Array.MaxLength
    private const decimal MaxVectorCount = 256; // Vector<byte>.Count of the widest register, a 2048-bit SVE vector

    public static (string Reason, Location Where)? Check(ForStatementSyntax loop, SemanticModel model,
        CancellationToken token)
    {
        if (loop.Declaration is not { } declaration || loop.Condition is not { } condition)
            return ("declare the loop variable in the initializer and compare it with a bound (`i < B` or `i <= B`)",
                loop.ForKeyword.GetLocation());
        var (variable, last) = Bound(declaration, condition, model, token);
        if (variable is null)
            return (
                "the condition must compare the variable declared in the initializer with < or <= against a constant, the Length of " +
                "a static readonly array, a static property returning one of those, or Math.Min of anything and one of those",
                condition.GetLocation());

        if (MaxValue(variable.Type) is not { } max)
            return ($"the loop variable '{variable.Name}' must be an integer", declaration.Type.GetLocation());
        if (variable.RefKind != RefKind.None)
            return ($"the loop variable '{variable.Name}' must not be a ref local", declaration.Type.GetLocation());
        var (step, problem) = Step(loop, variable, model, token);
        if (problem is not null) return problem;
        if (last + step > max)
            return ($"'{variable.Name}' overflows {variable.Type} before it passes the bound", condition.GetLocation());
        foreach (var declarator in declaration.Variables)
            if (declarator.Initializer is { } initializer && Writes(model.AnalyzeDataFlow(initializer.Value), variable))
                return ($"an initializer writes '{variable.Name}'", initializer.Value.GetLocation());

        if (Writes(model.AnalyzeDataFlow(condition), variable))
            return ($"the condition writes '{variable.Name}'", condition.GetLocation());
        if (Escape(loop, variable, model, token) is { } escape)
            return ($"the address of '{variable.Name}' is taken", escape);
        var body = model.AnalyzeDataFlow(loop.Statement);
        if (body is not { Succeeded: true })
            return ("data flow analysis of the body failed", loop.Statement.GetLocation());
        return Writes(body, variable)
            ? ($"the body writes the loop variable '{variable.Name}'",
                WriteSite(loop.Statement, variable, model, token))
            : null;
    }

    // The loop variable and the largest value it takes inside the body
    private static (ILocalSymbol? Variable, decimal Last) Bound(VariableDeclarationSyntax declaration,
        ExpressionSyntax condition, SemanticModel model, CancellationToken token)
    {
        var pending = new Stack<ExpressionSyntax>();
        pending.Push(condition);
        while (pending.Count > 0)
        {
            var conjunct = Symbols.Unparenthesized(pending.Pop());
            if (conjunct is not BinaryExpressionSyntax binary) continue;
            if (binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                pending.Push(binary.Right);
                pending.Push(binary.Left);
                continue;
            }

            var inclusive = binary.IsKind(SyntaxKind.LessThanOrEqualExpression);
            if (!inclusive && !binary.IsKind(SyntaxKind.LessThanExpression)) continue;
            if (model.GetSymbolInfo(Symbols.Unparenthesized(binary.Left), token).Symbol is not ILocalSymbol local)
                continue;
            if (!declaration.Variables.Any(v =>
                    SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(v, token), local))) continue;
            if (Limit(binary.Right, model, token) is { } limit) return (local, inclusive ? limit : limit - 1);
        }

        return (null, 0);
    }

    // The largest value the bound can take: a constant, Math.Min with a constant or static array length on either side, or such a length
    private static decimal? Limit(ExpressionSyntax bound, SemanticModel model, CancellationToken token)
    {
        if (Fixed(bound, model, token) is { } limit) return limit;
        if (bound is not InvocationExpressionSyntax { ArgumentList.Arguments: { Count: 2 } arguments } ||
            model.GetSymbolInfo(bound, token).Symbol is not IMethodSymbol
            {
                Name: "Min",
                ContainingType:
                { Name: "Math", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } }
            }) return null;
        var (x, y) = (Fixed(arguments[0].Expression, model, token), Fixed(arguments[1].Expression, model, token));
        if (x is null || y is null) return x ?? y;
        return Math.Min(x.Value, y.Value);
    }

    private static decimal? Fixed(ExpressionSyntax bound, SemanticModel model, CancellationToken token)
    {
        if (Constant(bound, model, token) is { } constant) return constant;
        var symbol = model.GetSymbolInfo(bound, token).Symbol;
        if (symbol is IPropertySymbol { Name: "Length", ContainingType.SpecialType: SpecialType.System_Array })
            return bound is MemberAccessExpressionSyntax access &&
                   model.GetSymbolInfo(access.Expression, token).Symbol is IFieldSymbol field &&
                   IsStaticArray(field)
                ? MaxArrayLength
                : null;

        return symbol is IPropertySymbol property ? Getter(property, token) : null;
    }

    // One level through a static get-only property, `static int Count => Keys.Length;`, whose expression body is a literal, a constant
    // or the Length of a static readonly array field of its own type. Its tree may not be the loop's, so its names are looked up in
    // the type instead of bound by a semantic model.
    private static decimal? Getter(IPropertySymbol property, CancellationToken token)
    {
        if (property is not
            {
                IsStatic: true, IsVirtual: false, IsAbstract: false, IsOverride: false, IsExtern: false, SetMethod: null
            } ||
            property.ContainingType.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            property.DeclaringSyntaxReferences.Length != 1) return null;
        var body = property.DeclaringSyntaxReferences[0].GetSyntax(token) switch
        {
            PropertyDeclarationSyntax { ExpressionBody.Expression: var expression } => expression,
            PropertyDeclarationSyntax { AccessorList.Accessors: { Count: 1 } accessors } when accessors[0]
                    .IsKind(SyntaxKind.GetAccessorDeclaration) =>
                accessors[0].ExpressionBody?.Expression,
            _ => null
        };
        if (body is null) return null;
        var type = property.ContainingType;
        return Symbols.Unparenthesized(body) switch
        {
            LiteralExpressionSyntax literal => Integer(literal.Token.Value),
            IdentifierNameSyntax name when Member(type, name) is IFieldSymbol { HasConstantValue: true } constant =>
                Integer(constant.ConstantValue),
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax array, Name.Identifier.ValueText: "Length" }
                when Member(type, array) is IFieldSymbol field && IsStaticArray(field) => MaxArrayLength,
            _ => null
        };
    }

    private static ISymbol? Member(INamedTypeSymbol type, IdentifierNameSyntax name)
    {
        return type.GetMembers(name.Identifier.ValueText).FirstOrDefault();
    }

    private static bool IsStaticArray(IFieldSymbol field)
    {
        return field is { IsStatic: true, IsReadOnly: true, Type: IArrayTypeSymbol };
    }

    private static decimal? Constant(ExpressionSyntax expression, SemanticModel model, CancellationToken token)
    {
        var value = model.GetConstantValue(expression, token);
        return value.HasValue ? Integer(value.Value) : null;
    }

    private static decimal? Integer(object? value)
    {
        return value switch
        {
            int i => i,
            long l => l,
            uint u => u,
            ulong ul => ul,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            char c => c,
            _ => null
        };
    }

    private static decimal? MaxValue(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_SByte => sbyte.MaxValue,
            SpecialType.System_Byte => byte.MaxValue,
            SpecialType.System_Int16 => short.MaxValue,
            SpecialType.System_UInt16 or SpecialType.System_Char => ushort.MaxValue,
            SpecialType.System_Int32 => int.MaxValue,
            SpecialType.System_UInt32 => uint.MaxValue,
            SpecialType.System_Int64 => long.MaxValue,
            SpecialType.System_UInt64 => ulong.MaxValue,
            _ => null
        };
    }

    private static (decimal Step, (string, Location)? Problem) Step(ForStatementSyntax loop, ILocalSymbol variable,
        SemanticModel model, CancellationToken token)
    {
        var rule =
            $"the iterator must be {variable.Name}++, ++{variable.Name} or {variable.Name} += a positive constant or Vector<T>.Count";
        decimal step = 0;
        foreach (var incrementor in loop.Incrementors)
        {
            if (Increment(incrementor, variable, model, token) is not { } by)
            {
                if (Writes(model.AnalyzeDataFlow(incrementor), variable)) return (0, (rule, incrementor.GetLocation()));
                continue;
            }

            if (step > 0)
                return (0, ($"'{variable.Name}' may change only once per iteration", incrementor.GetLocation()));
            step = by;
        }

        if (step > 0) return (step, null);
        return (0,
            (rule,
                loop.Incrementors.Count > 0 ? loop.Incrementors[0].GetLocation() : loop.CloseParenToken.GetLocation()));
    }

    private static decimal? Increment(ExpressionSyntax incrementor, ILocalSymbol variable, SemanticModel model,
        CancellationToken token)
    {
        var (target, step) = incrementor switch
        {
            PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.PostIncrementExpression) =>
                (post.Operand, 1m),
            PrefixUnaryExpressionSyntax pre when pre.IsKind(SyntaxKind.PreIncrementExpression) => (pre.Operand, 1m),
            AssignmentExpressionSyntax add when add.IsKind(SyntaxKind.AddAssignmentExpression) =>
                (add.Left, Constant(add.Right, model, token) ?? VectorCount(add.Right, model, token)),
            _ => (null, 0m)
        };
        return target is not null && step > 0 && Is(target, variable, model, token) ? step : null;
    }

    // Vector<T>.Count is at least 1 and a constant to the JIT; its largest value is the step that decides overflow
    private static decimal VectorCount(ExpressionSyntax step, SemanticModel model, CancellationToken token)
    {
        return model.GetSymbolInfo(step, token).Symbol is IPropertySymbol
        {
            Name: "Count", IsStatic: true,
            ContainingType:
            {
                Name: "Vector", Arity: 1,
                ContainingNamespace:
                {
                    Name: "Numerics",
                    ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }
                }
            }
        }
            ? MaxVectorCount
            : 0m;
    }

    // The references data flow does not follow: `&i` and `__makeref(i)`
    private static Location? Escape(ForStatementSyntax loop, ILocalSymbol variable, SemanticModel model,
        CancellationToken token)
    {
        foreach (var node in loop.DescendantNodes())
        {
            var operand = node switch
            {
                PrefixUnaryExpressionSyntax address when address.IsKind(SyntaxKind.AddressOfExpression) => address
                    .Operand,
                MakeRefExpressionSyntax reference => reference.Expression,
                _ => null
            };
            if (operand is not null && Is(operand, variable, model, token)) return node.GetLocation();
        }

        return null;
    }

    private static bool Writes(DataFlowAnalysis? flow, ILocalSymbol variable)
    {
        return flow is null || !flow.Succeeded || flow.WrittenInside.Contains(variable, SymbolEqualityComparer.Default);
    }

    private static Location WriteSite(StatementSyntax body, ILocalSymbol variable, SemanticModel model,
        CancellationToken token)
    {
        foreach (var node in body.DescendantNodesAndSelf())
        {
            var target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax pre when pre.IsKind(SyntaxKind.PreIncrementExpression) ||
                                                     pre.IsKind(SyntaxKind.PreDecrementExpression) => pre.Operand,
                PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.PostIncrementExpression) ||
                                                       post.IsKind(SyntaxKind.PostDecrementExpression) => post.Operand,
                ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                                             argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) =>
                    argument.Expression,
                RefExpressionSyntax reference => reference.Expression,
                _ => null
            };
            if (target is not null && Is(target, variable, model, token)) return node.GetLocation();
        }

        return body.GetLocation();
    }

    private static bool Is(ExpressionSyntax expression, ILocalSymbol variable, SemanticModel model,
        CancellationToken token)
    {
        return Symbols.Unparenthesized(expression) is IdentifierNameSyntax name &&
               name.Identifier.ValueText == variable.Name &&
               SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, token).Symbol, variable);
    }
}

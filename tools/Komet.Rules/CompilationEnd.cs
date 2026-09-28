using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Komet.Rules;

// Direct recursion at the call site, mutual recursion as a cycle in the call graph of the methods, constructors, accessors, operators
// and local functions declared in this compilation. An edge is a statically bound call: an invocation, an object creation or
// constructor initializer, the get or set accessor of a property or indexer, the add or remove accessor of an event, and a
// user-defined operator or conversion. Out of scope: calls through delegates, lambdas and method groups, virtual dispatch to
// overrides, calls from field and property initializers, and nameof.
internal sealed class CallGraph(IAssemblySymbol assembly)
{
    internal static readonly OperationKind[] Kinds =
    [
        OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference,
        OperationKind.EventAssignment, OperationKind.Binary, OperationKind.Unary, OperationKind.Increment,
        OperationKind.Decrement, OperationKind.CompoundAssignment, OperationKind.Conversion
    ];

    private readonly List<(IMethodSymbol Caller, IMethodSymbol Callee, Location Where)> _edges = [];

    public void Add(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        var targets = operation switch
        {
            IInvocationOperation call => (call.TargetMethod, null),
            IObjectCreationOperation creation => (creation.Constructor, null),
            IPropertyReferenceOperation property => Accessors(property),
            IEventAssignmentOperation { EventReference: IEventReferenceOperation reference } assignment =>
                (assignment.Adds ? reference.Event.AddMethod : reference.Event.RemoveMethod, null),
            IBinaryOperation binary => (binary.OperatorMethod, null),
            IUnaryOperation unary => (unary.OperatorMethod, null),
            IIncrementOrDecrementOperation step => (step.OperatorMethod, null),
            ICompoundAssignmentOperation compound => (compound.OperatorMethod, null),
            IConversionOperation conversion => (conversion.OperatorMethod, null),
            _ => (null, null)
        };
        if (targets is (null, null)) return;
        if (Caller(operation, context.ContainingSymbol) is not { } caller) return;
        Edge(context, caller, targets.First);
        Edge(context, caller, targets.Second);
    }

    private void Edge(OperationAnalysisContext context, IMethodSymbol caller, IMethodSymbol? target)
    {
        if (target is null || target.MethodKind == MethodKind.DelegateInvoke) return;
        var callee = Symbols.Normalize(target);
        if (!SymbolEqualityComparer.Default.Equals(callee.ContainingAssembly, assembly)) return;
        var where = context.Operation.Syntax.GetLocation();
        if (SymbolEqualityComparer.Default.Equals(caller, callee))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.Recursion, where, Symbols.Name(callee)));
            return;
        }

        lock (_edges)
        {
            _edges.Add((caller, callee, where));
        }
    }

    // The accessors a property or indexer reference calls: the setter when it is assigned (also in an object initializer, a with
    // expression or a deconstruction), both for increments, decrements and compound assignments, else the getter. A ref-returning
    // property has only the getter.
    private static (IMethodSymbol? First, IMethodSymbol? Second) Accessors(IPropertyReferenceOperation reference)
    {
        var (get, set) = (reference.Property.GetMethod, reference.Property.SetMethod ?? reference.Property.GetMethod);
        IOperation target = reference;
        while (target.Parent is ITupleOperation tuple) target = tuple;
        return target.Parent switch
        {
            ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, target) => (set, null),
            IDeconstructionAssignmentOperation deconstruction when ReferenceEquals(deconstruction.Target, target) => (set, null),
            ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, target) => (get, set),
            ICoalesceAssignmentOperation coalesce when ReferenceEquals(coalesce.Target, target) => (get, set),
            IIncrementOrDecrementOperation step when ReferenceEquals(step.Target, target) => (get, set),
            _ => (get, null)
        };
    }

    // The innermost local function around the call, else the member; a call inside a lambda belongs to no one, nameof calls nothing
    private static IMethodSymbol? Caller(IOperation operation, ISymbol owner)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ILocalFunctionOperation local) return Symbols.Normalize(local.Symbol);
            if (parent is IAnonymousFunctionOperation or INameOfOperation) return null;
        }

        return owner is IMethodSymbol method ? Symbols.Normalize(method) : null;
    }

    // Iterative depth-first search; an edge to a method still on the stack closes a cycle
    public void Report(CompilationAnalysisContext context)
    {
        var calls = Calls();
        // false: on the stack, true: done
        var state = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var stack = new List<(IMethodSymbol Method, int Next)>();
        foreach (var root in calls.Keys)
        {
            if (state.ContainsKey(root)) continue;
            state[root] = false;
            stack.Add((root, 0));
            while (stack.Count > 0)
            {
                var (method, next) = stack[stack.Count - 1];
                if (!calls.TryGetValue(method, out var callees) || next == callees.Count)
                {
                    state[method] = true;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                stack[stack.Count - 1] = (method, next + 1);
                var (callee, where) = callees[next];
                if (state.TryGetValue(callee, out var done))
                {
                    if (!done)
                        context.ReportDiagnostic(Diagnostic.Create(Rules.MutualRecursion, where,
                            Cycle(stack, callee)));
                    continue;
                }

                state[callee] = false;
                stack.Add((callee, 0));
            }
        }
    }

    // Callers and their callees in source order, one edge per pair at its first call site, so the report is deterministic
    private Dictionary<IMethodSymbol, List<(IMethodSymbol Callee, Location Where)>> Calls()
    {
        (IMethodSymbol Caller, IMethodSymbol Callee, Location Where)[] edges;
        lock (_edges)
        {
            edges = [.. _edges];
        }

        Array.Sort(edges, (a, b) => Compare(a.Where, b.Where));
        var calls = new Dictionary<IMethodSymbol, List<(IMethodSymbol, Location)>>(SymbolEqualityComparer.Default);
        foreach (var (caller, callee, where) in edges)
        {
            if (!calls.TryGetValue(caller, out var callees)) calls[caller] = callees = [];
            if (!callees.Exists(c => SymbolEqualityComparer.Default.Equals(c.Item1, callee)))
                callees.Add((callee, where));
        }

        return calls;
    }

    private static int Compare(Location a, Location b)
    {
        var file = string.CompareOrdinal(a.SourceTree?.FilePath, b.SourceTree?.FilePath);
        return file != 0 ? file : a.SourceSpan.Start.CompareTo(b.SourceSpan.Start);
    }

    private static string Cycle(List<(IMethodSymbol Method, int Next)> stack, IMethodSymbol back)
    {
        var from = stack.FindIndex(s => SymbolEqualityComparer.Default.Equals(s.Method, back));
        return string.Join(" -> ", stack.Skip(from).Select(s => Symbols.Name(s.Method)).Append(Symbols.Name(back)));
    }
}

// Assertion density over the whole compilation: calls of the Contracts helpers per method, constructor and local function with a body.
// Global, not per function; the Contracts class itself counts on neither side.
internal sealed class Density
{
    private static readonly string[] Assertions = ["Assert", "NotNull", "Finite", "Index", "Bounded"];

    private int _assertions, _functions;

    public void Call(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (Array.IndexOf(Assertions, method.Name) >= 0 &&
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

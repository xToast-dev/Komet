using Microsoft.CodeAnalysis;

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
        Category,
        DiagnosticSeverity.Error, true);

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
        Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor KnobKey = new("KR0013", "Settings knob without a lang key",
        "Knob '{0}' is shown on settings page '{1}' but '{2}' is missing in {3}", Category, DiagnosticSeverity.Error,
        true);
}

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Komet.Rules;

// Four ways a translation breaks. A key the code asks for but a language file lacks renders as the bare key; a key only some files
// know leaves the others bare; a placeholder in a key that Value(), Bar(), Title() or Warn() renders stays literal, because those pass
// no arguments; a knob on a settings page needs "settings-" + its lower-case key, "settings-page-" + its page and "settings-" + its
// group. Keys built at runtime ("hud-pass-" + name) carry no literal and stay unchecked.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LangAnalyzer : DiagnosticAnalyzer
{
    // A lang key spelled out in full: one of the prefixes the mod uses, and no trailing dash, which would be a concatenation
    private static readonly Regex KeyLiteral = new(
        "^(?:hud|settings|verify|hotkey|cmd|time|update)-[a-z0-9-]*[a-z0-9]$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex
        PanelKey = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string[] Renderers = ["Value", "Bar", "Section", "Title", "Warn"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Rules.MissingKey, Rules.LangMismatch, Rules.Placeholder, Rules.KnobKey];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var (langs, sources) = (LangFile.ReadAll(start.Options.AdditionalFiles, start.CancellationToken),
                Sources.Of(start.Compilation, start.Options));
            start.RegisterSyntaxNodeAction(sources.Node(c => Literal(c, langs)), SyntaxKind.StringLiteralExpression,
                SyntaxKind.InterpolatedStringExpression);
            start.RegisterSyntaxNodeAction(sources.Node(c => Panel(c, langs)), SyntaxKind.InvocationExpression);
            start.RegisterOperationAction(sources.Operation(c => Knob(c, langs)), OperationKind.ObjectCreation,
                OperationKind.Invocation);
            start.RegisterAdditionalFileAction(c => Mismatch(c, langs));
        });
    }

    private static void Literal(SyntaxNodeAnalysisContext context, LangFile[] langs)
    {
        var text = context.Node switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax { Contents: { Count: 1 } contents } when
                contents[0] is InterpolatedStringTextSyntax only =>
                only.TextToken.ValueText,
            _ => ""
        };
        if (KeyLiteral.IsMatch(text) && Missing(langs, text) is { } missing)
            context.ReportDiagnostic(Diagnostic.Create(Rules.MissingKey, context.Node.GetLocation(), text, missing));
    }

    // panel.Value("fps", ...) renders "hud-fps"
    private static void Panel(SyntaxNodeAnalysisContext context, LangFile[] langs)
    {
        var call = (InvocationExpressionSyntax)context.Node;
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name,
            MemberBindingExpressionSyntax b => b.Name,
            _ => null
        };
        if (name is not IdentifierNameSyntax { Identifier.ValueText: var renderer } ||
            Array.IndexOf(Renderers, renderer) < 0) return;
        if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression) || !PanelKey.IsMatch(literal.Token.ValueText)) return;
        var key = "hud-" + literal.Token.ValueText;
        if (Missing(langs, key) is { } missing)
            context.ReportDiagnostic(Diagnostic.Create(Rules.MissingKey, literal.GetLocation(), key, missing));
        if (renderer == "Section") return;
        foreach (var lang in langs)
            if (lang.TryGet(key, out var text) && text.IndexOf('{') >= 0)
                context.ReportDiagnostic(Diagnostic.Create(Rules.Placeholder, literal.GetLocation(), key, renderer,
                    lang.Name, text));
    }

    // Knob rows: new("Key", "page", "group", ...) or a factory returning a Knob, Switch("Key", "page", "group", ...); a null page is not shown
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "lang keys are lower case")]
    private static void Knob(OperationAnalysisContext context, LangFile[] langs)
    {
        var (type, arguments) = context.Operation switch
        {
            IObjectCreationOperation creation => (creation.Type, creation.Arguments),
            IInvocationOperation call => (call.TargetMethod.ReturnType, call.Arguments),
            _ => (null, [])
        };
        if (type is not { Name: "Knob" }) return;
        var key = Argument(arguments, "key");
        if (key?.ConstantValue is not { HasValue: true, Value: string knob } ||
            Argument(arguments, "page")?.ConstantValue is not { HasValue: true, Value: string page })
            return;
        var group = Argument(arguments, "group")?.ConstantValue is { HasValue: true, Value: string name }
            ? "settings-" + name
            : null;
        foreach (var lang in (string?[])["settings-" + knob.ToLowerInvariant(), "settings-page-" + page, group])
            if (lang is not null && Missing(langs, lang) is { } missing)
                context.ReportDiagnostic(Diagnostic.Create(Rules.KnobKey, key.Syntax.GetLocation(), knob, page, lang,
                    missing));
    }

    private static IOperation? Argument(ImmutableArray<IArgumentOperation> arguments, string parameter)
    {
        return arguments
            .FirstOrDefault(a => string.Equals(a.Parameter?.Name, parameter, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static void Mismatch(AdditionalFileAnalysisContext context, LangFile[] langs)
    {
        var file = Array.Find(langs, l => l.Path == context.AdditionalFile.Path);
        if (file is null) return;
        foreach (var key in file.Keys)
            if (Missing(langs, key) is { } missing)
                context.ReportDiagnostic(
                    Diagnostic.Create(Rules.LangMismatch, file.Where(key), key, file.Name, missing));
    }

    // The language files that lack the key, or null when all have it
    private static string? Missing(LangFile[] langs, string key)
    {
        if (langs.Length == 0) return "every language file: no assets/**/lang/*.json is passed as AdditionalFiles";
        var missing = langs.Where(l => !l.TryGet(key, out _)).Select(l => l.Name).ToArray();
        return missing.Length == 0 ? null : string.Join(", ", missing);
    }
}

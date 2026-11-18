# Coding rules

The build is the gate: `TreatWarningsAsErrors` with `AnalysisLevel latest-all`, the .NET analyzers, Sonar, Roslynator,
the IDE style rules (`.editorconfig`) and Komet's own analyzer `tools/Komet.Rules`. `KometQuick=true`
(`build.sh quick`, `scripts/shadertest.sh`) drops the third-party analyzers for speed; release and CI run everything.

## Komet.Rules (errors unless noted)

| Rule | What |
|---|---|
| KR0001 | No `while`/`do` loops; use `for` with a constant bound. |
| KR0002 | No `goto`. |
| KR0003 | No preprocessor directives. |
| KR0004 | A `for` bound is a constant or `Math.Min(x, Constant)`. |
| KR0005 | `foreach` only over `Contracts.Bounded(...)`, a collection expression (spreads only of `Bounded`) or an array initializer. |
| KR0006/7 | No recursion, direct or mutual; walk trees with a bounded stack. |
| KR0008 | Functions at most 60 lines (warning; not an error in `Komet.Vulkan` and `Komet.Ui`). |
| KR0010/11 | Every used lang key exists, in every language file. |
| KR0012 | A key drawn without arguments has no placeholder. |
| KR0013 | Every knob shown on a settings page has its lang key. |

```csharp
for (var i = 0; i < Math.Min(items.Length, 64); i++) Use(items[i]);
foreach (var item in items.Bounded(64)) Use(item);
```

## Style (.editorconfig)

- File-scoped namespaces, 4 spaces, LF, code wraps at 120 columns (comments up to 140).
- Compact bodies: a one-expression member is `=> expression;`; an `if` with one `return`/`continue`/`break`/`throw`/call
  stands on one line without braces where it fits. Multi-line bodies keep braces.
- Discard return values explicitly: `_ = set.Add(value);` (IDE0058, RCS1163).
- Primary constructors (IDE0290), collection expressions (IDE0300–0306), no unused members, parameters or values.
- Nullable is on; check nulls (`Contracts.NotNull` counts as a guard) instead of using `!`.
- String search and comparison state a `StringComparison`.
- Catching every exception is allowed (CA1031 off): at thread and engine boundaries an escaping exception ends the game.

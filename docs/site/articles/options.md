# Options pages

<xref:Komet.Api.KometOptions> adds pages to Komet's options window. A page is a list of rows; each method of
<xref:Komet.Api.OptionPage> adds one and returns the page, so calls chain.

```csharp
KometOptions.Page("mymod", "My mod", section: "Mods")
    .Group("Look")
    .Switch("Warm picture", () => Config.Warm, v => Config.Warm = v, hint: "Turns the warm pass on.")
    .Slider("Warmth", 0, 1, 0.05, () => Config.Warmth, v => Config.Warmth = v).Default(0.7)
    .Choice("Quality", ["Fast", "Fine"], () => Config.Quality, v => Config.Quality = v)
    .Slider("Distance", 0, 512, 32, () => Config.Distance, v => Config.Distance = v, unit: " blocks")
        .Format(v => v >= 512 ? "unlimited" : v + " blocks")
        .EnabledWhen(() => Config.Warm)
    .Button("Settings file", "Open", OpenFolder);

KometOptions.Applied += (_, _) => Config.Save();
```

| Row | Value |
|---|---|
| `Group` | a heading |
| `Switch` | 0 or 1 |
| `Slider` | a number in min..max, in steps |
| `Choice` | the index of a name |
| `Button` | none; runs at once |

`Format`, `EnabledWhen` and `Default` change the row added last.

## Rules

- Texts are yours to translate: pass `Lang.Get(...)` results.
- Setters run when the player changes a row; saving is yours, best in `Applied`.
- Reusing a page id starts the page over: a rejoin does not double the rows.
- At most 32 pages from all mods, 40 rows per page.

A shader mod gets a page from `komet.json` without code; see the [komet.json reference](komet-json.md).

# KapiStyles · KapiStyle

## Members and usage

`komet.Styles.Register(style)` adds a menu style from code. Reusing an id replaces the style; afterwards
`komet.Styles.Changed` fires. Code may register up to 32 styles, all mods together (`KapiStyles.MaxStyles`). `Register`
returns false for an empty id or too many styles. `komet.Dispose()` and the end of the world take your styles away.

```csharp
komet.Styles.Register(new KapiStyle("mymod-night", "Night")
{
    Base = "pragmatic",
    Background = "config/night.fsh",
    Colors = { ["accent"] = "#7A5CFF" }
});
```

`new KapiStyle(id, name)` takes the style's id (unique among all styles) and the name in the style list. It has
`Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo` and `Background`. Files are relative to your mod's
assets. What the keys mean is on the "Menu styles" page.

[API index](docs-api)

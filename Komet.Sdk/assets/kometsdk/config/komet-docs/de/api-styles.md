# KapiStyles · KapiStyle

## Methoden und Verwendung

`komet.Styles.Register(style)` meldet einen Menü-Stil aus Code an. Eine schon vergebene ID ersetzt den Stil, danach
feuert `komet.Styles.Changed`. Code kann bis zu 32 Stile anmelden, alle Mods zusammen (`KapiStyles.MaxStyles`).
`Register` gibt false zurück bei leerer ID oder zu vielen Stilen. `komet.Dispose()` und das Ende der Welt nehmen deine
Stile weg.

```csharp
komet.Styles.Register(new KapiStyle("mymod-night", "Night")
{
    Base = "pragmatic",
    Background = "config/night.fsh",
    Colors = { ["accent"] = "#7A5CFF" }
});
```

`new KapiStyle(id, name)` nimmt die ID des Stils (eindeutig unter allen Stilen) und den Namen in der Stil-Liste. Er hat
`Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo` und `Background`. Dateien sind relativ zu den Assets
deiner Mod. Was die Schlüssel bedeuten, steht auf der Seite „Menü-Stile“.

[API-Index](docs-api)

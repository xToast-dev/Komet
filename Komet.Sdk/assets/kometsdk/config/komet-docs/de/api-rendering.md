# KapiRendering · KapiRenderStep

## Render-Schritte

Ein Render-Schritt ist eigener Code an einer benannten Stelle in Komets Frame. Komet führt ihn als Renderer des Spiels
in dieser Stage aus, unter OpenGL wie unter Vulkan, misst ihn, verbucht ihn im Debug-Protokoll und zeigt ihn auf der
Seite Verbundene Mods.

```csharp
var outline = komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, DrawOutline)
{
    Order = 0.6,
    Enabled = () => config.Outline
});
if (!outline.Active) api.Logger.Warning("mymod: {0}", outline.Error);
```

| Mitglied                       | Was es tut                                                                    |
|--------------------------------|-------------------------------------------------------------------------------|
| `komet.Rendering.AddStep(step)`| Fügt den Schritt hinzu; gibt eine `KapiRegistration` zurück (`Active`, `Error`, `Dispose()`) |
| `KapiRenderStep(id, stage, render)` | Der Schritt: seine ID in deiner Mod, seine Stage und was er zeichnet (bekommt die Sekunden des Frames) |
| `Order`                        | Innerhalb der Stage läuft Niedrigeres zuerst; 0,5 als Standard                |
| `Range`                        | Die Render-Reichweite des Spiels; 0 als Standard                              |
| `Enabled`                      | Wird vor jedem Frame gefragt; null läuft immer                                |
| `KapiRendering.MaxSteps`       | Wie viele Schritte alle Mods zusammen anlegen dürfen: 64                      |
| `KapiRendering.Renderer`       | Der Renderer, der die Welt gerade zeichnet                                    |

## Stages

`KapiRenderStage` nennt die Stellen in der Reihenfolge des Frames: `Before`, `Opaque`, `Transparent`,
`AfterTransparent`, `ShadowFar`, `ShadowNear`, `AfterPostProcessing`, `AfterBlit`, `Gui`, `AfterFinal`, `Done`.
Zeichne mit der Render-API des Spiels, wie ein `IRenderer` derselben Stage es täte.

## Regeln

- Dieselbe ID ersetzt deinen vorher angelegten Schritt. Ein Schritt läuft ab dem nächsten Frame, bis seine
  Registrierung entsorgt wird, `komet.Dispose()` läuft oder die Welt schließt.
- Eine leere ID oder eine volle Tabelle wird abgelehnt: `Active` ist false, `Error` sagt, warum.
- Ein Schritt, der eine Ausnahme wirft, ist für die Welt aus. Das Log bekommt `Komet: render step modid:id threw` mit
  der Ausnahme, die Seite Verbundene Mods zeigt den Fehler.
- Der Spieler kann alle Schritte deiner Mod auf der Seite Verbundene Mods für die Sitzung ausschalten. Deine anderen
  Registrierungen bleiben.
- Während einer Aufnahme mit `.komet debug` wird jeder Schritt als `modid:id` verbucht, wie bei
  `komet.Debug.Measure`.

## Seite Verbundene Mods

*Entwickler › Verbundene Mods* (mit dem Entwicklermodus des Spiels) listet jede über KAPI verbundene Mod: einen Schalter für ihre Render-Schritte und
eine Karte mit der verlangten KAPI-Version, dem, was sie angemeldet hat (Schritte, Passes, Shader-Registrierungen,
Seiten, Features, Holds, Styles), und jedem Render-Schritt mit Stage und Millisekunden pro Frame oder seinem Fehler.
Mods, die Komet abgelehnt hat, etwa wegen einer neueren KAPI-Version, stehen darunter mit dem Grund.

[API-Index](docs-api)

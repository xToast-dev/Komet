# KapiDebug · KapiTiming

## Methoden und Verwendung

Schreibt eigene Zeitmessungen ins Debug-Protokoll.

```csharp
using (komet.Debug.Measure("pathfinding")) FindPath();
```

- `komet.Debug.Measure(what)` geht auf jedem Thread und verbucht die Zeit als `modid:what`. Es gibt ein `KapiTiming`
  zurück; sein `Dispose()` (oder `using`) verbucht die Zeit. Außerhalb einer Aufnahme kostet es nur das Lesen eines
  Felds und belegt keinen Speicher.
- Eine Aufnahme startest du mit `.komet debug` oder im Protokoll-Tab des HUD-Fensters ({key:komet-hud-window}). Das
  Protokoll listet jeden Namen mit Gesamtzeit, Anzahl der Aufrufe und Zeit pro Aufruf, die teuersten zuerst.
- `KapiDebug.Capturing` sagt, ob gerade aufgenommen wird. Prüf es vor Arbeit, die sich nur für das Protokoll lohnt.
- `modid:what` darf höchstens 128 Zeichen haben (`KapiDebug.MaxName`); längere misst Komet nicht. Eine Aufnahme behält
  bis zu 1024 Namen.

[API-Index](docs-api)

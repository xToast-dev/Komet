# API-Referenz

## Namespace Komet.Api

KAPI ist Komets Schnittstelle für andere Mods. Wähle einen Teil. Die Suche links findet API-Namen und Themen.

[KapiOptions · KapiPage](docs-api-options)

[KapiFeatures · KapiFeature](docs-api-features)

[KapiShaders · KapiPass](docs-api-shaders)

[KapiRendering · KapiRenderStep](docs-api-rendering)

[KapiStyles · KapiStyle](docs-api-styles)

[KapiDebug · KapiTiming](docs-api-debug)

## Verbinden

`Kapi.Connect(this, "1.0")` im `StartClientSide` deines Mod-Systems gibt ein `KapiMod` zurück: deine Verbindung.
Alles andere hängt daran: `Shaders`, `Rendering`, `Options`, `Features`, `Debug` und `Styles`. Deine Mod-ID kommt aus
deiner `modinfo.json`; jede Seite, jeder Pass, jeder Render-Schritt, jede Funktion, Zeitmessung und jeder Stil, den du
anmeldest, trägt sie. Kein Aufruf fragt danach. Jede verbundene Mod steht auf Komets Seite Verbundene Mods.

```csharp
var komet = Kapi.Connect(this, "1.0");
if (komet is null) { StartOwn(); return; } // ein Komet ohne KAPI 1.0: das Log sagt, warum
komet.Shaders.Uniform("mymodWarmth", () => config.Warmth);
komet.Options.Page("Meine Mod").Switch("Warmes Bild", () => config.Warm, v => config.Warm = v);
```

| Member                    | Was es tut                                                                               |
|---------------------------|------------------------------------------------------------------------------------------|
| `Kapi.Connect`        | Verbindet deine Mod; null, wenn die Version nicht unterstützt ist oder die Mod keine ID hat |
| `Kapi.Version`        | KAPIs Version als `major.minor`, hier `1.0` (`Major`, `Minor` als Zahlen)                |
| `Kapi.Supports(v)`    | Ob sich eine für Version `v` geschriebene Mod verbinden kann                             |
| `komet.ModId`, `Version`  | Deine Mod-ID und die Version, die du verlangt hast                                       |
| `komet.IsConnected`       | Falsch nach `Dispose()` oder wenn die Welt geschlossen ist                               |
| `komet.Dispose()`         | Nimmt alles zurück, was die Verbindung angemeldet hat                                    |

## Versionen

KAPI ist als `major.minor` versioniert; dieser Komet bietet KAPI 1.0. Deine Mod nennt die Version, für die sie
geschrieben ist, in `Connect` und nirgends sonst: nicht in der `modinfo.json`, nicht in einem Attribut.

- Komet verbindet deine Mod, wenn die Hauptversionen gleich sind und seine eigene Nebenversion gleich oder neuer ist.
  Eine Mod für 1.0 verbindet sich mit 1.0, 1.1, 1.2 und so weiter; eine Mod für 1.2 nicht mit 1.0 oder 1.1, und keine
  mit 2.0.
- Passt die Version nicht, gibt `Connect` null zurück und loggt `Komet: KAPI did not connect <modid>: ...` mit beiden
  Versionen. Starte dann deinen eigenen Weg, wie ohne Komet. `Kapi.Supports("1.2")` fragt vorher, ohne zu loggen.
- Das Versprechen: Eine neue Nebenversion fügt nur hinzu (Typen, Member, Überladungen), Code für 1.0 kompiliert und
  läuft also weiter. Eine neue Hauptversion darf ändern oder entfernen; sie kommt mit einer Umstiegstabelle im
  Changelog.
- Verlange die niedrigste Version, die alles hat, was du nutzt. Eine neuere Nebenversion nur, wenn du etwas aufrufst,
  das sie hinzugefügt hat.

## Einbindung und Lebenszyklus

Halte Komet optional: referenziere `Komet.Core.dll` mit `Private="false"`, nenne KAPIs Typen nur in einer
Bridge-Klasse und rufe sie nur auf, wenn `api.ModLoader.IsModEnabled("komet")` wahr ist. Das ganze Beispiel steht unter
„Komet optional integrieren“.

- Ruf KAPI vom Haupt-Thread auf, in `StartClientSide`. Nur `komet.Debug.Measure` darf jeder Thread aufrufen.
- Eine Verbindung hält eine Welt lang. Schließt die Welt, nimmt Komet Seiten, Passes, Render-Schritte, Defines,
  Uniforms, Edits, Zurückhaltungen, Stile und Event-Handler zurück; verbinde dich im nächsten `StartClientSide` neu.
- `komet.Dispose()` nimmt sie früher zurück, alle auf einmal. Deine Funktionen bleiben bis zum Ende der Welt, weil sich
  Harmony-Patches nicht alle auf einmal zurücknehmen lassen.
- Wirft ein Event-Handler eine Ausnahme, loggt Komet sie und meldet den Handler ab.
- Was keine Verbindung braucht, ist statisch: `KapiShaders.Available`, `KapiShaders.Renderer`, `KapiShaders.Reload()`,
  `KapiFeatures.StateOf`, `KapiFeatures.Snapshot`, `KapiFeatures.FingerprintOf` und `KapiDebug.Capturing`.

[Komet optional integrieren](docs-integration)

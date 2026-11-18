# KapiShaders · KapiPass

## Dein Effekt oder dein eigener Renderer

`komet.Shaders.TryAddPasses` kompiliert deine Passes sofort und meldet sie zusammen an, oder keinen davon. Lehnt es ab,
starte stattdessen deinen eigenen Renderer: Genau einer der beiden zeichnet.

```csharp
var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", source) { Enabled = () => config.Grade });
if (!fx.Active) StartOwn(); // fx.Error und das Log sagen, warum
```

[Komet optional integrieren](docs-integration)

## Methoden und Verwendung

`komet.Shaders` ändert die Shader des Spiels und zeichnet eigene Vollbild-Effekte, unter OpenGL wie unter Komets
Vulkan-Renderer.

| Mitglied                                | Was es tut                                                                                                        |
|-----------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| `Define(name, value, programs)`         | Setzt `#define name value` in Programme; `value` wird beim Laden gelesen, nach einer Änderung also `Reload()` aufrufen |
| `Uniform(name, value)`                  | Ein Uniform, das Komet jedes Bild setzt: `Func<float>` oder `Func<float[]>` mit 1 bis 4 Zahlen                    |
| `Edit(program, vertex, fragment)`       | Schreibt den Quelltext eines Programms beim Laden um; `*` bearbeitet jedes Programm                               |
| `AddPass(pass)`                         | Ein eigener Vollbild-Pass, gebaut, sobald der Renderer bereit ist                                                 |
| `TryAddPasses(passes)`                  | Kompiliert die Passes sofort und meldet alle oder keinen an; für den Rückfall auf deinen eigenen Renderer         |
| `EditPass(modId, passId, fragment)`     | Schreibt den Pass einer anderen Mod (oder einen eigenen) vor dem Kompilieren um                                   |
| `RendererChanged`                       | Der Renderer hat gewechselt; Komet lädt neu und baut die Passes selbst wieder                                     |
| `KapiShaders.Reload()`                  | Baut die Shader des Spiels mit allen Defines und Edits neu (statisch)                                             |
| `KapiShaders.Renderer`                  | `KapiRenderer.OpenGL` oder `KapiRenderer.Vulkan` (statisch)                                                       |
| `KapiShaders.Available`                 | Komets Shader-Modul läuft und nimmt Anmeldungen an (statisch)                                                     |

```csharp
KapiRegistration hour = komet.Shaders.Uniform("mymodHour", () => api.World.Calendar.HourOfDay);
hour.Dispose(); // nimmt es zurück, bevor die Welt schließt
```

- Jede Anmeldung gibt eine `KapiRegistration` zurück: `Active` sagt, ob Komet sie angenommen hat, `Error`, warum nicht,
  `Dispose()` nimmt sie vorzeitig zurück. `komet.Dispose()` nimmt alle zurück.
- `programs` nennt Programme des Spiels (`chunkopaque`, `final`, ...) oder `pass:id` für einen deiner Passes; ohne
  Angabe bekommt jedes Programm und jeder Pass deiner Mod das Define.
- Jedes Programm hat `#define KOMET 1`, unter Vulkan zusätzlich `#define KOMET_VULKAN 1`.
- Für alle Mods zusammen nimmt Komet bis zu 128 Defines, 256 Uniforms, 64 Edits und 32 Passes an
  (`KapiShaders.MaxDefines`, `MaxUniforms`, `MaxEdits`, `MaxPasses`).

[Shader und Passes](docs-shaders)

[Renderer-Integration für Mods](docs-vulkan-shaders)

[API-Index](docs-api)

## KapiPass

`new KapiPass(id, fragment)`: `id` ist innerhalb deiner Mod eindeutig (dieselbe ID ersetzt deinen früheren Pass),
`fragment` ist GLSL 330, das `main()` definiert und `color` schreibt. Komet setzt einen Vorspann davor mit `uv`,
`scene`, `depth`, `resolution` und `time`. `Stage` ist `KapiStage.Final` (nach der finalen Komposition des Spiels, vor
der GUI), `Order` sortiert Passes einer Stufe (niedrigere zuerst), `Enabled` wird jedes Bild gefragt (null: immer),
`Uniforms` hält Uniforms dieses Passes nach Namen. Zeilennummern in Kompilierfehlern sind deine. Ein fehlgeschlagenes
Neuladen behält die Programme, die vorher liefen.

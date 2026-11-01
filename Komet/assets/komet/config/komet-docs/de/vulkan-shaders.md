# Shader für Vulkan

Diese Seite ist für Shader-Autoren. Sie zeigt, wie Komet deine Shader unter seinem Vulkan-Renderer zeichnet, welche Regeln dafür gelten und was in der Vulkan-Vorlage steckt. Wie Vulkan für Spieler funktioniert, steht in „Vulkan erklärt“ unter Hilfe. Die Felder der `komet.json` stehen in „Shader-Referenz“, Wörter wie Pass oder Uniform in „Begriffe“ unter Hilfe.

## So läuft dein Shader

Du schreibst keine Zeile Vulkan-Code. Du schreibst GLSL wie für das Spiel, und Komet erledigt den Rest:

```flow
Das Spiel lädt deinen Shader wie immer
Komet setzt KOMET und deine Defines davor
OpenGL kompiliert ihn wie immer
Unter Vulkan liest Komet denselben Quelltext
shaderc übersetzt ihn nach SPIR-V
Vulkan zeichnet ihn im selben Bild
```

- `KOMET` ist mit Komet immer gesetzt, `KOMET_VULKAN` nur, solange Komets Vulkan-Renderer zeichnet. Wechselt der Renderer, baut Komet alle Shader und Passes selbst neu, damit `KOMET_VULKAN` wieder stimmt.
- Bei Mods mit eigenen Shader-Dateien übersetzt Komet auch die Shader, die noch nicht gezeichnet wurden. Unter Shader › Aktive Shader siehst du, ob es geklappt hat.
- Mit **Vulkan: Shader speichern** (Komet › Vulkan, sichtbar mit Komet › Werkzeuge › **Erweiterte Optionen zeigen**) speichert Komet übersetzte Shader auf der Festplatte. Änderst du deinen Shader, übersetzt Komet ihn neu.
- Die meisten Mods brauchen `#ifdef KOMET_VULKAN` nie. Nimm es nur für Arbeit, die sich nur unter Vulkan lohnt, oder um einen Unterschied zu umgehen, den du gefunden hast.

## Regeln für saubere Ports

Ein Port ist die übersetzte Fassung deines Shaders. Hältst du dich an diese Regeln, klappt er immer:

- Schreib GLSL wie für OpenGL 3.3 core (`#version 330 core`; in einem Pass setzt Komet die Zeile selbst): `in` und `out`, `texture()`. Kein `gl_FragColor`, `varying`, `attribute` oder `texture2D`.
- Uniforms: `float`, `vec2` bis `vec4`, `int` bis `ivec4` und `mat4`. Meide `uint`-Uniforms, `mat2`, `mat3` und Uniform-Structs: Ihre Werte kommen unter Vulkan nicht an.
- Sampler: `sampler2D`, `sampler2DShadow`, `samplerCube` und `sampler2DArray`, höchstens 32 pro Programm. Keine Sampler-Arrays: Was damit zeichnet, fehlt unter Vulkan im Bild.
- Array-Größen müssen nach dem Präprozessor schlichte Zahlen sein oder Summen und Produkte davon, zum Beispiel `4 * 3 + 1`.
- Der Startwert einer Uniform (`uniform float a = 0.5;`) muss eine Zahl sein oder ein Konstruktor aus Zahlen wie `vec3(1.0, 0.5, 0.0)`.
- Keine Interface-Blöcke (`out Block { ... }`), keine Geometry- oder Tessellation-Shader.
- Uniform-Blöcke und Storage-Buffer funktionieren, mit einem `binding` von 0 bis 15.
- Die Eingänge eines eigenen Vertex-Shaders brauchen eine feste Stelle: `layout(location = 0) in vec3 pos;` oder `glBindAttribLocation` im Code.
- Die Koordinaten sind auf beiden Renderern gleich: `uv` (0, 0) ist unten links, die Tiefe läuft von 0 bis 1 mit 1 für den Himmel, `gl_FragCoord` zählt von unten. Dreh für Vulkan nichts um.

## Was Vulkan nicht zeigt

Unter Vulkan zeichnet Komet alles selbst, auch das, was andere Mods direkt mit OpenGL zeichnen, soweit Vulkan es ausdrücken kann. Was nicht geht, **fehlt im Bild**. OpenGL füllt die Lücke nicht, denn sein Bild wird unter Vulkan nie gezeigt.

- Ein Bild-Effekt als Pass (`passes` in der `komet.json` oder `KometShaders.AddPass`) läuft auf beiden Renderern. Das ist der sichere Weg.
- Eigene OpenGL-Framebuffer einer Mod übernimmt Komet oft, aber nicht immer. Was nicht klappt, nennt das Log in einer Zeile mit `Komet: Vulkan cannot draw`.
- Lässt sich das nicht ändern, schreib `"renderer": "opengl"` in deine `komet.json`. Solange deine Mod läuft, nimmt Komet dann OpenGL statt Vulkan, und ihr Steckbrief unter Aktive Shader sagt, warum. So hält es Komet auch mit SheyderMod.

## Testen und das Log

1. Stell Komet › Übersicht › **Renderer** auf OpenGL und schau dir dein Bild an. Dann stell ihn auf Vulkan: Das Bild muss gleich aussehen. Nach dem Übernehmen wirkt der Wechsel sofort.
2. Wähl unter Shader › Aktive Shader deine Mod aus. Ihr Steckbrief sagt, ob alle Shader für Vulkan übersetzt sind, oder nennt den ersten, der nicht durchkam.
3. Such in `client-main.log` nach diesen Zeilen:

| Zeile im Log | Bedeutung |
|---|---|
| `Komet: Vulkan cannot draw X (no Vulkan port: ...)` | Der Port von X ist gescheitert; der Grund nennt die Regel. X fehlt im Bild |
| `Komet: Vulkan cannot draw ...: it is missing from the picture` | Ein Zeichenauftrag, den Vulkan nicht übernehmen kann |
| `Komet: Vulkan port of X: 35 ms ...` | Nur die Zeit: Der Port hat geklappt |
| `Komet shaders: pass ... is left out` | Ein Pass kompiliert schon unter OpenGL nicht |

X ist der Name des Programms, zum Beispiel `chunkopaque`. Bei einem Pass oder einem Programm, das eine Mod direkt mit OpenGL baut, steht dort eine Nummer, etwa `program 123`. Programme, die eine Mod über die Shader-API des Spiels anmeldet, tragen ihren Namen. Steht in einer Zeile `Vulkan port of` statt der Millisekunden ein Grund, ist der Port ebenfalls gescheitert.

## Die Vulkan-Vorlage

Modding › Vorlagen › **Vulkan-Vorlage anlegen** schreibt ein vollständiges Shader-Paket nach `ModConfig/komet/mod-template/myshaders-dev` (ist der Name vergeben, nach `myshaders-dev-2` und so weiter) und öffnet den Ordner. Es läuft auf OpenGL und auf Komets Vulkan-Renderer und hält sich an alle Regeln oben.

### Was drin ist

```text
myshaders-dev/
  README.md                         the guide, in English
  myshaders/                        the mod, ready for the Mods folder
    modinfo.json                    rename "myshaders" to your id
    assets/myshaders/
      config/komet.json             settings, passes, renderer
      shaders/haze.fsh              pass 1: distance haze from the depth
      shaders/grade.fsh             pass 2: colour grading with a define
      lang/en.json, lang/de.json    the texts of the settings
  code/                             optional C# part
    MyShaders.csproj
    MyShadersSystem.cs
    KometBridge.cs
```

Der Code liegt neben der Mod, nicht darin: Das Spiel lehnt eine gezippte Mod ab, die `.cs`-Dateien außerhalb von `src/` enthält.

### Ausprobieren

1. Öffne Modding › Vorlagen › **Mods-Ordner** und kopier den Ordner `myshaders` aus `myshaders-dev` hinein.
2. Betritt die Welt erneut. Unter Shader › Aktive Shader steht „My Shaders“ in der Gruppe „Für Komet gemacht“. Außerdem hat die Mod eine eigene Seite unter Shader.
3. Ändere **Belichtung**: Das Bild ändert sich sofort (eine Uniform). Ändere **Qualität**: Die Shader werden neu gebaut (ein Define).
4. Schalte Komet › Übersicht › **Renderer** zwischen OpenGL und Vulkan um. Das Bild muss gleich aussehen.

### Zu deiner Mod machen

1. Wähl eine ID aus Kleinbuchstaben ohne Leerzeichen. Ersetze `myshaders` überall: den Ordner unter `assets/`, `modid` in der `modinfo.json`, die Sprachschlüssel `myshaders:` in der `komet.json`, die Namen der Uniforms, die Defines `MYSHADERS_` und im Code-Teil den Wert von `Mod` in `KometBridge.cs`.
2. Ändere die Passes und speichere. Shader › Aktive Shader › **Packs neu laden** zeigt die Änderung ohne Neustart.
3. Kompiliert ein Pass nicht, lässt Komet ihn weg. Das Feld rechts unter Aktive Shader und das `client-main.log` (Zeilen mit `Komet shaders: pass`) nennen die Zeile. Die Zeilennummern zählen ab deiner Datei.

### Von komet.json zum Shader

| In der komet.json | Im Shader | Wirkt |
|---|---|---|
| `"uniform": "myshadersExposure"` | `uniform float myshadersExposure;`, von dir deklariert | sofort |
| `"define": "MYSHADERS_QUALITY"` | `#define MYSHADERS_QUALITY 0` oder `1` vor deiner Datei | baut die Shader neu |
| `"programs": ["pass:grade", "pass:haze"]` | welche Passes die Defines bekommen | |
| `"when": "grade"` an einem Pass | der Schalter, der den Pass an- und ausschaltet | sofort |
| `"order"` an einem Pass | kleinere Werte zuerst: haze (10) vor grade (20) | |

Jeder Pass bekommt `#define KOMET 1` und, solange Komets Vulkan-Renderer zeichnet, auch `#define KOMET_VULKAN 1`.

### Der Code-Teil

`code/` gibt der Mod C# für das, was eine JSON-Datei nicht kann. In der Vorlage reicht er die Uhrzeit des Spiels an den Dunst-Pass weiter. So wird der Dunst nachts dunkel. Das Herzstück steht in `KometBridge.cs`:

```csharp
KometShaders.Define(Mod, "MYSHADERS_CODE", () => "1", "pass:haze");
KometShaders.Uniform(Mod, "myshadersHour", () => api.World.Calendar.HourOfDay);
```

1. Bau den Teil im Ordner `code/` mit `dotnet build -c Release -p:VintageStory=<Spielordner> -p:Komet=<Pfad zu Komet.Core.dll>`. Statt `-p:VintageStory` geht auch die Umgebungsvariable `VINTAGE_STORY`.
2. `MyShaders.dll` landet in `myshaders/` neben der `modinfo.json`. Setz dort `"type": "code"`.
3. Komet bleibt optional: `MyShadersSystem` ruft `KometBridge` nur auf, wenn `IsModEnabled("komet")` wahr ist. `Komet.Core.dll` ist mit `Private="false"` eingebunden und wird nie mitgeliefert.
4. Nimm im Code die Domain deiner Mod als Mod-ID, hier `"myshaders"`. Nur dann gibt Komet die Defines aus dem Code auch an die Passes aus der `komet.json`.
5. Komet vergisst alle Anmeldungen, wenn die Welt schließt. `StartClientSide` meldet sie bei jedem Betreten einer Welt neu an.

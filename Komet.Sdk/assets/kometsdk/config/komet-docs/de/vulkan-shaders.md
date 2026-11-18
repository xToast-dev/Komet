# Renderer-Integration für Mods

[API: KapiShaders](docs-api-shaders)

[Komet optional integrieren](docs-integration)

## Regeln für saubere Ports

Ein Port ist die übersetzte Fassung deines Shaders. Hältst du dich an diese Regeln, klappt er immer:

- Schreib GLSL wie für OpenGL 3.3 core (`#version 330 core`; in einem Pass setzt Komet die Zeile selbst): `in` und
  `out`, `texture()`. Kein `gl_FragColor`, `varying`, `attribute` oder `texture2D`.
- Uniforms: `float`, `vec2` bis `vec4`, `int` bis `ivec4` und `mat4`. Meide `uint`-Uniforms, `mat2`, `mat3` und
  Uniform-Structs: Ihre Werte kommen unter Vulkan nicht an.
- Sampler: `sampler2D`, `sampler2DShadow`, `samplerCube` und `sampler2DArray`, höchstens 32 pro Programm. Keine
  Sampler-Arrays: Was damit zeichnet, fehlt unter Vulkan im Bild.
- Array-Größen müssen nach dem Präprozessor schlichte Zahlen sein oder Summen und Produkte davon, zum Beispiel
  `4 * 3 + 1`.
- Der Startwert einer Uniform (`uniform float a = 0.5;`) muss eine Zahl sein oder ein Konstruktor aus Zahlen wie
  `vec3(1.0, 0.5, 0.0)`.
- Keine Interface-Blöcke (`out Block { ... }`), keine Geometry- oder Tessellation-Shader.
- Uniform-Blöcke und Storage-Buffer funktionieren, mit einem `binding` von 0 bis 15.
- Die Eingänge eines eigenen Vertex-Shaders brauchen eine feste Stelle: `layout(location = 0) in vec3 pos;` oder
  `glBindAttribLocation` im Code.
- Die Koordinaten sind auf beiden Renderern gleich: `uv` (0, 0) ist unten links, die Tiefe läuft von 0 bis 1 mit 1 für
  den Himmel, `gl_FragCoord` zählt von unten. Dreh für Vulkan nichts um.

## Was Vulkan nicht zeigt

Unter Vulkan zeichnet Komet alles selbst, auch das, was andere Mods direkt mit OpenGL zeichnen, soweit Vulkan es
ausdrücken kann. Was nicht geht, **fehlt im Bild**. OpenGL füllt die Lücke nicht, denn sein Bild wird unter Vulkan nie
gezeigt.

- Ein Bild-Effekt als Pass (`passes` in der `komet.json` oder `komet.Shaders.AddPass`) läuft auf beiden Renderern. Das
  ist der sichere Weg.
- Eigene OpenGL-Framebuffer einer Mod übernimmt Komet oft, aber nicht immer. Was nicht klappt, nennt das Log in einer
  Zeile mit `Komet: Vulkan cannot draw`.
- Lässt sich das nicht ändern, schreib `"renderer": "opengl"` in deine `komet.json`. Solange deine Mod läuft, nimmt
  Komet dann OpenGL statt Vulkan, und ihr Steckbrief unter Aktive Shader sagt, warum. So hält es Komet auch mit
  SheyderMod.

## Testen und das Log

1. Stell Komet › Übersicht › **Renderer** auf OpenGL und schau dir dein Bild an. Dann stell ihn auf Vulkan: Das Bild
   muss gleich aussehen. Nach dem Übernehmen wirkt der Wechsel sofort.
2. Wähl unter Shader › Aktive Shader deine Mod aus. Ihr Steckbrief sagt, ob alle Shader für Vulkan übersetzt sind, oder
   nennt den ersten, der nicht durchkam.
3. Such in `client-main.log` nach diesen Zeilen:

| Zeile im Log                                                    | Bedeutung                                                                  |
|-----------------------------------------------------------------|----------------------------------------------------------------------------|
| `Komet: Vulkan cannot draw X (no Vulkan port: ...)`             | Der Port von X ist gescheitert; der Grund nennt die Regel. X fehlt im Bild |
| `Komet: Vulkan cannot draw ...: it is missing from the picture` | Ein Zeichenauftrag, den Vulkan nicht übernehmen kann                       |
| `Komet: Vulkan port of X: 35 ms ...`                            | Nur die Zeit: Der Port hat geklappt                                        |
| `Komet shaders: pass ... is left out`                           | Ein Pass kompiliert schon unter OpenGL nicht                               |

X ist der Name des Programms, zum Beispiel `chunkopaque`. Bei einem Pass oder einem Programm, das eine Mod direkt mit
OpenGL baut, steht dort eine Nummer, etwa `program 123`. Programme, die eine Mod über die Shader-API des Spiels
anmeldet, tragen ihren Namen. Steht in einer Zeile `Vulkan port of` statt der Millisekunden ein Grund, ist der Port
ebenfalls gescheitert.

## Die Vulkan-Vorlage

Modding › Vorlagen › **Shader-Mod-Projekt mit optionaler C#-Anbindung** schreibt ein vollständiges Beispiel nach
`ModConfig/komet/mod-template/myshaders-dev` und öffnet den Ordner. Es läuft auf OpenGL und Vulkan, hält sich an alle
Regeln oben, und seine `README.md` erklärt jede Datei. Der C#-Teil liegt in `code/` neben der Mod, weil das Spiel eine
gezippte Mod mit `.cs`-Dateien außerhalb von `src/` ablehnt.

1. Kopiere den Ordner `myshaders` in den Mods-Ordner (Modding › Vorlagen › **Mods-Ordner**) und betritt die Welt erneut.
2. Ändere **Belichtung** (eine Uniform, sofort) und **Qualität** (ein Define, baut die Shader neu).
3. Schalte Komet › Übersicht › **Renderer** zwischen OpenGL und Vulkan um. Das Bild muss gleich aussehen.

Hast du deine Mod auf beiden Renderern geprüft, setze `"renderer": "both"` in deiner `komet.json`. Das hebt auch eine
Vulkan-Sperre aus einem Adapter auf, den Komet für deine Mod mitbringt (wie für SheyderMod).

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

- `KOMET` ist mit Komet immer gesetzt, `KOMET_VULKAN` nur, solange Komets Vulkan-Renderer zeichnet. Wechselt der
  Renderer, baut Komet alle Shader und Passes selbst neu, damit `KOMET_VULKAN` wieder stimmt.
- Bei Mods mit eigenen Shader-Dateien übersetzt Komet auch die Shader, die noch nicht gezeichnet wurden. Unter Shader ›
  Aktive Shader siehst du, ob es geklappt hat.
- Mit **Vulkan: Shader speichern** (Komet › Vulkan, sichtbar mit Komet › Werkzeuge › **Erweiterte Optionen zeigen**)
  speichert Komet übersetzte Shader auf der Festplatte. Änderst du deinen Shader, übersetzt Komet ihn neu.
- Die meisten Mods brauchen `#ifdef KOMET_VULKAN` nie. Nimm es nur für Arbeit, die sich nur unter Vulkan lohnt, oder um
  einen Unterschied zu umgehen, den du gefunden hast.

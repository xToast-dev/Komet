# Shader und Passes

[API: KapiShaders](docs-api-shaders)

## komet.json im Überblick

Die Datei liegt in deiner Mod unter `assets/<deine-modid>/config/komet.json`. Alle Felder sind freiwillig. Schon `{}`
meldet deine Mod bei Komet an: Sie steht dann unter Shader › Aktive Shader in der Gruppe „Für Komet gemacht“.

| Feld             | Was es tut                                                                                                       |
|------------------|------------------------------------------------------------------------------------------------------------------|
| `$schema`        | Lässt deinen Editor Felder vorschlagen und Fehler zeigen (Adresse unten)                                         |
| `_comment`       | Eine Notiz für Leser; Komet liest sie nicht                                                                      |
| `name`, `author` | Titel deiner Seite unter Shader und Autor im Feld rechts unter Aktive Shader; ohne sie gilt deine `modinfo.json` |
| `settings`       | Schalter, Regler und Auswahlen (siehe „Einstellungen“)                                                           |
| `programs`       | Welche Shader deine Defines bekommen (siehe „Einstellungen“)                                                     |
| `passes`         | Bild-Effekte über dem fertigen Bild (siehe „Passes“)                                                             |
| `renderer`       | `"both"` (Standard) oder `"opengl"`: Bei `"opengl"` nimmt Komet OpenGL statt Vulkan, solange deine Mod läuft     |
| `config`         | Steuert das eigene Einstellungsfenster deiner Mod (siehe „Mods mit eigenem Fenster“)                             |
| `holds`          | Komet-Funktionen, die zurückgehalten werden, solange deine Mod läuft (siehe dort)                                |

Die Schema-Adresse für `komet.json` ist
`https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet.schema.json`. Für ein Pack zum Ausprobieren
gilt dieselbe Adresse mit `komet-shader.schema.json` am Ende.

Eine eigene Seite unter Shader bekommt deine Mod, sobald `settings` oder `passes` mindestens einen Eintrag hat.

## Einstellungen

Jeder Eintrag in `settings` wird eine Zeile auf der Seite deines Packs.

| Feld                 | Bedeutung                                                                 |
|----------------------|---------------------------------------------------------------------------|
| `key`                | Eindeutiger Name im Pack; unter ihm speichert Komet den Wert des Spielers |
| `label`              | Der Name der Zeile im Menü                                                |
| `hint`               | Der Erklärtext zur Zeile                                                  |
| `type`               | `switch` (Standard), `slider`, `choice` oder `group`                      |
| `default`            | Der Startwert (Standard `0`)                                              |
| `min`, `max`, `step` | Bereich und Schritt eines Reglers (Standard `0`, `1` und `0.1`)           |
| `unit`               | Die Einheit hinter dem Wert eines Reglers, zum Beispiel `EV`              |
| `options`            | Die Namen einer Auswahl                                                   |
| `define`             | Wird `#define NAME wert`; eine Änderung baut die Shader neu               |
| `uniform`            | Wird `uniform float NAME`; wirkt sofort                                   |

- `switch` liefert 0 oder 1, `choice` die Nummer des Eintrags in `options` ab 0, `slider` eine Zahl von `min` bis `max`.
- Als Define wird ein Schalter oder eine Auswahl zur ganzen Zahl, ein Regler zur Zahl mit Dezimalpunkt, zum Beispiel
  `0.6`.
- `group` ist nur eine Überschrift mit dem Text aus `label`.
- Ein Regler braucht `max` größer als `min` und `step` größer als 0, eine Auswahl braucht `options`. Sonst zeigt Komet
  die Zeile als Schalter.
- `name`, `label`, `hint` und die Namen in `options` dürfen Sprachschlüssel aus deinen Sprachdateien sein
  (`domain:schlüssel`) oder normaler Text.
- `programs` nennt die Shader, die die Defines bekommen: Programme des Spiels wie `final` oder `chunkopaque`, oder einen
  deiner Passes als `pass:<id>`. Ohne `programs` bekommen alle Shader die Defines, auch alle deine Passes.
- Uniforms erreichen jeden Shader und jeden Pass, der sie deklariert, egal was `programs` sagt.
- Ein Pack hat höchstens 40 Einstellungen. Die Werte der Spieler stehen in `ModConfig/komet/shader-settings.json`.

## Passes

Ein Pass ist eine Fragment-Datei, die Komet über das ganze Bild zeichnet. Er läuft, nachdem das Spiel sein Bild fertig
gemischt hat (samt Bloom und Gottesstrahlen), und vor der Oberfläche.

| Feld       | Bedeutung                                                                                                                                    |
|------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| `id`       | Der Name des Passes, eindeutig in deiner Mod                                                                                                 |
| `fragment` | Die Datei: in der `komet.json` ab `assets/<deine-modid>/`, zum Beispiel `shaders/grade.fsh`; in einem Pack zum Ausprobieren im selben Ordner |
| `when`     | Der `key` eines Schalters: Der Pass zeichnet, solange er an ist                                                                              |
| `order`    | Die Reihenfolge über alle Passes aller Mods, kleinere Werte zuerst (Standard `0`)                                                            |

- Ein Pass liest eine Kopie des Bildes bis hierher (`scene`) und überschreibt jeden Bildpunkt mit `color`.
- Ein Pfad mit `..` oder ein absoluter Pfad ist nicht erlaubt.
- Alle Mods zusammen haben höchstens 32 Passes.

Diese Zeilen setzt Komet vor deine Datei. Du schreibst nur `main()` und setzt `color`. Deklarier diese Namen nicht noch
einmal:

```glsl
#version 330 core
#define KOMET 1
#define KOMET_VULKAN 1          // only while Vulkan draws
// then the defines your komet.json gives this pass
in vec2 uv;                     // 0..1, (0, 0) bottom left
out vec4 color;                 // what the pass draws
uniform sampler2D scene;        // the picture so far (a copy)
uniform sampler2D depth;        // the scene's depth, 0..1, 1 = sky
uniform vec2 resolution;        // in pixels
uniform float time;             // seconds, for animation
```

Danach folgt `#line 1`: Die Zeilennummern in Fehlermeldungen zählen ab deiner Datei.

## Was die Symbole bedeuten

Hinter jedem Namen unter Aktive Shader stehen kleine Symbole. Das erste, farbige Symbol zeigt, wer wen steuert:

| Symbol               | Bedeutung                                                                                                   |
|----------------------|-------------------------------------------------------------------------------------------------------------|
| Kettenglied auf Grün | **Eingebunden**: Die Mod meldet sich selbst an, per `komet.json`, als Pack oder über die C#-Schnittstelle   |
| Komet auf Blau       | **Von Komet gesteuert**: Die Mod weiß nichts von Komet; Komet steuert sie über eine mitgelieferte Anbindung |
| Auge auf Grau        | **Nicht eingebunden**: Komet erkennt die Mod an ihren Dateien und steuert nichts                            |

Das zweite, farbige Symbol zeigt, wo die Mod gerade läuft. Bei einer zurückgehaltenen Mod fehlt es:

| Symbol                   | Bedeutung                                                                              |
|--------------------------|----------------------------------------------------------------------------------------|
| Vulkan (Berg) auf Rot    | **Vulkan**: läuft auf Komets Vulkan-Renderer, alle Shader übersetzt                    |
| Vulkan (Berg) auf Orange | **Vulkan, lückenhaft**: Nicht jeder Shader ließ sich übersetzen                        |
| Würfel auf Türkis        | **OpenGL**: läuft auf OpenGL; zeichnet die Mod nur mit OpenGL, sagt das ihr Steckbrief |

Die grauen Symbole danach nennen Einzelheiten:

| Symbol               | Bedeutung                                                                                                               |
|----------------------|-------------------------------------------------------------------------------------------------------------------------|
| Blatt mit Zeilen     | **komet.json**: angemeldet über ihre `komet.json`                                                                       |
| Ordner               | **Eigenes Pack**: ein Shader-Pack aus deinem Ordner                                                                     |
| `< >`                | **C#-Schnittstelle**: angemeldet über KAPI (`komet.Shaders`)                                                                    |
| Zwei Blätter         | **Komet-Fassung**: bringt eigene Dateien für Komet mit (`config/komet/`)                                                |
| Zwei Balken          | **Pausiert**: Komet hält die Mod zurück; sie läuft gerade nicht                                                         |
| Rotes Ausrufezeichen | **Problem**: Ein Shader kompiliert nicht oder ließ sich nicht übersetzen, oder eine Abhängigkeit fehlt oder passt nicht |

Ist keine Zeile ausgewählt, zeigt das Feld rechts diese Legende. Wählst du eine Mod aus, steht dort ihr Steckbrief: wie
sie eingebunden ist, wo sie läuft, wie viele Shader kompilieren, was sie ersetzt und ob eine Abhängigkeit fehlt oder
nicht passt. Ein Dreieck steht dort vor ihren Shadern, zwei Pfeile vor dem, was sie ersetzt.

## Wenn etwas nicht klappt

Fast jedes Problem steht im `client-main.log`. Such nach diesen Zeilen:

| Zeile im Log                                          | Bedeutung                                                                                                                         |
|-------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| `Komet shaders: ... is not a valid komet-shader.json` | Ein JSON-Fehler mit Zeile und Stelle, auch in einer `komet.json`                                                                  |
| `Komet shaders: pack ... names the pass file ...`     | Die Datei aus `fragment` fehlt, oder ihr Pfad ist nicht erlaubt                                                                   |
| `Komet shaders: pass ... is left out`                 | Der Pass kompiliert oder linkt nicht, oder sein `Enabled` wirft. Der Grund steht dahinter; die Zeilennummer zählt ab deiner Datei |
| `Komet shaders: ... which is no GLSL name`            | Ein `define` oder `uniform` ist kein gültiger GLSL-Name (höchstens 64 Zeichen)                                                    |
| `Komet: Vulkan cannot draw`                           | Etwas fehlt nur unter Vulkan (siehe „Renderer-Integration für Mods“)                                                                          |

- Kompiliert ein Shader nicht, den das Spiel lädt, legt Komet seinen ganzen Quelltext unter
  `Logs/komet-shader-<datei>.txt` ab. Die Zeilennummer aus dem Fehler passt genau zu dieser Datei.
- Zeigt das Menü einen Regler als Schalter, prüf `min`, `max` und `step`. Eine Auswahl ohne `options` wird auch zum
  Schalter.
- Komet ergänzt die Include-Dateien `shadowcoords.vsh` und `fogandlight.fsh` um **Schatten in der Ferne** (Werte mit
  `kometDistant` im Namen). Ersetzt deine Mod diese Dateien und erkennt Komet ihren Text nicht wieder, bleiben die
  fernen Schatten aus. Das Log sagt dann `Komet: the shadow shaders are not the engine's, distant shadows stay off`.

## Shader Studio

Das Shader Studio ist Teil von Komet: öffne es mit {key:komet-hud-debug} oder `.komet dev`. Alle anderen Werkzeuge liegen im
HUD-Fenster ({key:komet-hud-window}).

- **Ein Baum** ab deinem Vintagestory-Ordner: Spieldateien sind schreibgeschützt, Projekte liegen als Ordner unter
  `ModConfig/komet/shaders`. Ein Projekt hält Kopien von Spielshadern, die die Originale ersetzen, solange es an ist.
  Auch die Survival- und Creative-Shader (galaxy, rift, machinegear) sind bearbeitbar.
- **Editor** mit Vorschlägen, Suche, Verlauf und letztem guten Stand; Probleme stehen je Renderer mit der Zeile da.
  **TEST** (Strg+S) baut sofort, **AUTO** baut bei jeder Änderung. Ein fehlerhafter Shader behält den letzten guten Stand.
- **Spiel läuft mit: Meine Version / Vanilla** wählt, was das Spiel zeichnet; die Wahl bleibt, wenn das Studio schließt.
- **Im Spiel erzwingen** zeichnet den offenen Shader, obwohl seine Bedingungen ihn verbergen: Polarlicht, Gottesstrahlen,
  SSAO, Bloom, FXAA, Wolken, Schatten und Partikel. Andere Shader werden immer gezeichnet.
- Die **3D-Vorschau** zeigt den Shader an einem Modell, mit Reglern für Klima und Jahreszeit.
- **Als Mod exportieren** schreibt das Projekt als Shader-Mod. `.komet shadertest` zeichnet jeden installierten Shader in
  der 3D-Ansicht und speichert Bilder und einen Bericht unter `Logs/komet-shadertest`.

Ein Modshader unter `assets/<deine-modid>/shaders/` bleibt ein eigener Pass und ersetzt von selbst keinen Spielshader.
Verweise ihn in deiner `komet.json`.

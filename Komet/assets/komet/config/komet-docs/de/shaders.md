# Shader-Referenz

Diese Seite ist zum Nachschlagen. Bist du neu, fang mit „Deine Mod für Komet“ an: Dort baust du Schritt für Schritt eine Mod aus Komets Vorlage. Was ein Shader braucht, damit er auch unter Komets Vulkan-Renderer läuft, steht in „Shader für Vulkan“. Wörter wie Pass, Define oder Uniform erklärt die Seite „Begriffe“ unter Hilfe.

## komet.json im Überblick

Die Datei liegt in deiner Mod unter `assets/<deine-modid>/config/komet.json`. Alle Felder sind freiwillig. Schon `{}` meldet deine Mod bei Komet an: Sie steht dann unter Shader › Aktive Shader in der Gruppe „Für Komet gemacht“.

| Feld | Was es tut |
|---|---|
| `$schema` | Lässt deinen Editor Felder vorschlagen und Fehler zeigen (Adresse unten) |
| `_comment` | Eine Notiz für Leser; Komet liest sie nicht |
| `name`, `author` | Titel deiner Seite unter Shader und Autor im Feld rechts unter Aktive Shader; ohne sie gilt deine `modinfo.json` |
| `settings` | Schalter, Regler und Auswahlen (siehe „Einstellungen“) |
| `programs` | Welche Shader deine Defines bekommen (siehe „Einstellungen“) |
| `passes` | Bild-Effekte über dem fertigen Bild (siehe „Passes“) |
| `renderer` | `"both"` (Standard) oder `"opengl"`: Bei `"opengl"` nimmt Komet OpenGL statt Vulkan, solange deine Mod läuft |
| `config` | Steuert das eigene Einstellungsfenster deiner Mod (siehe „Mods mit eigenem Fenster“) |
| `holds` | Komet-Funktionen, die zurückgehalten werden, solange deine Mod läuft (siehe dort) |

Die Schema-Adresse für `komet.json` ist `https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet.schema.json`. Für ein Pack zum Ausprobieren gilt dieselbe Adresse mit `komet-shader.schema.json` am Ende.

Eine eigene Seite unter Shader bekommt deine Mod, sobald `settings` oder `passes` mindestens einen Eintrag hat.

## Komets eigener Weg

- Mit Komet hat jeder Shader, den das Spiel lädt, das Define `KOMET`, unter Komets Vulkan-Renderer zusätzlich `KOMET_VULKAN`. Auch deine Passes bekommen beide. Ohne Komet fehlen sie: Mit `#ifdef KOMET` läuft derselbe Shader mit und ohne Komet.
- Läuft Komet, ersetzt `assets/<domain>/config/komet/shaders/<datei>` die Datei `assets/<domain>/shaders/<datei>`, ebenso unter `shaderincludes`. Das gilt auch für die Domain `game`. Die ersetzte Datei muss es geben, sonst steht eine Warnung im Log. Ohne Komet liest das Spiel diese Dateien nie.
- Im C#-Code sagt `api.ModLoader.IsModEnabled("komet")`, ob Komet läuft.

## Ohne eigene Mod

Ein Ordner `ModConfig/komet/shaders/<name>/` mit einer `komet-shader.json` wird zu einer eigenen Seite unter Shader. Die Felder sind dieselben wie in der `komet.json`, nur `renderer`, `config` und `holds` gibt es hier nicht. Pass-Dateien liegen im selben Ordner. Ohne `name` heißt die Seite wie der Ordner, in Kleinbuchstaben.

Ein fertiges Pack legt Modding › Vorlagen › **Beispiel-Pack anlegen** an. Es schreibt nach `ModConfig/komet/shaders/example` (ist der Name vergeben, nach `example-2` und so weiter) und lädt die Packs neu. Das Pack erscheint sofort als Seite „Beispiel-Pack“ unter Shader.

Hast du eine Datei geändert, drück Shader › Aktive Shader › **Packs neu laden**. Du musst die Welt dafür nicht verlassen.

## Das Beispiel-Pack

Das Beispiel-Pack besteht aus zwei Dateien: Die `komet-shader.json` beschreibt die Einstellungen, die Datei `tint.fsh` zeichnet. So sieht die erste aus (ohne die Zeile `_comment`):

```json
{
  "$schema": "https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet-shader.schema.json",
  "name": "komet:shaderpack-example",
  "author": "Komet",
  "programs": [
    "final"
  ],
  "settings": [
    {
      "type": "group",
      "label": "komet:shaderpack-example-look"
    },
    {
      "key": "tint",
      "label": "komet:shaderpack-example-tint",
      "hint": "komet:shaderpack-example-tint-hint",
      "type": "switch",
      "default": 0,
      "define": "EXAMPLE_TINT"
    },
    {
      "key": "strength",
      "label": "komet:shaderpack-example-strength",
      "hint": "komet:shaderpack-example-strength-hint",
      "type": "slider",
      "min": 0,
      "max": 1,
      "step": 0.05,
      "default": 0.6,
      "uniform": "exampleStrength"
    }
  ],
  "passes": [
    {
      "id": "tint",
      "fragment": "tint.fsh",
      "when": "tint"
    }
  ]
}
```

| Stelle | Was sie tut |
|---|---|
| `name`, `author` | Titel der Seite unter Shader und Autor im Feld rechts unter Aktive Shader. `komet:shaderpack-example` ist ein Sprachschlüssel, normaler Text geht auch |
| `programs` | Der Shader des Spiels, der das Define bekommt: hier `final`, das fertige Bild |
| `group` | Eine Überschrift auf der Seite |
| `tint` | Ein Schalter. Über `when` schaltet er den Pass an und aus. Außerdem setzt er `EXAMPLE_TINT` in `final`, darum baut jede Änderung die Shader neu |
| `strength` | Ein Regler von 0 bis 1. Er kommt als `uniform float exampleStrength` an und wirkt sofort |
| `hint` | Der Erklärtext, den das Menü zu einer Zeile zeigt |
| `passes` | Ein Bild-Effekt aus der Datei `tint.fsh`, gezeichnet über das fertige Bild |

So sieht `tint.fsh` aus:

```glsl
// Komet's example pass: the left half of the picture in red, the sky at half strength (depth 1 = sky).
// Komet puts the prelude in front: uv, color, scene, depth, resolution, time. exampleStrength is the pack's slider.
uniform float exampleStrength;

void main()
{
    vec3 c = texture(scene, uv).rgb;
    float sky = texture(depth, uv).r >= 1.0 ? 0.5 : 1.0;
    vec3 red = vec3(dot(c, vec3(0.3, 0.59, 0.11)) * 1.6, 0.0, 0.0);
    color = vec4(mix(c, red, exampleStrength * step(uv.x, 0.5) * sky), 1.0);
}
```

1. `uniform float exampleStrength` holt den Wert des Reglers.
2. `scene` ist das Bild bis hierher, `uv` die Stelle auf dem Bildschirm von 0 bis 1. Beides setzt Komet vor deine Datei.
3. `depth` ist die Tiefe, 1 heißt Himmel. Dort ist `sky` 0,5, sonst 1: Am Himmel wirkt der Effekt halb so stark.
4. `red` ist die Helligkeit des Bildes, nur im roten Kanal.
5. `color` ist, was der Pass zeichnet. `step(uv.x, 0.5)` ist links von der Mitte 1 und rechts davon 0. Links wird das Bild zum Rot hin gemischt, so stark wie der Regler sagt.

Eine ganze Mod mit Pass legt Modding › Vorlagen › **Mod-Vorlage anlegen** an. Wie du sie zu deiner machst, zeigt „Deine Mod für Komet“.

## Einstellungen

Jeder Eintrag in `settings` wird eine Zeile auf der Seite deines Packs.

| Feld | Bedeutung |
|---|---|
| `key` | Eindeutiger Name im Pack; unter ihm speichert Komet den Wert des Spielers |
| `label` | Der Name der Zeile im Menü |
| `hint` | Der Erklärtext zur Zeile |
| `type` | `switch` (Standard), `slider`, `choice` oder `group` |
| `default` | Der Startwert (Standard `0`) |
| `min`, `max`, `step` | Bereich und Schritt eines Reglers (Standard `0`, `1` und `0.1`) |
| `unit` | Die Einheit hinter dem Wert eines Reglers, zum Beispiel `EV` |
| `options` | Die Namen einer Auswahl |
| `define` | Wird `#define NAME wert`; eine Änderung baut die Shader neu |
| `uniform` | Wird `uniform float NAME`; wirkt sofort |

- `switch` liefert 0 oder 1, `choice` die Nummer des Eintrags in `options` ab 0, `slider` eine Zahl von `min` bis `max`.
- Als Define wird ein Schalter oder eine Auswahl zur ganzen Zahl, ein Regler zur Zahl mit Dezimalpunkt, zum Beispiel `0.6`.
- `group` ist nur eine Überschrift mit dem Text aus `label`.
- Ein Regler braucht `max` größer als `min` und `step` größer als 0, eine Auswahl braucht `options`. Sonst zeigt Komet die Zeile als Schalter.
- `name`, `label`, `hint` und die Namen in `options` dürfen Sprachschlüssel aus deinen Sprachdateien sein (`domain:schlüssel`) oder normaler Text.
- `programs` nennt die Shader, die die Defines bekommen: Programme des Spiels wie `final` oder `chunkopaque`, oder einen deiner Passes als `pass:<id>`. Ohne `programs` bekommen alle Shader die Defines, auch alle deine Passes.
- Uniforms erreichen jeden Shader und jeden Pass, der sie deklariert, egal was `programs` sagt.
- Ein Pack hat höchstens 40 Einstellungen. Die Werte der Spieler stehen in `ModConfig/komet/shader-settings.json`.

## Passes

Ein Pass ist eine Fragment-Datei, die Komet über das ganze Bild zeichnet. Er läuft, nachdem das Spiel sein Bild fertig gemischt hat (samt Bloom und Gottesstrahlen), und vor der Oberfläche.

| Feld | Bedeutung |
|---|---|
| `id` | Der Name des Passes, eindeutig in deiner Mod |
| `fragment` | Die Datei: in der `komet.json` ab `assets/<deine-modid>/`, zum Beispiel `shaders/grade.fsh`; in einem Pack zum Ausprobieren im selben Ordner |
| `when` | Der `key` eines Schalters: Der Pass zeichnet, solange er an ist |
| `order` | Die Reihenfolge über alle Passes aller Mods, kleinere Werte zuerst (Standard `0`) |

- Ein Pass liest eine Kopie des Bildes bis hierher (`scene`) und überschreibt jeden Bildpunkt mit `color`.
- Ein Pfad mit `..` oder ein absoluter Pfad ist nicht erlaubt.
- Alle Mods zusammen haben höchstens 32 Passes.

Diese Zeilen setzt Komet vor deine Datei. Du schreibst nur `main()` und setzt `color`. Deklarier diese Namen nicht noch einmal:

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

## Aus Code: KometShaders

Mit C# meldest du dasselbe und noch mehr über `KometShaders` an:

```csharp
var q = KometShaders.Define("mymod", "MY_QUALITY", () => quality.ToString(), "final");
var b = KometShaders.Uniform("mymod", "myBloom", () => bloom);
var e = KometShaders.Edit("mymod", "final", null, src => src.Replace("a", "b"));
var p = KometShaders.AddPass(new KometPass("mymod", "grade", KometPassStage.Final, source)
{
    Order = 10,
    Enabled = () => config.Grade,
    Uniforms = { ["myStrength"] = () => [config.Strength] }
});
KometShaders.Reload(); // after a define's value changed
```

- Ruf die API nur auf, wenn `api.ModLoader.IsModEnabled("komet")` wahr ist, und nur im Haupt-Thread.
- `Define`, `Uniform`, `Edit` und `AddPass` geben ein `IDisposable` zurück. `Dispose()` nimmt die Anmeldung zurück. Alle Anmeldungen enden mit der Welt: Melde sie in `StartClientSide` bei jedem Betreten einer Welt neu an.
- `Define` liest seinen Wert bei jedem Laden der Shader. Ändert er sich, ruf `Reload()` auf. Die Programme nennst du wie in `programs`.
- `Uniform` nimmt eine Zahl oder 1 bis 4 Zahlen (`float` bis `vec4`). Komet fragt den Wert einmal pro Bild ab.
- `Edit` schreibt den Quelltext eines Programms beim Laden um. `"*"` steht für jedes Programm. Komets eigene Änderungen kommen zuerst, such deshalb nicht nach ganzen Zeilen.
- `AddPass` zeichnet einen Pass wie aus `passes`. Komet fragt `Enabled` in jedem Bild ab. Wirft es einen Fehler, geht der Pass aus.
- `KometShaders.Renderer` sagt, welcher Renderer gerade zeichnet. `RendererChanged` meldet einen Wechsel. Die Shader baut Komet dabei selbst neu.
- Alle Mods zusammen haben höchstens 128 Defines, 256 Uniforms, 64 Änderungen und 32 Passes.

Eine eigene Einstellungsseite baust du mit `KometOptions` (Seite „API für Mods“).

## Mods aus der Datenbank

Shader-Mods, die Komet nicht kennen, musst du nicht anpassen. Komet findet sie beim Betreten einer Welt an ihren Shader-Dateien und zeigt sie unter Shader › Aktive Shader in der Gruppe „Weitere Shader-Mods“.

- Komet zeigt, ob jeder ihrer Shader unter OpenGL kompiliert. Unter Vulkan übersetzt Komet sie selbst, auch die, die noch nicht gezeichnet wurden, und zeigt, ob das geklappt hat. Ein rotes Ausrufezeichen hinter dem Namen heißt, dass einer nicht durchkam. Das Feld rechts nennt ihn und den Grund.
- Eine Mod, die Shader des Spiels oder einer anderen Mod ersetzt, ist erst einmal **aus**: Komet startet sie nicht, und das Spiel nutzt seine eigenen Dateien. Ein Hinweis im Chat sagt das beim Betreten der Welt. Mit ihrem Schalter startest du sie sofort.
- Schaltest du so eine Mod aus, pausiert sie sofort: Ihre Effekte verschwinden, das Spiel zeichnet wieder mit seinen eigenen Shadern. Schaltest du sie wieder ein, läuft sie weiter, wo sie war. Ab dem nächsten Betreten einer Welt bleibt sie ganz aus. Das gilt auch für Mods, die nur aus Shader-Dateien bestehen.
- Fehlt eine Mod, von der sie abhängt, oder ist diese in einer anderen Hauptversion geladen, startet Komet sie nicht, und ihr Steckbrief sagt, warum.

Eine Mod mit `komet.json` steht stattdessen unter „Für Komet gemacht“ und läuft, bis du sie ausschaltest. Dann gehen ihre Einstellungen und Passes sofort aus, ebenso das, was ihr Code über `KometShaders` angemeldet hat. Ihr Code selbst läuft bis zum nächsten Betreten einer Welt, außer sie ersetzt Shader des Spiels: Dann pausiert sie sofort wie oben. Eine Mod aus der Datenbank bekommt eigene Regler erst mit einer `komet.json` (siehe „Deine Mod für Komet“).

## Was die Symbole bedeuten

Hinter jedem Namen unter Aktive Shader stehen kleine Symbole. Das erste, farbige Symbol zeigt, wer wen steuert:

| Symbol | Bedeutung |
|---|---|
| Kettenglied auf Grün | **Eingebunden**: Die Mod meldet sich selbst an, per `komet.json`, als Pack oder über die C#-Schnittstelle |
| Komet auf Blau | **Von Komet gesteuert**: Die Mod weiß nichts von Komet; Komet steuert sie über eine mitgelieferte Anbindung |
| Auge auf Grau | **Nicht eingebunden**: Komet erkennt die Mod an ihren Dateien und steuert nichts |

Das zweite, farbige Symbol zeigt, wo die Mod gerade läuft. Bei einer zurückgehaltenen Mod fehlt es:

| Symbol | Bedeutung |
|---|---|
| Vulkan (Berg) auf Rot | **Vulkan**: läuft auf Komets Vulkan-Renderer, alle Shader übersetzt |
| Vulkan (Berg) auf Orange | **Vulkan, lückenhaft**: Nicht jeder Shader ließ sich übersetzen |
| Würfel auf Türkis | **OpenGL**: läuft auf OpenGL; zeichnet die Mod nur mit OpenGL, sagt das ihr Steckbrief |

Die grauen Symbole danach nennen Einzelheiten:

| Symbol | Bedeutung |
|---|---|
| Blatt mit Zeilen | **komet.json**: angemeldet über ihre `komet.json` |
| Ordner | **Eigenes Pack**: ein Shader-Pack aus deinem Ordner |
| `< >` | **C#-Schnittstelle**: angemeldet über `KometShaders` |
| Zwei Blätter | **Komet-Fassung**: bringt eigene Dateien für Komet mit (`config/komet/`) |
| Zwei Balken | **Pausiert**: Komet hält die Mod zurück; sie läuft gerade nicht |
| Rotes Ausrufezeichen | **Problem**: Ein Shader kompiliert nicht oder ließ sich nicht übersetzen, oder eine Abhängigkeit fehlt oder passt nicht |

Ist keine Zeile ausgewählt, zeigt das Feld rechts diese Legende. Wählst du eine Mod aus, steht dort ihr Steckbrief: wie sie eingebunden ist, wo sie läuft, wie viele Shader kompilieren, was sie ersetzt und ob eine Abhängigkeit fehlt oder nicht passt. Ein Dreieck steht dort vor ihren Shadern, zwei Pfeile vor dem, was sie ersetzt.

## Mods mit eigenem Fenster

Manche Shader-Mods haben ein eigenes Einstellungsfenster, SheyderMod zum Beispiel. Komet kann ihre Einstellungsdatei an Komets Voreinstellung koppeln. Das steuert der Block `"config"` in ihrer `komet.json`:

| Feld | Bedeutung |
|---|---|
| `file` | Die Einstellungsdatei der Mod in `ModConfig` |
| `reload` | Der Befehl, mit dem die Mod die Datei neu liest |
| `levels` | Für `quality`, `balanced` und `speed`: `from` nennt eine Profildatei aus `assets/<modid>/config/`, `set` ändert Werte darin |
| `switches` | Schalter für einzelne Werte: `key` ist ein Pfad mit Punkten wie `Fog.Enabled`, dazu `label` und `hint` |

```json
{
  "config": {
    "file": "mymod.json",
    "reload": ".mymod reload",
    "levels": {
      "quality": { "from": "default.json" },
      "speed": { "from": "fast.json", "set": { "Fog.Enabled": false } }
    },
    "switches": [ { "key": "Fog.Enabled", "label": "mymod:fog" } ]
  },
  "holds": [ { "feature": "FrustumSweep", "reason": "Our shadows need the game's own culling" } ]
}
```

Läuft so eine Mod, hat sie eine eigene Seite unter Shader:

- **Stufe**: „Wie Komet“ folgt Komets Voreinstellung (Komet › Übersicht › **Einstellungen**). Steht diese auf „Automatisch (empfohlen)“, zählt das als Ausgewogen, auf schwachen Rechnern als Tempo. „Qualität“, „Ausgewogen“ und „Tempo“ legen die Stufe fest. „Eigene“ lässt die Datei der Mod in Ruhe, dann gilt ihr eigenes Fenster.
- **Effekte**: die Schalter aus `switches`. Sie zeigen den Wert, den Komet zuletzt geschrieben hat. Bei „Eigene“ sind sie gesperrt. Eine neue Stufe setzt sie zurück.

Komet baut die Datei neu, wenn du die Welt betrittst, wenn du auf der Seite etwas änderst und, bei „Wie Komet“, wenn sich die Voreinstellung ändert. Dazu nimmt es das Profil aus `from` (ohne `from` die bisherige Datei), setzt `set` darüber und darüber deine Schalter. Es schreibt die Datei nach `ModConfig/<file>` und ruft den Befehl aus `reload` auf. Die Mod übernimmt sie dann sofort. Fehlt in `levels` die Stufe, schreibt Komet nichts. Vor dem ersten Überschreiben legt Komet eine Kopie mit der Endung `.before-komet` an, zum Beispiel `sheydermod.json.before-komet`.

`"holds"` hält Komet-Funktionen zurück, solange die Mod läuft: `feature` ist der Name einer Komet-Funktion wie bei `KometFeatures.HoldOff` (siehe „API für Mods“), `reason` sagt, warum. `"renderer": "opengl"` hält auf dieselbe Weise Komets Vulkan-Renderer zurück.

Für Mods ohne eigene `komet.json` geht dasselbe mit einer Anbindungsdatei unter `assets/<domain>/config/komet-adapters/<name>.json` in einer beliebigen Mod. Dort heißen die Felder `mod` (die ID der Mod), `config` (statt `file`), `reload`, `levels`, `settings` (statt `switches`) und `holds`. Komet bringt so eine Datei für SheyderMod mit. Sie hält den Vulkan-Renderer zurück, weil SheyderMod in OpenGL-Framebuffer zeichnet, die Komets Vulkan-Renderer nicht zeigen kann.

## Wenn etwas nicht klappt

Fast jedes Problem steht im `client-main.log`. Such nach diesen Zeilen:

| Zeile im Log | Bedeutung |
|---|---|
| `Komet shaders: ... is not a valid komet-shader.json` | Ein JSON-Fehler mit Zeile und Stelle, auch in einer `komet.json` |
| `Komet shaders: pack ... names the pass file ...` | Die Datei aus `fragment` fehlt, oder ihr Pfad ist nicht erlaubt |
| `Komet shaders: pass ... is left out` | Der Pass kompiliert oder linkt nicht, oder sein `Enabled` wirft. Der Grund steht dahinter; die Zeilennummer zählt ab deiner Datei |
| `Komet shaders: ... which is no GLSL name` | Ein `define` oder `uniform` ist kein gültiger GLSL-Name (höchstens 64 Zeichen) |
| `Komet: Vulkan cannot draw` | Etwas fehlt nur unter Vulkan (siehe „Shader für Vulkan“) |

- Kompiliert ein Shader nicht, den das Spiel lädt, legt Komet seinen ganzen Quelltext unter `Logs/komet-shader-<datei>.txt` ab. Die Zeilennummer aus dem Fehler passt genau zu dieser Datei.
- Zeigt das Menü einen Regler als Schalter, prüf `min`, `max` und `step`. Eine Auswahl ohne `options` wird auch zum Schalter.
- Komet ergänzt die Include-Dateien `shadowcoords.vsh` und `fogandlight.fsh` um **Schatten in der Ferne** (Werte mit `kometDistant` im Namen). Ersetzt deine Mod diese Dateien und erkennt Komet ihren Text nicht wieder, bleiben die fernen Schatten aus. Das Log sagt dann `Komet: the shadow shaders are not the engine's, distant shadows stay off`.

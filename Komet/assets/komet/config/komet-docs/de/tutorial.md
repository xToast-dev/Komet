# Deine Mod für Komet

Diese Seite führt dich in sieben Schritten zu einer Shader-Mod, die mit Komet zusammenarbeitet. Am Ende steht deine Mod in Komets Shader-Liste, hat eigene Regler und geht mit Komet einen eigenen Weg. Ohne Komet läuft sie wie bisher. Du brauchst kein C#.

## Schritt 1: Vorlage anlegen

Am schnellsten startest du mit Komets fertiger Vorlage:

1. Drücke Modding › Vorlagen › **Mod-Vorlage anlegen**. Komet legt den Ordner `ModConfig/komet/mod-template/mymod` an und öffnet ihn.
2. Kopiere `mymod` in deinen Mods-Ordner (`VintagestoryData/Mods`). Der Knopf **Mods-Ordner** auf derselben Seite öffnet ihn.
3. Betritt eine Welt. Bist du schon in einer, verlass sie und betritt sie neu. Unter Shader › Aktive Shader steht jetzt **My Komet Mod** bei „Für Komet gemacht“, und unter Shader hat sie eine eigene Seite mit einem Schalter und einem Regler. Das Bild wird warm.

Hast du schon eine Mod, kannst du dir die Vorlage auch nur ansehen und ihre Teile in deine Mod übernehmen.

## Schritt 2: Was drinsteckt

```text
mymod/
  README.md                          a short guide to this folder
  modinfo.json                       name, id and version, as in every mod
  assets/mymod/
    config/komet.json                the one file Komet reads
    config/komet/shaders/README.txt  Komet versions of your shader files go here
    shaders/warm.fsh                 the picture effect Komet draws
```

Alles für Komet steht in `config/komet.json`: eine Gruppe „Look“, der Schalter `warm`, der Regler `warmth` und ein Pass, der `shaders/warm.fsh` über das fertige Bild zeichnet, solange der Schalter an ist. Die Datei liegt im Ordner `config`, den das Spiel ohnehin lädt. Ohne Komet macht sie nichts; deine Mod braucht Komet also nicht als Abhängigkeit.

## Schritt 3: Deine Kennung

Ersetze `mymod` überall durch die Kennung deiner Mod, zum Beispiel `sunnyday`:

1. In `modinfo.json` bei `modid`. Ändere dort auch `name` und `authors`.
2. Im Ordnernamen: Aus `assets/mymod` wird `assets/sunnyday`. Der Ordner muss genau wie `modid` heißen, denn über diesen Namen ordnet Komet die `komet.json` deiner Mod zu.
3. In der Uniform `mymodWarmth`, in `komet.json` und in `warm.fsh`. Ein eigenes Präfix, also ein Namensanfang wie deine Kennung, hält deine Namen von denen anderer Mods getrennt.

Name und Autor nimmt Komet aus deiner `modinfo.json`, außer wenn `komet.json` eigene nennt (`name`, `author`).

## Schritt 4: Regler anlegen

Jeder Eintrag unter `settings` wird zu einer Zeile auf deiner Seite. `"type"` ist `switch` (Schalter), `slider` (Regler), `choice` (Auswahl) oder `group` (eine Überschrift). Ein Eintrag gibt seinen Wert auf einem von zwei Wegen an deine Shader weiter:

| Weg | So steht es im Shader | Wann es wirkt |
|---|---|---|
| `"define": "SUNNY_RAYS"` | `#if SUNNY_RAYS` | nach einem kurzen Neuladen der Shader |
| `"uniform": "sunnyStrength"` | `uniform float sunnyStrength;` | sofort |

```json
{
  "settings": [
    { "key": "rays", "label": "Sun rays", "type": "switch", "default": 1, "define": "SUNNY_RAYS" },
    { "key": "strength", "label": "Strength", "type": "slider", "min": 0, "max": 2, "step": 0.1,
      "default": 1, "uniform": "sunnyStrength" }
  ]
}
```

Ein Define bekommt bei einem Schalter 0 oder 1, bei einer Auswahl die Nummer der Option, gezählt ab 0. Es erreicht die Shader, die `programs` nennt; ohne `programs` alle Shader und alle deine Passes. Einen Pass nennst du in `programs` als `pass:<id>`. Eine Uniform erreicht jeden Shader und jeden Pass, der sie deklariert. Texte wie `label` und `hint` dürfen Sprachschlüssel sein, etwa `sunnyday:strength`.

Ohne Komet gibt es das Define nicht. Gib ihm deshalb im Shader einen Standardwert, dann läuft deine Mod auch ohne Komet:

```glsl
#ifndef SUNNY_RAYS
#define SUNNY_RAYS 1
#endif
```

## Schritt 5: Eigener Weg

Läuft Komet, kann deine Mod den normalen Weg des Spiels verlassen und eine Fassung nehmen, die du für Komet abgestimmt hast. Nimm, was zu deiner Mod passt:

**Kleiner Unterschied:** Komet setzt in jedem Shader und jedem Pass das Define `KOMET`, unter seinem Vulkan-Renderer zusätzlich `KOMET_VULKAN`.

```glsl
#ifdef KOMET
    // the path tuned for Komet
#else
    // the game alone
#endif
```

**Ganze Datei anders:** Leg die Komet-Fassung mit demselben Pfad unter `config/komet/` ab. Das geht für `shaders` und `shaderincludes`. Mit Komet tritt sie an die Stelle der normalen Datei; ohne Komet sieht das Spiel sie nie. Die normale Datei muss es trotzdem geben, sonst meldet das Log, dass die Komet-Fassung nichts zu ersetzen hat.

| Normale Datei | Komet-Fassung |
|---|---|
| `assets/sunnyday/shaders/sky.fsh` | `assets/sunnyday/config/komet/shaders/sky.fsh` |
| `assets/game/shaders/final.fsh` | `assets/game/config/komet/shaders/final.fsh` |

**Bild-Effekt:** Ein Eintrag unter `passes` zeichnet eine eigene Datei über das fertige Bild, so wie `warm.fsh`. Das geht nur mit Komet und läuft unter OpenGL und Vulkan. Was Komet vor deine Pass-Datei setzt (`uv`, `color`, `scene`, `depth`, `resolution`, `time`), zeigt die Seite „Shader-Referenz“.

## Schritt 6: Ausprobieren

1. Ändere `komet.json` oder einen Shader und speichere.
2. Drücke Shader › Aktive Shader › **Packs neu laden**. Du musst die Welt nicht verlassen.
3. Unter Shader › Aktive Shader steht bei deiner Mod, ob jeder Shader kompiliert und, unter Vulkan, ob Komet ihn übersetzen konnte. Ein rotes Ausrufezeichen heißt, dass etwas nicht klappt, zum Beispiel ein Shader. Das Detail nennt, was es ist. Ein Pass, der nicht kompiliert, bekommt kein Zeichen: Das Detail zeigt ihn mit „ist aus“ und dem Grund. Den genauen Fehler nennt das Log.
4. Schalte einmal den Renderer um (Komet › Übersicht › **Renderer**) und schau, ob deine Mod unter OpenGL und unter Vulkan gleich aussieht. Was unter Vulkan anders sein kann, erklärt die Seite „Shader für Vulkan“.

Fehler stehen in `client-main.log`, in Zeilen mit `Komet shaders:` oder `Shader compile error`. Kompiliert ein Shader nicht, legt Komet seinen vollständigen Quelltext einmal pro Sitzung unter `Logs/komet-shader-<datei>.txt` ab; die Zeilennummern im Fehler beziehen sich auf diese Datei. Bei einem Pass beziehen sie sich direkt auf deine Datei.

## Schritt 7: Veröffentlichen

Prüfe vor dem Hochladen kurz:

- `komet.json` liegt unter `assets/<deine-id>/config/` und ist gültiges JSON. Mit der Zeile `"$schema"` aus der Vorlage zeigt dir ein Editor wie VS Code Fehler sofort.
- Jedes Define hat im Shader einen Standardwert für Spieler ohne Komet.
- Deine Uniforms und Defines tragen dein eigenes Präfix.
- Zeichnet deine Mod nur mit OpenGL, steht `"renderer": "opengl"` in `komet.json` (Standard ist `"both"`). Komet zeichnet dann mit OpenGL, solange deine Mod läuft; die Zeile **Renderer** ist gesperrt und nennt deine Mod.
- Deine Mod ist getestet: ohne Komet und mit Komet unter beiden Renderern.

## Häufige Fehler

| Was passiert | Woran es liegt |
|---|---|
| Deine Mod fehlt bei „Für Komet gemacht“ | Die Datei liegt nicht genau unter `assets/<id>/config/komet.json` |
| Deine Mod hat keine eigene Seite | `komet.json` ist kein gültiges JSON (das Log nennt die Zeile) oder hat weder `settings` noch `passes` |
| Ein Regler ändert nichts | Der Name im Shader ist anders geschrieben, oder bei einem Define fehlt der Shader in `programs` |
| Ein Pass zeichnet nie | `when` nennt nicht den `key` eines Schalters, der Schalter ist aus oder die Datei aus `fragment` fehlt (das Log sagt es) |
| Ohne Komet kompiliert der Shader nicht | Ein Define ohne Standardwert im Shader |
| Ein Pass kompiliert nicht | Er deklariert `uv`, `color`, `scene`, `depth`, `resolution` oder `time` noch einmal |
| Unter Vulkan fehlt dein Effekt | Deine Mod zeichnet in eigene OpenGL-Framebuffer. Nimm einen Pass oder setze `"renderer": "opengl"` |

## Weiter

- „Shader-Referenz“: alle Felder von `komet.json`, Passes und die Code-API `KometShaders`.
- „Shader für Vulkan“: die Regeln für Shader, die unter Vulkan sauber laufen, und die Vulkan-Vorlage.
- „API für Mods“: eigene Seiten, Funktionen und Stile aus C#.

# Einstieg

Hier fängst du an, wenn du Komet erweitern oder deine Mod mit Komet verbinden willst. Für das meiste brauchst du kein C#: Eine JSON-Datei und deine Shader-Dateien reichen. Wörter wie Define, Uniform oder Pass erklärt die Seite „Begriffe“ unter Hilfe.

## Was willst du machen?

| Du willst | Lies | Code nötig |
|---|---|---|
| Ein Beispiel anlegen und daran lernen | „Vorlagen“ | Nein |
| Deine Shader-Mod mit Komet verbinden | „Deine Mod für Komet“ | Nein |
| Jedes Feld von `komet.json` nachschlagen | „Shader-Referenz“ | Nein |
| Shader schreiben, die auch unter Vulkan laufen | „Shader für Vulkan“ | Nein |
| Das Aussehen dieses Menüs ändern | „Menü-Stile“ | Nein |
| Eigene Seiten, Funktionen oder Shader aus C# | „API für Mods“ | Ja, C# |
| Verstehen, wie Komet mit Vulkan zeichnet | „Vulkan erklärt“ unter Hilfe | Nein |

## Fertige Vorlagen

Jede Vorlage legst du mit einem Knopf unter Modding › Vorlagen an. Vorher bleiben deine Ordner leer. Ist ein Name schon vergeben, hängt Komet eine Zahl an, etwa `example-2`.

- **Beispiel-Pack anlegen**: ein kleines Shader-Pack in `ModConfig/komet/shaders/example`, ganz ohne eigene Mod. Es steht sofort als eigene Seite unter Shader.
- **Beispiel-Stil anlegen**: ein Menü-Stil mit bernsteinfarbener Akzentfarbe in `ModConfig/komet/styles/example`. Komet wählt ihn sofort aus.
- **Mod-Vorlage anlegen**: eine kleine Shader-Mod für Komet in `ModConfig/komet/mod-template/mymod`. Komet öffnet den Ordner, und du kopierst ihn nach `VintagestoryData/Mods`. Die Seite „Deine Mod für Komet“ geht sie Schritt für Schritt durch.
- **Vulkan-Vorlage anlegen**: ein vollständiges Shader-Paket für Entwickler in `ModConfig/komet/mod-template/myshaders-dev`, mit zwei Passes, Reglern, einem Define, Texten in zwei Sprachen und einem optionalen C#-Teil. Die `README.md` im Ordner erklärt jede Datei, die Seite „Shader für Vulkan“ die Regeln dahinter.

Die Knöpfe **Vorlagen-Ordner** und **Mods-Ordner** öffnen die beiden Ordner, zwischen denen du kopierst.

## Eine Datei: komet.json

Deine Mod meldet sich mit einer einzigen Datei bei Komet an: `assets/<modid>/config/komet.json`. Darin stehen deine Regler, deine Bild-Effekte (Passes) und ob deine Mod unter Vulkan läuft. Das Spiel lädt den Ordner `config` ohnehin, macht aber nichts mit der Datei. Ohne Komet bleibt sie einfach liegen, und deine Mod braucht Komet nicht als Abhängigkeit.

```flow
Deine Mod bringt eine komet.json mit
Komet liest sie beim Betreten einer Welt
Sie steht bei „Für Komet gemacht“
Spieler stellen deine Regler ein
Komet gibt die Werte an deine Shader
```

„Für Komet gemacht“ ist eine Gruppe unter Shader › Aktive Shader. Dazu bekommt deine Mod unter Shader eine eigene Seite mit ihren Reglern. Wie die Datei aussieht, zeigt die Seite „Deine Mod für Komet“, jedes Feld die Seite „Shader-Referenz“.

## Wo die Dateien liegen

- Deine Mod für Komet: `assets/<modid>/config/komet.json`
- Komet-Fassungen deiner Shader: `assets/<modid>/config/komet/shaders/`
- Shader-Pack ohne Mod: `ModConfig/komet/shaders/<name>/komet-shader.json`
- Menü-Stil ohne Mod: `ModConfig/komet/styles/<id>/style.json`
- Menü-Stil in einer Mod: `assets/<modid>/config/komet-styles/<id>/style.json`
- Eigenes Logo: `ModConfig/komet/logo.png`
- Vorlagen: `ModConfig/komet/mod-template/`

`ModConfig` und `Mods` liegen beide in `VintagestoryData`. Was in `ModConfig` liegt, gilt nur auf deinem Rechner. Was in einer Mod liegt, bekommt jeder Spieler, der die Mod hat.

## Mit Code

Für alles, was eine JSON-Datei nicht kann, hat Komet eine kleine öffentliche API im Namespace `Komet.Api`. Fünf Klassen sind der Einstieg, jede mit einer eigenen Aufgabe:

| Klasse | Was sie tut |
|---|---|
| `KometOptions` | Fügt eigene Seiten in Komets Optionsmenü ein |
| `KometFeatures` | Liest den Zustand von Komets Funktionen, hält eine zurück oder meldet eigene an |
| `KometShaders` | Defines, Uniforms, Änderungen am Quelltext und Vollbild-Passes für Shader |
| `KometStyles` | Meldet einen Menü-Stil aus Code an |
| `KometDebug` | Schreibt eigene Zeitmessungen ins Debug-Protokoll |

Die Seite „API für Mods“ zeigt jede Klasse mit Beispielen.

## Bevor du loslegst

Zwei Regeln gelten für jeden Code, der Komet nutzt. Beide stehen mit Beispielen auf „API für Mods“:

- **Komet bleibt optional**: Deine Mod lädt auch ohne Komet, wenn nur eine eigene Klasse Komets Typen nennt und du sie erst aufrufst, wenn `api.ModLoader.IsModEnabled("komet")` wahr ist. Die Vulkan-Vorlage zeigt das Muster vollständig in ihrem Ordner `code/`.
- **Alles endet mit der Welt**: Seiten, Funktionen, Zurückhaltungen, Shader-Anmeldungen, Stile und Event-Abos enden, wenn die Welt schließt. Melde sie im Haupt-Thread in `StartClientSide` an; das läuft ohnehin jedes Mal, wenn du eine Welt betrittst.

## Mit anderen Mods

Komet ändert den Code des Spiels mit Harmony-Patches, wie viele Mods. Damit es keinen Streit gibt:

- **Komet tritt zurück**: Patcht eine andere Mod eine Methode, auf die eine Komet-Funktion baut, schaltet Komet diese Funktion ab (Zustand `StoodDown`). Das Spiel macht die Arbeit dann wie gewohnt. Log und HUD sagen es dir.
- **Fingerabdrücke**: Eine Funktion, die Code des Spiels ersetzt, prüft vorher einen Fingerabdruck dieses Codes. Hat ein Spiel-Update diesen Code geändert, bleibt die Funktion aus (Zustand `EngineChanged`), statt etwas kaputt zu machen.
- **Eine Funktion zurückhalten**: Verträgt sich deine Mod nicht mit einer von Komets Funktionen, bitte Komet, sie beim Verhalten des Spiels zu lassen: ohne C# mit `holds` in `komet.json` (siehe „Shader-Referenz“), mit C# über `KometFeatures.HoldOff`. Die Einstellung des Spielers bleibt gespeichert; die Zeile der Funktion im Menü ist gesperrt und nennt deine Mod und den Grund.
- **Eigene Funktionen**: Mit `KometFeatures.Register` meldest du eigene Funktionen an. Komet zeigt ihren Schalter und schützt sie nach denselben Regeln: Zurückhalten, Zurücktreten und Fingerabdruck. Wie das geht, steht auf „API für Mods“.

Jedes Debug-Protokoll legt in seinem Ordner unter `Logs/komet-debug/` auch eine `harmony.txt` ab: eine Liste, welche Mod welche Methode patcht. Sie hilft, wenn sich zwei Mods in die Quere kommen.

## Wo du mehr liest

- „Vorlagen“: alle Beispiele zum Anlegen und die beiden Ordner.
- „Deine Mod für Komet“: Schritt für Schritt von der Vorlage bis zum Veröffentlichen.
- „Shader-Referenz“: jedes Feld von `komet.json`, Shader-Packs, Passes und die Fehlersuche.
- „Shader für Vulkan“: wie du Shader schreibst, die Komet sauber für Vulkan übersetzt.
- „Menü-Stile“: jeder Schlüssel von `style.json`, Schriften, Logo und Hintergrund-Shader.
- „API für Mods“: jede Klasse aus der Tabelle oben, mit Code.
- „Vulkan erklärt“ unter Hilfe: wie Komets Vulkan-Renderer zeichnet.

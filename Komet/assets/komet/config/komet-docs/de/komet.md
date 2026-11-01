# Komet

Komet ist eine Mod für mehr Leistung in Vintage Story und läuft auf deinem Rechner: Ein Server braucht sie nicht. Im Einzelspieler hilft Komet auch dem eingebauten Server, der deine Welt im Hintergrund verwaltet.

Das Ziel ist ruhiges Spielen: weniger Ruckler und bessere 1-%-Lows, nicht nur mehr FPS im Schnitt. Die 1-%-Lows zeigen, wie flüssig die langsamsten Momente laufen (mehr dazu unter „Begriffe“). Dafür ersetzt Komet aufwendige Teile des Spiels durch Code, der dasselbe Bild liefert, nur schneller oder sparsamer.

## Was Komet macht

- Zeichnet die Welt mit weniger Arbeit für Prozessor und Grafikkarte: schnellere Sichtprüfung, weniger Warten auf den Grafiktreiber, schlankere Shader.
- Baut und lädt Chunks schneller, die nächsten zuerst, auf eigenen Hilfs-Threads.
- Erzeugt weniger Speichermüll, damit das Spiel seltener zum Aufräumen anhält.
- Behebt nebenbei Fehler des Spiels: schwarze Chunks nach neu berechnetem Licht (im Einzelspieler), Licht von Fackeln in der Hand, das beim Laufen mitschwimmt, und Kreaturen und Spieler, die hinter einer Glasscheibe in der Hand verschwinden.
- Bringt Extras mit: Schatten bis zur Sichtweite, einen experimentellen Vulkan-Renderer und ein HUD, das misst, wo die Zeit bleibt.
- Ersetzt das Einstellungsmenü des Spiels durch ein eigenes Optionsmenü mit Suche, Voreinstellungen und dieser Hilfe.

## Erste Schritte

1. Öffne das Menü mit Esc › **Einstellungen** oder mit dem Chatbefehl `.komet`.
2. Schau dir unter Komet › Übersicht die Voreinstellung an. Ab Werk steht sie auf **Automatisch (empfohlen)**. Das passt zu den meisten Rechnern.
3. Wenn du magst, wähl auf derselben Seite den **Renderer**. **OpenGL** ist die sichere Wahl. Was **Vulkan (experimentell)** kann, steht unter „Vulkan erklärt“.
4. Übernimm deine Wahl mit **Enter**.
5. Drück im Spiel **F7**: Das Overlay zeigt FPS, 1-%-Lows und Frametime. Mehr dazu unter „HUD und Messen“.

## Wenn Komet zurücktritt

Ändert eine andere Mod dieselbe Stelle im Code des Spiels wie Komet oder hat ein Spiel-Update diese Stelle geändert, lässt Komet diesen Teil aus. Das Spiel arbeitet dort wie gewohnt, nur ohne Komets Beschleunigung. HUD und Log sagen dir, welcher Teil das ist. Mehr dazu unter „Probleme lösen“.

## Wo du was findest

| Seite | Was du dort findest |
|---|---|
| „Das Optionsmenü“ | Abschnitte, Voreinstellungen, Tasten, Werkzeuge, wo deine Einstellungen liegen |
| „HUD und Messen“ | Overlay, HUD-Fenster und Debug-Fenster, was die Zahlen bedeuten |
| „Vulkan erklärt“ | was der Vulkan-Renderer ist und wann er sich lohnt |
| „Probleme lösen“ | was du bei Rucklern oder Bildfehlern tust, wie du einen Fehler meldest |
| „Begriffe“ | Fachwörter wie Chunk, Shader oder Frametime, einfach erklärt |
| Abschnitt Modding | für Mod-Autoren und Designer: Vorlagen, Shader, Menü-Stile, API |

## Updates und Prüfsumme

Wenn du zum ersten Mal eine Welt betrittst, fragt Komet, ob es nach Updates schauen darf. Sagst du ja, fragt Komet danach bei jedem Betreten einer Welt einmal bei GitHub (`api.github.com`) nach, ob es eine neuere Version gibt. Dabei prüft es auch, ob deine installierte Datei genau der veröffentlichten entspricht (sha256-Prüfsumme). Gibt es eine neuere Version oder weicht deine Datei ab, zeigt das HUD-Fenster (**Strg+F7**) einen Hinweis.

- Komet › Werkzeuge › **Update-Hinweise** schaltet das jederzeit an oder aus.
- Komet › Werkzeuge › **Prüfsumme** zeigt im HUD-Fenster deine Version, die neueste auf GitHub und ob deine Datei passt. Dafür fragt Komet einmal bei GitHub nach, auch wenn die Update-Hinweise aus sind.

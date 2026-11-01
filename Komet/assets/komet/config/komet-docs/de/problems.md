# Probleme lösen

Diese Seite hilft, wenn etwas nicht stimmt. Zuerst kommen häufige Probleme und was dagegen hilft. Dann erklärt sie, was es heißt, wenn Komet zur Seite tritt, und zuletzt, wie du ein Problem meldest. Wörter wie Spike oder GC erklärt die Seite „Begriffe“.

## Schnelle Hilfe

### Bild und Welt

| Was du siehst | Was hilft |
|---|---|
| Gelände fehlt oder hat Löcher | Klick auf Komet › Werkzeuge › **Gelände neu aufbauen**. Du bleibst dabei in der Welt. Nach ein paar Sekunden ist das Gelände wieder da. |
| Etwas sieht nur unter Vulkan falsch aus oder fehlt | Stell Komet › Übersicht › **Renderer** auf **OpenGL** und vergleiche. Mehr auf der Seite „Vulkan erklärt“. |
| Komet zeigt als Renderer „OpenGL (Vulkan ausgefallen, siehe Log)“ | Vulkan hatte einen Fehler, und OpenGL zeichnet weiter. Melde es mit einem Debug-Protokoll und `client-main.log`. |
| Eine Shader-Mod wirkt nicht | Komet hält Mods zurück, die Shader des Spiels ersetzen und nicht für Komet gemacht sind. Schalte sie unter Shader › Aktive Shader ein. Der Chat nennt sie, wenn du die Welt betrittst. |

### Ruckler und Tempo

| Was du siehst | Was hilft |
|---|---|
| Kurze Ruckler | Drück F7. Bei Rucklern ab dem Wert von Komet › HUD › **Ruckler-Hinweis ab** (Standard 50 ms) nennt eine Karte die Ursache. Mehr auf der Seite „HUD und Messen“. |
| Drehen wirkt trotz hoher FPS unruhig | Prüf, ob unter Komet › Rendern **Gleichmäßige FPS-Begrenzung** und **Gleichmäßige Bildzeit ohne Grenze** an sind (ab Werk sind sie es). Die erste wirkt mit einer FPS-Begrenzung, die zweite ohne. Beide wirken nur ohne VSync. |
| Sekundenlange Stopps, vor allem bei großer Sichtweite | Prüf, ob Komet › Sonstiges › **Aufräumen im Hintergrund** an ist. |
| Die Bildrate sinkt, während Chunks laden | Senk Komet › Chunks › **Priorität: Chunks laden**. Bei 0 % läuft das Spiel am flüssigsten, dafür laden Chunks langsamer. |
| Das HUD meldet vollen Grafikspeicher | Senk Vintage Story › Allgemein › **Sichtweite**. |
| Eine Mod kostet viel Zeit | Öffne mit Strg+F7 das HUD-Fenster. Die Übersicht nennt die Mod mit ihrer Zeit; **Aufnehmen** startet eine Aufnahme von 10 s. Im Protokoll steht unter „Render- und Eventzeit pro Mod“, welche Aufrufe der Mod die Zeit kosten. |

### Menü und Einstellungen

| Was du siehst | Was hilft |
|---|---|
| Ein Schalter ist gesperrt | Eine andere Mod braucht dort das Verhalten des Spiels. Siehe „Komet tritt zur Seite“ weiter unten. |
| Du hast einen Komet-Schalter im Verdacht | Prüf ihn wie unter „Einen Schalter prüfen“ weiter unten. |
| Komets Einstellungen sind plötzlich zurückgesetzt | `ModConfig/komet.json` war nicht lesbar, oft nach einer Änderung von Hand. Komet verwendet dann die Standardwerte und hebt die alte Datei als `komet.json.broken` auf. |
| Du hast zu viel verstellt | Klick auf Komet › Übersicht › **Werte dieses Rechners** oder setz Komet › Werkzeuge › **Anzeige, Panels und Funktionen** zurück (zweimal klicken). |
| Das HUD meldet „Die installierte Datei weicht von der veröffentlichten Prüfsumme ab“ | Deine Komet-Datei ist nicht die veröffentlichte. Lade Komet noch einmal herunter. Komet › Werkzeuge › **Prüfsumme** zeigt beide Prüfsummen. |

## Komet tritt zur Seite

Komet besteht aus vielen Funktionen (siehe „Begriffe“). Jede prüft, ob sie sicher arbeiten kann. Wenn nicht, tritt sie zur Seite: Das Spiel macht diese Arbeit wieder selbst, so wie ohne Komet. Dabei geht nichts kaputt, nur dieser eine Vorteil fehlt.

### Wo du es siehst

- **Im HUD**: Die Übersicht zeigt „X Komet-Features zurückgetreten oder ausgefallen“ und nennt die erste Funktion. Der Knopf **ansehen** erstellt ein Debug-Protokoll.
- **Im Menü**: Ist eine Funktion zurückgetreten oder zurückgehalten, ist ihr Schalter gesperrt. Darunter steht: „Eine andere Mod braucht hier das Verhalten des Spiels. Sobald sie es freigibt, gilt wieder deine Wahl.“ Hält eine Mod die Funktion fest, stehen ihre Mod-ID und ihr Grund dabei.
- **Im Debug-Protokoll**: Der Abschnitt „Komet-Einstellungen und Features“ nennt jede Funktion, die nicht aktiv ist, mit ihrem Zustand. Die Befunde nennen die Funktionen, die zurückgetreten sind, deren Spielcode sich geändert hat oder die fehlgeschlagen sind.
- **Im Log**: In `client-main.log` steht meist eine Zeile mit `Komet` und dem Namen der Funktion.

### Die Zustände

Im Protokoll stehen die Zustände mit ihrem englischen Namen.

| Zustand | Was passiert | Was du tust |
|---|---|---|
| zurückgetreten (`StoodDown`) | Eine andere Mod patcht denselben Code. Komet lässt ihr den Vortritt. | Nichts. Das Debug-Protokoll listet, welche Mods dieselben Methoden patchen. |
| zurückgehalten (`HeldOff`) | Eine andere Mod hat Komet gebeten, hier beim Verhalten des Spiels zu bleiben. | Nichts. Gibt die Mod die Funktion frei, gilt wieder deine Wahl. |
| Spielcode geändert (`EngineChanged`) | Ein Spiel-Update hat den Code geändert, den die Funktion ersetzt. Sie bleibt aus. | Warte auf ein Komet-Update. Ist Komet › Werkzeuge › **Update-Hinweise** an, sagt dir Komet, wenn es eines gibt. |
| fehlgeschlagen (`Failed`) | Die Funktion hatte einen Fehler. Jetzt läuft der Code des Spiels. | Melde es mit einem Debug-Protokoll. |
| nicht anwendbar (`NotApplicable`) | Sie gehört zum Server, und du spielst nicht im Einzelspieler, sondern auf einem Server. Oder deiner Grafikkarte oder deinem Treiber fehlt etwas. | Nichts, das ist normal. |
| aus (`Off`) | Alle ihre Schalter stehen auf dem Wert des Spiels. | Nichts, oder schalte sie wieder ein. |

## Einen Schalter prüfen

Hast du einen Komet-Schalter im Verdacht, prüf ihn so:

1. Schalte Komet › Werkzeuge › **Erweiterte Optionen zeigen** ein.
2. Auf Komets Seiten wie Rendern, Chunks und Sonstiges erscheint die Gruppe **Exakte Optimierungen**. Diese Schalter liefern dasselbe Bild wie das Spiel, nur schneller.
3. Schalte dort **Alle exakten Optimierungen** aus und klick auf **Übernehmen**.
4. Ist das Problem weg, schalte sie wieder ein und dann einzeln aus, bis du den Schalter gefunden hast.
5. Nenne ihn in deinem Bericht.

Die übrigen Schalter auf diesen Seiten prüfst du genauso. Das Debug-Protokoll nennt ohnehin jede Einstellung, die vom Spiel abweicht.

## Ein Problem melden

Das Debug-Protokoll ist ein Textbericht über alles, was Komet misst. Für Fehlerberichte hilft es sehr. Was das Debug-Fenster sonst noch kann, steht unter „HUD und Messen“.

1. Drück Strg+F8. Das Debug-Fenster öffnet sich.
2. Wähl die Art: **Sofort** (3 s) für den Zustand jetzt, **Aufnahme** für ein Problem, das immer wieder kommt, **Bei Spike** für seltene Ruckler.
3. Lass die Sprache auf **English**.
4. Starte die Aufnahme und tu währenddessen, was das Problem auslöst.
5. Das Protokoll erscheint im Fenster und liegt in deiner Zwischenablage.
6. Füg es in deinen Bericht ein (Discord, GitHub oder Chat). Schreib dazu, was du gemacht hast und was schiefgegangen ist.

### Chatbefehle

Dasselbe geht im Chat. Komets Befehle beginnen mit einem Punkt.

| Befehl | Was er tut |
|---|---|
| `.komet` | öffnet Komets Optionen |
| `.komet debug` | nimmt 10 s auf |
| `.komet debug 60` | nimmt 60 s auf (1 bis 600) |
| `.komet debug now` | nimmt 3 s auf |
| `.komet debug spike` | wartet auf einen Frame über der Grenze von **Ruckler-Hinweis ab** (Standard 50 ms) |
| `.komet debug spike 40` | wartet auf einen Frame über 40 ms |
| `.komet debug stop` | beendet eine Aufnahme früher |
| `.komet debug join` | nimmt ab jetzt die erste Minute jeder Welt auf; ein zweites Mal schaltet es wieder ab |
| `.komet debug now de` | `de` oder `en` wählt die Sprache des Protokolls, bei jeder Art |

### Was du anhängst

- Das Protokoll selbst. Es liegt auch als `komet-debug.txt` in `Logs/komet-debug/<Zeit>/` im Ordner VintagestoryData.
- Aus demselben Ordner: `frames.csv` (jeder Frame der Aufnahme), `harmony.txt` (welche Mod welche Methode patcht) und `summary.txt` (die Zahlen für den nächsten Vergleich).
- `client-main.log` aus `VintagestoryData/Logs`, vor allem nach einem Fehler oder Absturz.
- Fror das Spiel ein: `komet-hang-<Zeit>.txt` aus demselben Ordner `Logs`. Komet schreibt diese Datei, wenn 10 Sekunden lang kein Frame fertig wird.
- Lief Vulkan: `komet-vulkan-watch.txt` aus demselben Ordner `Logs`.

Komet behält die neuesten 30 Protokoll-Ordner und löscht ältere. Im Protokoll, in `harmony.txt` und in den Messungen steht dein Benutzerordner als `~` und dein Benutzername als `<user>`. So verraten die Pfade darin deinen Namen nicht.

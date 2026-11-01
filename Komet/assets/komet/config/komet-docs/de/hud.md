# HUD und Messen

Das HUD ist Komets Messanzeige. Es zeigt, wie flüssig das Spiel läuft, und sagt dir, was bremst. Es misst nur, solange das Overlay an ist, das HUD-Fenster offen ist oder eine Aufnahme oder ein Benchmark läuft. Wörter wie Frametime, Spike oder GC erklärt die Seite „Begriffe“.

## Die drei Fenster

| Taste | Was sich öffnet |
|---|---|
| F7 | Das Overlay: eine kleine Anzeige in einer Ecke des Bildschirms |
| Strg+F7 | Das HUD-Fenster mit Tabs für Frames, System, Render und mehr |
| Strg+F8 | Das Debug-Fenster: Aufnahmen und das Debug-Protokoll |

F7 schaltet das Overlay an und aus, der Chat bestätigt es. Die Tasten änderst du unter Vintage Story › Steuerung. Dort heißen sie „Komet HUD“, „Komet-HUD-Fenster“ und „Komet-Debug-Fenster“.

Ein Fenster verschiebst du an seiner Titelzeile. Komet merkt sich seine Position und den Tab, der zuletzt offen war. Komet › Werkzeuge › **Panel-Positionen** setzt die Positionen zurück (zweimal klicken).

## Das Overlay

Das Overlay zeigt:

- **FPS**: Bilder pro Sekunde.
- **1% Low**: die FPS der langsamsten 1 % der Frames. Diese Zahl zeigt, wie schlimm die langsamen Momente sind.
- **ms**: wie lange ein Frame im Schnitt dauert (die Frametime).
- einen Graphen: die Frametime der letzten 240 Frames. Jede Spitze ist ein langsamer Frame.
- **Mods gesamt**: wie viel Zeit der Code aller Mods pro Frame braucht, ohne das Spiel selbst.

FPS und Frametime gelten für den letzten kurzen Abschnitt (Komet › HUD › **Aktualisierung**, Standard 250 ms). Das 1-%-Low berechnet Komet aus den letzten 2000 gezählten Frames. Es erscheint ab 100 Frames, das 0,1-%-Low im Tab **Frames** ab 1000.

Läuft eine Debug-Aufnahme, steht oben rechts im Overlay **REC** mit den Sekunden. Wartet eine Aufnahme auf einen Spike, steht dort **bereit**.

### Der Ruckler-Hinweis

Ist ein Frame ein Spike und erreicht er den Wert von Komet › HUD › **Ruckler-Hinweis ab** (Standard 50 ms), erscheint für 5 Sekunden eine Karte unter dem Overlay (in einer unteren Ecke darüber). Sie nennt die Dauer, die wahrscheinliche Ursache und die Nummer des Spikes. Damit nichts flackert, kommt höchstens alle 3 Sekunden eine Karte. Die Karte erscheint nur, wenn das Overlay an ist.

### Welche Frames zählen

Komet zählt nur Frames, in denen du normal spielst. Nicht gezählt werden:

- die ersten 5 Sekunden in einer Welt,
- die Zeit, in der die ersten Chunks noch laden (höchstens 30 Sekunden),
- Frames, während das Spiel pausiert ist,
- etwa eine Sekunde, nachdem du das Overlay einschaltest oder das Optionsmenü oder das HUD-Fenster öffnest, schließt oder darin umschaltest.

So verfälschen Ladezeiten und Menüs die Lows nicht.

## Spikes und Ursachen

Ein Spike ist ein Frame, der viel länger dauert als die anderen. Komet zählt einen Frame als Spike, wenn er länger als 25 ms dauert und zugleich länger als das 2,5-Fache des laufenden Durchschnitts. Bei 60 FPS (16,7 ms) liegt die Grenze also bei etwa 42 ms, bei 120 FPS bei 25 ms.

Zu jedem Spike sucht Komet die Ursache:

| Ursache | Was sie bedeutet |
|---|---|
| GC-Pause | Die Speicherbereinigung hat das Spiel angehalten. |
| JIT-Kompilierung | Das Spiel hat Code übersetzt, den es zum ersten Mal brauchte. |
| Warten auf einen CPU-Kern | Andere Arbeit belegte die Kerne, das Spiel musste warten. Das misst Komet nur unter Linux. |
| zwischen den Frames (Eingabe, Events) | Arbeit außerhalb des Zeichnens, etwa Eingaben oder Ereignisse. |
| ein anderer Name | Der teuerste Arbeitsschritt des Spiels in diesem Frame, so wie ihn der Frame-Profiler des Spiels nennt. |
| nicht profiliert | Komet konnte den Frame keinem Schritt zuordnen. |

GC-Pause, JIT und das Warten auf einen Kern gewinnen, wenn eine davon allein mindestens die Hälfte der Zeit über dem Durchschnitt erklärt. Sonst nennt Komet den teuersten Arbeitsschritt, oder „zwischen den Frames“, wenn diese Zeit größer war.

### Einen Ruckler finden

```flow
F7: Overlay an und normal spielen
Ein Ruckler: die Karte nennt die Ursache
Strg+F7 › Frames: die letzten Spikes
Strg+F8 › Bei Spike: den nächsten aufnehmen
Das Protokoll lesen oder melden
```

## Das HUD-Fenster

Die Tabs des HUD-Fensters (Strg+F7):

- **Übersicht**: FPS, 1-%-Low, Frametime und GPU-Zeit, ein Graph und die Hinweise (siehe unten).
- **Frames**: außerdem p99 (die Frametime, unter der 99 % der Frames bleiben) und das 0,1-%-Low, ein großer Graph mit Linien bei 60 und 30 FPS, wie sich die Frametimes verteilen, und die letzten fünf Spikes mit Ursache.
- **System**: CPU des Spiels und des ganzen Rechners, Arbeitsspeicher, Grafikspeicher, GC-Pausen, geladene Chunks und Entities (Kreaturen, Spieler, liegende Gegenstände), Sichtweite.
- **Render**: welcher Renderer zeichnet, wohin die Zeit eines Frames geht, GPU-Zeit, Draw-Calls, Dreiecke und wie viel als verdeckt ausgelassen wird. Unter Vulkan auch, was Vulkan nicht zeichnet.
- **Threads**: wie beschäftigt der Haupt-Thread und Komets Hilfs-Threads sind, dazu Thread-Pool und Lock-Konflikte. Einige Werte gibt es nur unter Linux.
- **Log**: die neuesten Einträge aus `client-main.log`, nach Warnungen oder Fehlern filterbar. Ein Klick öffnet einen Eintrag ganz.

Oben rechts öffnet **Debug** das Debug-Fenster. Das Zahnrad zeigt die HUD-Einstellungen: was das Overlay zeigt, Position, Deckkraft, Schriftgröße, Spike-Hinweise und deine Tasten. Ein Klick auf das Versionsschild neben dem Titel zeigt deine Version und die Prüfsumme, mit dem Knopf **Jetzt prüfen**.

### Hinweise und was hilft

Auf der Übersicht steht unter „Hinweise und was hilft“, was gerade auffällt. Gab es Spikes, steht ganz oben der letzte; **ansehen** führt zum Tab **Frames**. Viele Hinweise haben einen Knopf, der mit einem Klick hilft. Ein Hinweis verschwindet, sobald seine Ursache weg ist. Fällt nichts auf, steht dort „Alles im grünen Bereich“.

| Hinweis | Knopf | Was der Knopf tut |
|---|---|---|
| Volle Collections dürfen das Spiel anhalten | Anwenden | schaltet Komet › Sonstiges › **Aufräumen im Hintergrund** ein |
| Der Garbage Collector hält das Spiel X % der Zeit an (ab 5 %) | Aufnehmen | startet eine Aufnahme von 10 s |
| Main-Thread wartet X ms pro Frame auf einen Kern (ab 0,5 ms, nur unter Linux, bei Priorität über 25 %) | Anwenden | setzt Komet › Chunks › **Priorität: Chunks laden** auf 25 % |
| X riskante Mod-Konflikte | keiner | der Hinweis nennt die erste Methode und ihre Mods; das Debug-Protokoll listet alle |
| Eine Mod kostet X ms pro Frame (ab 2 ms) | Aufnehmen | startet eine Aufnahme von 10 s; das Protokoll zeigt, welche Aufrufe der Mod die Zeit kosten |
| Grafikspeicher zu X % voll (ab 90 %) | keiner | eine geringere Sichtweite hilft |
| 1% Low bei X fps (unter 30) | Bereit machen | wartet auf den nächsten Spike und nimmt ihn auf |
| X Komet-Features zurückgetreten oder ausgefallen | ansehen | erstellt ein Debug-Protokoll (siehe „Probleme lösen“) |
| Neuerer Build auf GitHub | ansehen | zeigt Version und Prüfsumme |
| Die installierte Datei weicht von der veröffentlichten Prüfsumme ab | ansehen | zeigt Version und Prüfsumme |

Unter den Hinweisen öffnet **Debug-Protokoll erstellen** das Debug-Fenster.

## Das Debug-Fenster

Das Debug-Fenster (Strg+F8) schreibt das Debug-Protokoll: einen Textbericht über alles, was Komet misst. Während einer Aufnahme ist jede Messung an, auch wenn das HUD versteckt ist.

| Art | Wie lange | Wofür |
|---|---|---|
| Sofort | 3 s | ein schneller Blick auf den Zustand jetzt |
| Aufnahme | 10 s, 30 s, 60 s oder 10 Min. | Probleme, die immer wieder kommen |
| Bei Spike | wartet, hält dann 20 s davor und 2 s danach fest | seltene Ruckler |

- Für **Aufnahme** ist 30 s vorgewählt. **Stoppen** beendet sie früher.
- Für **Bei Spike** wählst du die Grenze: über 25, 50 oder 100 ms (vorgewählt 50 ms). **Abbrechen** beendet das Warten; das Protokoll meldet dann, dass kein Frame die Grenze überschritten hat.
- **Jeden Beitritt aufnehmen** nimmt die erste Minute jeder Welt auf, die du betrittst.
- Der Knopf rechts startet die gewählte Art: **Protokoll erstellen**, **Aufnahme starten** oder **Bereitschaft starten**.

### Das Protokoll

Ist die Aufnahme fertig, erscheint das Protokoll im Fenster und liegt in deiner Zwischenablage. Der Chat sagt, in welchem Ordner es liegt.

- Oben wählst du die Sprache: **English** oder **Deutsch**. Für Fehlerberichte ist Englisch der Standard. Ein Wechsel zeigt das letzte Protokoll in der anderen Sprache und kopiert es neu. Die Datei bleibt in der alten Sprache.
- **Alles kopieren** kopiert es noch einmal.
- **Springe zu** führt zu einem Abschnitt. Nach den Kopfzeilen beginnt das Protokoll mit den **Befunden**: was auffällt, mit der Stufe HOCH, MITTEL, NIEDRIG oder INFO.
- Gab es schon ein Protokoll, vergleicht Komet die beiden: Frametime, 1-%-Low, schlechtester Frame, Anteil über 50 ms, GC und Allokation. Was besser ist, steht in Grün, was schlechter ist, in Rot.

Ein Vergleich ist nur fair bei:

- derselben Art, Sichtweite und Fenstergröße sowie demselben Renderer,
- Aufnahmen ab 10 s (ein **Sofort** ist zu kurz),
- einem ähnlichen Blick (ähnlich viele gezeichnete Dreiecke),
- einer Welt, in der keine Chunks mehr laden.

Passt etwas nicht zusammen, schreibt Komet „Nicht vergleichbar“ und nennt den Grund. Wie du ein Protokoll meldest, steht auf der Seite „Probleme lösen“.

## HUD-Einstellungen

Die Seite Komet › HUD:

| Zeile | Was sie tut |
|---|---|
| **HUD anzeigen (F7)** | Overlay an oder aus |
| **Anordnung** | die Ecke: Oben links, Oben rechts, Unten links oder Unten rechts |
| **Deckkraft** | wie dicht der Hintergrund des Overlays ist, 0 bis 100 % (Standard 60 %) |
| **Schriftgröße** | 50 bis 200 % (Standard 100 %), für Overlay und Fenster |
| **Aktualisierung** | wie oft die Zahlen neu kommen, 100 bis 1000 ms (Standard 250 ms) |
| **FPS im Overlay** bis **Mods im Overlay** | was das Overlay zeigt, ein Schalter je Zeile |
| **Ruckler-Hinweis ab** | ab welcher Frametime die Karte erscheint, 0 bis 200 ms (Standard 50 ms, 0 schaltet sie ab) |
| **HUD-Fenster (Strg+F7)**, **Debug-Fenster (Strg+F8)** | **Öffnen** öffnet das Fenster |

Das Wichtigste davon gibt es auch kurz im HUD-Fenster hinter dem Zahnrad.

## Benchmark

Unter Komet › Werkzeuge:

- **Benchmark-Dauer**: 5 bis 120 s, Standard 30 s.
- **Benchmark**: Der Knopf (etwa „30 s starten“) misst für diese Dauer alles, was das HUD misst, auch wenn das HUD versteckt ist. Danach liegen die Mittelwerte und die letzten Spikes in deiner Zwischenablage und in `client-main.log`. Der Chat sagt Bescheid.
- **Aktuelle Werte**: **In Zwischenablage** erstellt ein Debug-Protokoll der Art **Sofort**.

Benchmark und Debug-Aufnahme laufen nie zugleich. Vergleiche nur Messungen an derselben Stelle, mit derselben Sichtweite und demselben Renderer.

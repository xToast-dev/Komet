# Das Optionsmenü

Komet ersetzt das Einstellungsmenü des Spiels durch ein eigenes. Darin stehen die Einstellungen des Spiels, Komets Optionen, die Shader und diese Hilfe an einem Ort, mit Suche und Voreinstellungen.

## Menü öffnen

- Esc › **Einstellungen** öffnet Komets Menü statt des Menüs des Spiels. Es beginnt auf Vintage Story › Allgemein.
- Der Chatbefehl `.komet` öffnet es immer, direkt auf Komet › Übersicht.
- **Esc** schließt es wieder. Was du noch nicht übernommen hast, fällt dabei weg.

Willst du das Menü des Spiels zurück, schalte Komet › Übersicht › **Einstellungsmenü ersetzen** aus. Kommst du über Esc › Einstellungen, findest du das Original außerdem ganz unten auf Vintage Story › Oberfläche: **Original-Grafikmenü**.

## Die fünf Abschnitte

Die Abschnitte sind die Reiter oben im Menü. Links stehen die Seiten des offenen Abschnitts.

| Abschnitt | Seiten |
|---|---|
| Vintage Story | Allgemein, Qualität, Leistung, Maus, Steuerung, Barrierefreiheit, Ton, Oberfläche; Entwickler, wenn der Entwicklermodus an ist |
| Komet | Übersicht, Rendern, Chunks, Sonstiges, Vulkan, HUD, Stil, Werkzeuge |
| Shader | Aktive Shader, dazu eine Seite pro Shader-Pack und pro Shader-Mod, die Komet steuert |
| Hilfe | Komet, Das Optionsmenü, HUD und Messen, Vulkan erklärt, Probleme lösen, Begriffe |
| Modding | Einstieg, Vorlagen, Deine Mod für Komet, Shader-Referenz, Shader für Vulkan, Menü-Stile, API für Mods |

Andere Mods können eigene Seiten oder ganze Abschnitte hinzufügen. Auf den Textseiten unter Hilfe und Modding springst du mit der Spalte **Inhalt** rechts zu einer Überschrift, und **Kopieren** an einem Codeblock legt ihn in die Zwischenablage.

## Voreinstellungen

Unter Komet › Übersicht wählst du in der Zeile **Einstellungen** eine Voreinstellung. Sie stellt die Optionen auf den Seiten Rendern, Chunks, Sonstiges und Vulkan auf einmal ein.

- **Automatisch (empfohlen)**: wie **Ausgewogen**, auf einem schwachen Rechner wie **Tempo**. Schwach heißt hier: höchstens 4 Threads des Prozessors (die Zeile **Dieser Rechner** nennt sie Kerne), 8 GB Arbeitsspeicher oder 4 GB Grafikspeicher.
- **Qualität**, **Ausgewogen**, **Tempo**: wie viel schönes Bild du gegen mehr FPS tauschst.
- **Angepasst** erscheint, sobald du selbst eine der Optionen änderst, die die Voreinstellung steuert. Dein Wert bleibt. Stellst du die Option auf ihren Standard zurück oder wählst du wieder eine Voreinstellung, folgt sie wieder der Voreinstellung und deinem Rechner.

Einige Optionen richtet Komet außerdem nach deinem Rechner aus, bei jeder Voreinstellung. Die Zeile **Dieser Rechner** zeigt, was Komet weiß: Kerne, Arbeitsspeicher, Grafikspeicher und die Bildwiederholrate deines schnellsten Monitors.

Der Knopf **Werte dieses Rechners** in dieser Zeile nimmt alle deine eigenen Änderungen an Komets Optionen zurück. Das gilt auch für die Gruppe **Allgemein** auf dieser Seite, die exakten Optimierungen und den **Renderer** (er steht danach wieder auf **OpenGL**). Danach folgt jede Option wieder der Voreinstellung und deinem Rechner. HUD, Stil und Werkzeuge bleiben, wie sie sind. Der Knopf wirkt sofort.

## Ändern und übernehmen

Das Menü sammelt Änderungen erst und lässt sie dann zusammen wirken. So laden zum Beispiel die Shader nur einmal neu.

```flow
Wert ändern
Zeile bekommt eine Marke
Enter oder Übernehmen
Alles wirkt auf einmal
```

| Aktion | Taste |
|---|---|
| Übernehmen | Enter |
| Übernehmen und schließen | erst Enter, dann Esc |
| Alles zurücknehmen | Entf |
| Ganze Seite auf Standard | R |
| Schließen ohne Übernehmen | Esc |

- Geänderte Zeilen sind markiert, mit einem kleinen Punkt hinter dem Namen.
- Die Tasten stehen unten in der Fußzeile, und du kannst sie auch anklicken. Daneben steht, wie viele Änderungen warten. Ein Klick auf die Zahl zeigt die Liste **Deine Änderungen**, in der du jede Zeile noch einmal ändern kannst. Noch ein Klick oder **Esc** bringt dich zurück.
- Sofort, ohne Übernehmen, wirken:
  - Knöpfe und Tastenbelegungen
  - die Zeilen auf Komet › Stil
  - auf Shader › Aktive Shader die Schalter unter **Für Komet gemacht** und **Weitere Shader-Mods**
  - die Seiten, auf denen „Komet übernimmt …“ steht

## Suche

- Drück **Strg+F** oder klick in das Suchfeld über der Seitenliste. Erst dann kannst du tippen. Vorher wirken Q, E und R als Tastenkürzel.
- Die Suche findet auf allen Seiten jede Option, deren Name oder Beschreibung dein Suchwort enthält. Über den Treffern stehen Abschnitt und Seite. Die Texte der Hilfe durchsucht sie nicht.
- **Enter** beendet das Tippen, **Esc** oder **×** leert die Suche.

## Tasten

| Taste | Wirkung |
|---|---|
| Q / E | Abschnitt zurück / weiter |
| Tab / Shift+Tab | Seite weiter / zurück |
| Pfeil hoch / runter | Zeile wählen |
| Bild auf / Bild ab | acht Zeilen hoch / runter |
| Pfeil links / rechts | Wert der gewählten Zeile ändern |
| Shift+Pfeil links / rechts | Regler um zehn Schritte |
| Pos1 / Ende | angeklickter Regler auf kleinsten / größten Wert |
| Enter | Knopf drücken, Taste belegen, sonst übernehmen |
| R | ganze Seite auf Standard |
| Entf | alle Änderungen zurücknehmen |
| Strg+F | Suche |
| Esc | einen Schritt zurück, sonst Menü schließen |

Die gewählte Zeile folgt auch der Maus.

**Esc** geht erst einen Schritt zurück: Es lässt einen angeklickten Regler los, verlässt die Liste deiner Änderungen oder leert die Suche. Erst wenn nichts mehr offen ist, schließt Esc das Menü.

## Zeilen bedienen

- **Schalter**: Ein Klick schaltet an oder aus.
- **Auswahl**: Ein Klick geht zum nächsten Wert, ein Rechtsklick zurück. Du kannst auch auf die Pfeile links und rechts neben dem Wert klicken.
- **Regler**: Zieh ihn oder klick auf die gewünschte Stelle. Ein Rechtsklick setzt ihn auf seinen Standard. Weicht der Wert davon ab, zeigt eine kleine Marke, wo der Standard liegt.
- **Knopf**: Ein Klick führt ihn sofort aus.
- Fährst du mit der Maus über eine Zeile, erklärt sie der Bereich rechts.
- Ausgegraute Zeilen können gerade nichts bewirken, weil eine Option aus ist, die sie brauchen. Schalte diese Option zuerst an.
- Steht in der Erklärung, dass eine andere Mod hier das Verhalten des Spiels braucht, ist die Zeile gesperrt. Sobald die Mod es freigibt, gilt wieder deine Wahl.

## Erweiterte Optionen

Normalerweise zeigen Komets Seiten nur Optionen, bei denen du abwägst, etwa Bild gegen Tempo oder Arbeitsspeicher gegen Rechenzeit. Komet › Werkzeuge › **Erweiterte Optionen zeigen** blendet auf Rendern, Chunks, Sonstiges und Vulkan zusätzlich die Gruppe **Exakte Optimierungen** ein. Sie enthält alle übrigen Komet-Schalter und ganz oben den Schalter **Alle exakten Optimierungen**, der alle auf einmal an- oder ausschaltet.

Exakte Optimierungen liefern dasselbe Ergebnis wie das Spiel (oder beheben einen Fehler darin), nur schneller oder sparsamer, und bleiben an. Schalte eine davon nur aus, um einen Fehler einzugrenzen, und nenne sie dann in deinem Fehlerbericht (siehe „Probleme lösen“).

## Tastenbelegung

Vintage Story › Steuerung zeigt alle Tastenbelegungen des Spiels auf einer Seite. Änderungen hier wirken sofort, ohne Übernehmen.

- Klick eine Zeile an (oder drück **Enter**) und drück dann die neue Taste, Tastenkombination oder Maustaste.
- **Esc** behält die alte Belegung.
- **×** entfernt die Belegung. Die Zeile zeigt dann **nicht belegt**.
- Ein Rechtsklick holt den Standard des Spiels zurück.
- Doppelt belegte Tasten erscheinen rot, mit dem Namen der anderen Belegung.
- **R** setzt alle Belegungen der Seite sofort auf den Standard des Spiels.
- Ganz unten setzt **Steuerung zurücksetzen** alle Belegungen zurück (zweimal klicken). Kommst du über Esc › Einstellungen, gibt es dort auch **Makro-Editor öffnen**.

## Werkzeuge

Komet › Werkzeuge sammelt Schalter und Knöpfe für Wartung und Fehlersuche.

| Zeile | Was sie tut |
|---|---|
| **Erweiterte Optionen zeigen** | zeigt die exakten Optimierungen (siehe oben) |
| **Update-Hinweise** | sucht beim Betreten einer Welt nach Updates (siehe „Komet“) |
| **Prüfsumme** | zeigt deine Version und ob deine Datei der veröffentlichten entspricht |
| **Gelände neu aufbauen** | baut alle geladenen Chunks neu auf, ohne die Welt zu verlassen, und gibt dabei ihren Grafikspeicher frei |
| **Benchmark-Dauer**, **Benchmark** | misst 5 bis 120 s lang und legt die Mittelwerte in die Zwischenablage und ins Client-Log (siehe „HUD und Messen“) |
| **Aktuelle Werte** | nimmt 3 s lang ein Debug-Protokoll auf und legt es in die Zwischenablage |
| **Panel-Positionen** | stellt HUD-Fenster und Debug-Fenster wieder an ihren Startplatz |
| **Anzeige, Panels und Funktionen** | setzt HUD-Anzeige, das Aussehen des Menüs, alle Komet-Optionen und die Positionen zurück |

Die beiden Knöpfe zum Zurücksetzen fragen vorher nach: Der erste Klick macht aus **Zurücksetzen** ein **Bestätigen**, erst der zweite setzt zurück. Deine Antwort zu den Update-Hinweisen bleibt dabei erhalten.

## Wo alles gespeichert ist

Alle Pfade liegen in deinem Ordner VintagestoryData.

| Pfad | Inhalt |
|---|---|
| `ModConfig/komet.json` | Voreinstellung, die Komet-Optionen, die du selbst gesetzt hast, HUD und Menü |
| `ModConfig/komet/styles/` | deine eigenen Menü-Stile |
| `ModConfig/komet/logo.png` | dein Logo im Kopf, wenn der Stil kein eigenes hat |
| `ModConfig/komet/shaders/` | Shader-Packs zum Ausprobieren |
| `ModConfig/komet/shader-settings.json` | welche Shader an oder aus sind und die Werte der Packs |
| `ModConfig/komet/mod-template/` | Mod-Vorlagen aus Modding › Vorlagen |
| `Logs/komet-debug/` | Debug-Protokolle |
| `Cache/komet-vulkan/` | gespeicherte Vulkan-Shader (mit **Vulkan: Shader speichern**) |

## Aussehen des Menüs

Komet › Stil bestimmt, wie dieses Menü aussieht. Alles auf dieser Seite wirkt sofort.

- **Stil**: Pragmatic (ab Werk) mit Reitern oben, Seitenliste, Zeilen und Details vor einem Planeten. Dazu kommen Stile anderer Mods und deine eigenen.
- **Deckkraft der Flächen** und **Animationen** (Aus, Ruhig, Voll) stellen Feinheiten ein.

Wie du einen eigenen Stil baust, steht unter „Menü-Stile“ im Abschnitt Modding. Ein fertiges Beispiel legt Modding › Vorlagen › **Beispiel-Stil anlegen** an.

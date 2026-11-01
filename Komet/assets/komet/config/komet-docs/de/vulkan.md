# Vulkan erklärt

Komet kann die Welt mit Vulkan statt mit OpenGL zeichnen. Der erste Teil dieser Seite sagt dir, was das bringt, wie du es ausprobierst und was du einstellen kannst. Der zweite Teil erklärt die Technik dahinter, für alle, die es genau wissen wollen. Wörter wie Shader, Pass oder Pool erklärt die Seite „Begriffe“.

## Warum Vulkan

Vintage Story zeichnet mit OpenGL. OpenGL ist alt und bewährt, aber der Treiber erledigt viel Arbeit im Hintergrund, und das Spiel muss oft auf ihn warten. Mit Vulkan entscheidet Komet mehr selbst: wann auf die Grafikkarte gewartet wird, was im Speicher bleibt und welches Gelände die Grafikkarte weglassen kann.

Was das für dich bedeuten kann:

- Weniger Arbeit für die CPU, weil die Grafikkarte selbst aussucht, welches Gelände sie zeichnet.
- Gleichmäßigere Bilder mit **Vulkan: gleichmäßiger Takt** und **Späte Bilder überbrücken**.
- Weniger Grafikspeicher mit **Grafikspeicher sparen**.

Die andere Seite, ganz ehrlich: Der Vulkan-Renderer ist **experimentell**. Was Vulkan nicht zeichnen kann, fehlt im Bild. OpenGL bleibt die sichere Wahl. Bringt dir Vulkan nichts oder sieht etwas falsch aus, schalte zurück.

## Ausprobieren

1. Öffne Komet › Übersicht.
2. Stell **Renderer** auf **Vulkan (experimentell)** und übernimm die Änderung (**Enter**). Das wirkt sofort, auch mitten in einer Welt.
3. Das Gelände wird neu aufgebaut. Nach ein paar Sekunden ist es wieder da.
4. Über der Zeile steht, wer gerade zeichnet: **Renderer (aktiv: Vulkan)**.

Steht dort noch OpenGL, wartet Vulkan auf etwas oder konnte nicht starten. Den Grund nennt dann eine Zeile `Komet: Vulkan …` in `client-main.log`. Unter Windows und auf Linux-Systemen ohne shaderc (das Werkzeug, das die Shader übersetzt) lädt Komet es beim ersten Mal herunter (etwa 21 MB). Bis dahin zeichnet OpenGL weiter. Der Chat sagt dir, wann du die Welt verlassen und neu betreten sollst.

**Zurückschalten**: Stell **Renderer** wieder auf **OpenGL** und übernimm. Auch das wirkt sofort, und das Gelände wird noch einmal neu aufgebaut. Verlässt du die Welt, endet die Arbeit von Vulkan ebenfalls. Das Hauptmenü und die Ladebildschirme zeichnet immer OpenGL.

Fällt Vulkan aus, übernimmt OpenGL ab dem nächsten Bild, und über der Zeile steht **OpenGL (Vulkan ausgefallen, siehe Log)**. Für einen neuen Versuch stellst du **Renderer** auf OpenGL und dann wieder auf Vulkan.

## Die Vulkan-Optionen

Alle weiteren Optionen stehen unter Komet › Vulkan. Sie wirken nur, solange Vulkan zeichnet. Mit OpenGL sind ihre Zeilen ausgegraut. Die Spalte „Standard“ zeigt, was gilt, solange du eine Option nicht selbst änderst.

### Immer zu sehen

| Option | Standard | Was sie tut |
|---|---|---|
| Verdecktes Gelände auslassen | 2 | Lässt Gelände weg, das du nicht siehst (0 bis 2) |
| Grafikspeicher sparen | an | OpenGL bekommt keine zweite Kopie der großen Daten |
| Späte Bilder überbrücken (experimentell) | an ab 120 Hz | Zeigt bei einem späten Bild eine gedrehte Kopie |
| Vulkan: GPU-Zeiten messen | an | Misst die Zeit der Grafikkarte pro Teil des Bildes |

### Erweiterte Zeilen

Diese drei Zeilen erscheinen erst mit Komet › Werkzeuge › **Erweiterte Optionen zeigen**. Sie stehen unter **Exakte Optimierungen**. Der Schalter **Alle exakten Optimierungen** schaltet sie zusammen ein oder aus.

| Option | Standard | Was sie tut |
|---|---|---|
| Vulkan: gleichmäßiger Takt | an | Wartet am Anfang des Bildes auf die Grafikkarte |
| Ruhendes OpenGL entlasten | an | Schickt Shader-Werte nicht an das ruhende OpenGL |
| Vulkan: Shader speichern | an | Behält übersetzte Shader auf der Festplatte |

### Einzelheiten

- **Verdecktes Gelände auslassen**: 0 wie im Spiel, 1 lässt auch Gelände hinter anderem Gelände weg, 2 lässt die Grafikkarte alles selbst aussuchen. Kann deine Grafikkarte Stufe 2 nicht, gilt Stufe 1. Mehr dazu unten bei „Gelände und GPU-Culling“.
- **Grafikspeicher sparen**: OpenGL hält sonst eine eigene Kopie von allem, damit es jederzeit übernehmen kann. Mit dieser Option behält es für große Texturen und Puffer nur kleine Platzhalter. Das spart Uploads und meist mehrere hundert MB. Hört Vulkan auf, gibt Komet OpenGL die echten Inhalte zurück. Nur wenn die Grafikkarte selbst ausgefallen ist, geht das nicht: Dann siehst du nur die Oberfläche und musst die Welt neu betreten. Umschalten wirkt am besten vor dem Betreten einer Welt.
- **Späte Bilder überbrücken**: Lohnt sich vor allem bei schnellen Monitoren und kostet etwas Grafikkartenzeit. Wie es arbeitet, steht unten bei „Die Bildbrücke“.
- **Vulkan: GPU-Zeiten messen**: Die Zeiten für Gelände, Himmel, Kreaturen, Partikel und Effekte erscheinen im HUD und im Log. Ist das HUD aus, misst Komet nur jedes 16. Bild. Das Messen kostet ein wenig Leistung.
- **Vulkan: gleichmäßiger Takt**: Ist die Grafikkarte der Engpass, bewegt sich die Kamera gleichmäßiger, mit weniger Mikrorucklern.
- **Ruhendes OpenGL entlasten**: Spart etwa 0,15 ms pro Bild.
- **Vulkan: Shader speichern**: Weniger Ruckler, wenn Dinge zum ersten Mal erscheinen. Mehr dazu unten bei „Shader-Cache“.

## Etwas sieht falsch aus

1. Schalte auf OpenGL und sieh dir dieselbe Stelle an. Ist es dort auch falsch, liegt es nicht an Vulkan.
2. Öffne das HUD-Fenster (Strg+F7), Tab **Render**. Steht dort **… Aufrufe pro Frame fehlen**, klick auf die Zeile: Sie klappt auf und nennt die Arten, die Vulkan nicht zeichnen kann.
3. Such in `client-main.log` nach Zeilen, die mit `Komet: Vulkan` beginnen, besonders nach solchen mit `cannot draw`, `failed` oder `stopped`.
4. Nutzt du Shader-Packs oder Shader-Mods, sieh unter Shader › Aktive Shader nach. Dort steht bei einer Mod, wenn sich ihre Shader nicht für Vulkan übersetzen lassen.
5. Schalte diese Optionen einzeln aus: **Grafikspeicher sparen**, **Späte Bilder überbrücken**. Stell danach **Verdecktes Gelände auslassen** niedriger.
6. Versuch es mit Komet › Werkzeuge › **Gelände neu aufbauen**.
7. Hilft nichts davon, nimm ein Debug-Protokoll auf, während das Problem zu sehen ist. Wie das geht und was in einen Bericht gehört, steht auf der Seite „Probleme lösen“.

---

## Das große Bild

Ab hier geht es um die Technik. Zum Spielen brauchst du nichts davon.

Das Spiel kennt Vulkan nicht. Es spricht weiter OpenGL: Es legt Texturen an, lädt Chunks hoch, setzt Shader und bittet ums Zeichnen, alles mit OpenGL-Aufrufen.

Komet sitzt dazwischen. Ist Vulkan gewählt, hört Komet ab dem Start der Mod jeden OpenGL-Aufruf des Spiels mit. Schaltest du erst in der Welt um, holt Komet sich das, was schon da ist, von OpenGL. Im Code heißt dieser Zuhörer **Tap** (Abzweig). Er schreibt mit, was das Spiel anlegt (Texturen, Puffer, Shader, Framebuffer) und worum es bittet. Gezeichnet wird alles in Vulkan, mit Komets eigenen Kopien.

OpenGL **ruht** in dieser Zeit. Es bekommt weiter die Uploads, die Namen und die Einstellungen, zeichnet aber nichts von der Welt. So kann es sofort übernehmen, falls Vulkan aufhört.

```flow
Das Spiel ruft OpenGL zum Zeichnen
Komets Tap fängt den Aufruf ab
Vulkan zeichnet mit Komets Kopien
OpenGL bekommt nur die Daten
Vulkan zeigt das fertige Bild
```

## Der Weg eines Bildes

1. Das Bild beginnt schon vor dem ersten Zeichenschritt des Spiels, damit die ganze Arbeit dieses Bildes in Vulkan landet.
2. Die Gelände-Passes: Wassertiefe, Schattenkarten, das feste Gelände, Glas und Wasser, Wasserpflanzen.
3. Der Rest der Szene: Himmel, Kreaturen, Partikel, Effekte wie SSAO oder Gottesstrahlen und die Passes von Shader-Packs.
4. Die Oberfläche obendrauf: Schnellleiste, Chat, Menüs, HUD.
5. Vulkan zeigt das fertige Bild im Spielfenster (über die Swapchain, siehe unten).

Komet schreibt die Vulkan-Befehle in einem eigenen Thread auf. So wartet der Haupt-Thread des Spiels weniger.

## Shader übersetzen

Die Shader des Spiels sind in GLSL für OpenGL geschrieben. Vulkan braucht sie in einer anderen Form: SPIR-V. Komet übersetzt jeden Shader selbst, damit weder du noch Autoren von Shader-Packs das tun müssen.

```flow
Das Spiel verknüpft einen Shader
Komet liest seinen Quelltext
Komet passt ihn für Vulkan an
shaderc übersetzt ihn in SPIR-V
Komet baut daraus eine Pipeline
```

Der Quelltext enthält schon die Einstellungen des Spiels und das, was Mods daran geändert haben. Komet ändert nur die wenigen Teile, die Vulkan anders braucht, zum Beispiel wo Werte liegen. Eine **Pipeline** ist der Shader samt allen Zeichen-Einstellungen. Übersetzt und gebaut wird in Hilfs-Threads, schon während die Welt lädt. Ein Zeichenauftrag, dessen Shader noch nicht fertig ist, wartet darauf. Solange Komet Shader vorbereitet, zeigt oben in der Mitte des Bildschirms eine Karte den Fortschritt: **Komet · Vulkan bereitet Shader vor: … von …**.

### shaderc

Unter Linux nutzt Komet zuerst das shaderc des Systems. Unter Windows und auf Linux-Systemen ohne shaderc lädt Komet es einmal von nuget.org herunter (Paket Silk.NET.Shaderc.Native) und prüft es gegen eine feste SHA-256-Prüfsumme. Es liegt dann in `VintagestoryData/Cache/komet-shaderc`. Während des Herunterladens zeichnet OpenGL weiter. Ist shaderc da, verlässt du die Welt und betrittst sie neu, damit Vulkan startet. Schlägt das Herunterladen fehl, versucht Komet es beim nächsten Betreten der Welt wieder.

### Shader-Cache

Mit **Vulkan: Shader speichern** landen übersetzte Shader und Pipelines in `VintagestoryData/Cache/komet-vulkan`. Ab dem nächsten Start liest Komet sie von der Festplatte, statt sie neu zu übersetzen. So fallen die meisten Ruckler weg, wenn Welt, Kreaturen oder Partikel zum ersten Mal erscheinen. Schon beim ersten Start baut Komet häufige Pipelines vorab, nach einer mitgelieferten Liste. Nach einem Komet-Update werden die Shader einmal neu übersetzt.

## Gelände und GPU-Culling

Unter Vulkan gehört jede Ressource Vulkan selbst: die Chunk-Pools, die Texturen, die Bildschirmbilder. Komet übernimmt jeden Chunk-Pool, sobald das Spiel ihn anlegt. So verursachen neue Chunks keine zusätzliche Wartezeit.

### Zwei Durchgänge

**Verdecktes Gelände auslassen** lässt die Grafikkarte Gelände weglassen, das du nicht sehen kannst:

- **0**: wie im Spiel.
- **1**: lässt auch Gelände weg, das hinter anderem Gelände liegt.
- **2**: Die Grafikkarte sucht das Gelände ganz selbst aus und lässt zusätzlich Seiten weg, die von dir wegzeigen. Das entlastet die CPU.

Dafür hält die Grafikkarte eine **Tiefenpyramide**: ein kleines Bild davon, wie weit die Dinge entfernt sind. Sie prüft in zwei Durchgängen, damit nichts ein Bild zu spät erscheint:

1. Erster Durchgang: Jeder Chunk wird gegen die Pyramide des letzten Bildes geprüft (mit der Kamera mitverschoben). Was klar sichtbar ist, wird gezeichnet.
2. Aus dem bisher gezeichneten Boden entsteht eine neue Pyramide.
3. Zweiter Durchgang: Was der erste Durchgang versteckt hat, wird gegen die neue Pyramide noch einmal geprüft. Was doch sichtbar ist, wird jetzt gezeichnet, im selben Bild.

Stufe 2 braucht Rechnen mit doppelter Genauigkeit auf der Grafikkarte. Kann sie das nicht, gilt Stufe 1. Fehlt der Grafikkarte eine Fähigkeit, die dieses Culling überhaupt braucht (`drawIndirectCount`), zeichnet Komet das feste Gelände ganz ohne dieses Culling und schreibt das ins Log.

## Das Bild zeigen

Unter Vulkan zeigt Komet das Bild selbst im Spielfenster, über eine **Swapchain**: ein paar Bildschirmbilder, in die abwechselnd gezeichnet wird und die abwechselnd gezeigt werden. Das geht unter Linux (Wayland oder X11) und unter Windows. Die Swapchain folgt der VSync-Einstellung des Spiels. Ist VSync aus, nimmt Komet den Modus Mailbox, wenn dein Treiber ihn anbietet: Der Monitor zeigt bei jedem Auffrischen das neueste fertige Bild, ohne Tearing und ohne dass das Spiel auf ihn wartet.

Ändert sich die Fenstergröße, wird die Swapchain neu angelegt. Bekommt Vulkan gar keine Swapchain, kann es nicht laufen, und OpenGL zeichnet stattdessen.

### Die Bildbrücke

Mit **Späte Bilder überbrücken** achtet Komet auf Bilder, die deutlich später kommen als sonst, etwa bei einer GC-Pause. Kommt eins zu spät, zeigt ein eigener Thread das letzte Bild noch einmal, gedreht dorthin, wohin sich die Kamera gerade dreht. So bleibt das Umsehen flüssig.

- Überbrückt wird nur das Drehen. Kreaturen und deine eigene Bewegung laufen weiter mit den FPS des Spiels.
- Deine Hand in der Ich-Perspektive, dein Körper in der Außenansicht und die Oberfläche werden so gezeigt, wie sie gezeichnet wurden, nicht gedreht.
- Am meisten lohnt es sich bei schnellen Monitoren. Komet schaltet es ab 120 Hz von selbst ein.

## Was fehlen kann

Vulkan kann fast alles ausdrücken, was das Spiel tut, aber nicht jeden einzelnen OpenGL-Trick. Trifft Komet auf einen Zeichenauftrag, den es nicht übernehmen kann, passiert Folgendes:

- Dieser Auftrag **fehlt** im Bild. OpenGL füllt die Lücke nicht, denn das Bild von OpenGL wird nie gezeigt.
- Komet schreibt pro Art eine Zeile in `client-main.log`: `Komet: Vulkan cannot draw …: it is missing from the picture`.
- Das HUD-Fenster zählt im Tab **Render** die fehlenden Aufrufe pro Frame und nennt ihre Arten.

Fällt Vulkan selbst aus (durch einen Fehler in Komets Vulkan-Code oder auf der Grafikkarte), schreibt Komet eine Warnung wie `Komet: Vulkan failed: …` oder `Komet: Vulkan stopped: …` ins Log. Komet gibt Vulkan und seinen Speicher frei, und ab dem nächsten Bild zeichnet wieder OpenGL. Dasselbe passiert, wenn der Arbeitsspeicher des Systems fast voll ist (`Komet: Vulkan let go for this world: …`), damit das System das Spiel nicht beendet.

## Bekannte Grenzen

- Der Renderer ist experimentell. Was er nicht zeichnen kann, fehlt im Bild.
- Andere Mods, die OpenGL direkt aufrufen, laufen meist mit: Komet zeichnet auch sie in Vulkan, soweit Vulkan das ausdrücken kann. Was nicht geht, fehlt im Bild, aber nur unter Vulkan.
- Shader-Packs laufen mit beiden Renderern.
- Du schreibst selbst Shader? Was Vulkan von ihnen verlangt, steht auf der Seite „Shader für Vulkan“ unter Modding.

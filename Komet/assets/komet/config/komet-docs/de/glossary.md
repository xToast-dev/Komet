# Begriffe

Diese Seite erklärt die Wörter, die dir in Komets Menü, im HUD und im Debug-Protokoll begegnen. Bei jedem Eintrag steht, was das Wort bedeutet und was es im Spiel bewirkt. Zum Spielen musst du nichts davon wissen. Es hilft aber, wenn du einen Hinweis, eine HUD-Zeile oder einen Fehlerbericht liest.

## Bild und Tempo

- **Frame (Bild)**: ein einzelnes Bild, das das Spiel zeichnet. Viele Bilder pro Sekunde ergeben ein bewegtes Bild.
- **FPS** (Bilder pro Sekunde): wie viele Bilder das Spiel in einer Sekunde zeichnet. Mehr ist flüssiger.
- **Frametime**: wie lange ein Bild gedauert hat, in Millisekunden (ms). 60 FPS heißt etwa 16,7 ms pro Bild. Weniger ist besser. Gleichmäßige Frametimes fühlen sich flüssiger an als hohe, aber sprunghafte FPS.
- **1-%-Low**: die FPS der langsamsten 1 % aller Bilder. Sie zeigen, wie schlimm die langsamen Momente sind. Komets wichtigstes Ziel ist ein besseres 1-%-Low, nicht nur ein höherer Durchschnitt. Das HUD zeigt auch ein 0,1-%-Low, die langsamsten 0,1 %.
- **Ruckler, Spike**: ein einzelnes Bild, das viel länger dauert als die anderen. Du siehst es als kurzes Stocken. Komets HUD zählt ein Bild als Spike, wenn es länger als 25 ms dauert und zugleich länger als das 2,5-Fache des Durchschnitts. Es nennt auch die wahrscheinliche Ursache.
- **Mikroruckler**: winzige, regelmäßige Ruckler, auch wenn die FPS gut aussehen. Sie kommen von Bildern, die abwechselnd kurz und lang dauern.
- **VSync**: Das Spiel wartet auf deinen Monitor, bevor es ein neues Bild zeigt. Das verhindert Tearing (eine sichtbare Schnittlinie im Bild), kann aber etwas Verzögerung bringen.
- **FPS-Begrenzung**: eine Obergrenze für die FPS, die du einstellst. Das Spiel ruht dann zwischen den Bildern. Es rundet die Grenze aber auf ganze Millisekunden, und das erzeugt Mikroruckler. Komets **Gleichmäßige FPS-Begrenzung** hält sie genau ein (nur ohne VSync). Ohne Begrenzung gleicht **Gleichmäßige Bildzeit ohne Grenze** die Frametimes an.
- **CPU** (Prozessor) und **GPU** (Grafikkarte): Die CPU berechnet die Spiellogik und bereitet die Arbeit vor, die GPU zeichnet das Bild. Die langsamere von beiden begrenzt deine FPS.

## Speicher

- **Arbeitsspeicher, RAM**: der Speicher, in dem das Spiel die Welt, die Kreaturen und alles andere hält, solange es läuft.
- **Grafikspeicher, VRAM**: der eigene Speicher der Grafikkarte. Er hält Texturen (die Bilder auf Blöcken und Kreaturen), die Formen des Geländes und die Bildschirmbilder. Läuft er voll, wird das Spiel stark langsamer oder stürzt ab.
- **Allokation**: Das Spiel fordert ein Stück Speicher für etwas Neues an, zum Beispiel für eine Liste oder die Daten eines Chunks. Viele kurzlebige Allokationen werden bald danach zu Müll.
- **Müll**: Speicher, den das Spiel nicht mehr braucht, der aber noch nicht freigegeben ist.
- **GC, Speicherbereinigung**: das Aufräumen, das Müll freigibt. Das Spiel (gebaut auf .NET) macht das selbst. Während es aufräumt, kann das Spiel kurz stehen bleiben, und das siehst du als Ruckler. Komet erzeugt weniger Müll und verlegt das Aufräumen, wo es geht, in ruhige Momente. Das HUD zeigt, wie viel vom schlimmsten Bild eine GC-Pause war.

## Welt

- **Block**: ein Würfel der Welt, 1 × 1 × 1.
- **Chunk**: ein Stück der Welt aus 32 × 32 × 32 Blöcken. Das Spiel lädt, baut und zeichnet die Welt Chunk für Chunk.
- **Chunk-Säule**: alle Chunks an einer Stelle übereinander, vom Boden der Welt bis zum Himmel.
- **Sichtweite**: wie weit du sehen kannst, in Blöcken. Mehr Sichtweite heißt viel mehr Chunks, die gebaut, gespeichert und gezeichnet werden müssen.
- **Mesh**: die Form von etwas, wie die Grafikkarte sie braucht, also viele kleine Dreiecke mit ihren Ecken, ihrer Textur und Farbe. Jeder Chunk hat ein Mesh.
- **Tessellierung (Chunk-Aufbau)**: aus den Blöcken eines Chunks sein Mesh machen. Das passiert in Hilfs-Threads, also Helfern, die neben dem eigentlichen Spiel laufen. Komet baut die nächstgelegenen Chunks zuerst.
- **Upload**: ein fertiges Mesh oder eine Textur an die Grafikkarte schicken. Zu viele Uploads in einem Bild erzeugen Ruckler, darum verteilt Komet sie auf mehrere Bilder (**Zeit für Chunk-Uploads**).
- **Pool**: ein großes Stück Grafikspeicher, in dem die Meshes vieler Chunks gemeinsam liegen. Aus wenigen Pools zu zeichnen ist schneller als aus Tausenden kleiner Stücke.

```flow
Du setzt einen Block oder baust ihn ab
Ein Hilfs-Thread baut das Mesh neu
Das Mesh geht als Upload zur Grafikkarte
Es landet in einem Pool
Die Grafikkarte zeichnet den Pool
```

## Zeichnen

### Renderer und Shader

- **Renderer**: der Teil, der das Bild zeichnet. In Komet wählst du zwischen OpenGL und Vulkan (Komet › Übersicht › **Renderer**).
- **OpenGL**: ein älterer, weit verbreiteter Weg, auf dem Programme mit der Grafikkarte sprechen. Vintage Story nutzt ihn. Er ist die sichere Wahl.
- **Vulkan**: ein neuerer Weg, mit der Grafikkarte zu sprechen. Er gibt einem Programm mehr Kontrolle und lässt es manche Arbeit mit weniger Warten erledigen. Komets Vulkan-Renderer ist experimentell. Mehr dazu auf der Seite „Vulkan erklärt“.
- **Treiber**: die Software vom Hersteller deiner Grafikkarte, die OpenGL- oder Vulkan-Aufrufe in Arbeit für die Karte übersetzt.
- **Shader**: ein kleines Programm, das auf der Grafikkarte läuft. Es bestimmt, wo jede Ecke auf dem Bildschirm landet und welche Farbe jedes Pixel bekommt. Wasser, Schatten und Licht entstehen durch Shader.
- **GLSL**: die Sprache, in der die Shader des Spiels geschrieben sind.
- **SPIR-V**: die Form, in der Vulkan seine Shader braucht. Komet übersetzt das GLSL des Spiels in SPIR-V.
- **shaderc**: das Werkzeug, das Komet für diese Übersetzung nutzt. Hat dein System keins, lädt Komet es einmal herunter.
- **Uniform**: ein Wert, den das Spiel einem Shader im laufenden Spiel übergibt, zum Beispiel die Tageszeit oder die Position der Kamera. Ihn zu ändern kostet fast nichts.
- **Define**: eine feste Einstellung, die in den Text eines Shaders geschrieben wird, bevor er übersetzt wird. Ändert sich ein Define, müssen die Shader neu geladen werden, und das dauert einen Moment.
- **Pipeline**: ein Shader zusammen mit allen Zeichen-Einstellungen, die Vulkan dafür braucht. Sie zu bauen dauert beim ersten Mal eine Weile, darum kann Komet sie speichern (siehe **Shader-Cache** unten).
- **Swapchain**: ein paar Bildschirmbilder, in die abwechselnd gezeichnet wird und die abwechselnd im Fenster erscheinen. Unter Vulkan zeigt Komet das Bild über eine eigene Swapchain.

### Schritte eines Bildes

- **Pass (Durchgang)**: ein Schritt beim Zeichnen eines Bildes. Zum Beispiel: erst die Schatten, dann das feste Gelände, dann Wasser und Glas, dann Effekte, zuletzt die Oberfläche obendrauf.
- **Draw-Call (Zeichenauftrag)**: ein Auftrag an die Grafikkarte wie „Zeichne jetzt dieses Mesh“. Weniger Draw-Calls heißt weniger Arbeit für die CPU.
- **Schattenkarte**: ein Bild der Welt, wie die Sonne sie sieht. Daran erkennt das Spiel, was im Schatten liegt. Komets **Schatten in der Ferne** zeichnet Schatten bis zu deiner Sichtweite.
- **SSAO** (Umgebungsverdeckung): dunkelt Ecken und Spalten etwas ab, damit die Welt tiefer wirkt.
- **Gottesstrahlen**: Sonnenstrahlen, die durch Lücken fallen, zum Beispiel zwischen Bäumen.
- **Oberfläche, GUI**: alles, was über der Welt gezeichnet wird: Schnellleiste, Menüs, Chat, HUD.

### Arbeit sparen

- **Culling (Auslassen)**: weglassen, was du nicht sehen kannst, damit die Grafikkarte es nicht umsonst zeichnet.
- **Frustum-Culling**: lässt weg, was außerhalb deines Blickfelds liegt (hinter dir oder seitlich).
- **Verdeckungs-Culling**: lässt weg, was zwar in deinem Blickfeld liegt, aber hinter etwas anderem verborgen ist, zum Beispiel ein Tal hinter einem Berg. Komets **Verdecktes Gelände auslassen** macht das auf der Grafikkarte, nur unter Vulkan.
- **Tiefenpyramide**: ein kleines, grobes Bild davon, wie weit die Dinge auf dem Bildschirm entfernt sind. Komet prüft Chunks daran, um schnell zu erkennen, ob sie verdeckt sind.
- **Shader-Cache**: übersetzte Shader und Pipelines, auf der Festplatte gespeichert, damit der nächste Start sie nicht neu bauen muss. So gibt es weniger Ruckler, wenn Dinge zum ersten Mal erscheinen. Komet speichert sie mit **Vulkan: Shader speichern**.

## Komet intern

- **Funktion**: ein Teil von Komet, der eine Sache im Spiel ändert, zum Beispiel wie Chunks hochgeladen werden. Jede Funktion hat einen Zustand: aktiv, aus, zurückgehalten, zurückgetreten und so weiter.
- **Schalter, Knob**: eine Einstellung, die eine Funktion ein- oder ausschaltet oder festlegt, wie viel sie tut (ein Regler). Im Code heißen sie Knobs.
- **Voreinstellung**: ein fertiger Satz Werte für mehrere Abwägungen von Komet auf einmal. Du wählst sie unter Komet › Übersicht in der Zeile **Einstellungen**: **Automatisch (empfohlen)**, **Qualität**, **Ausgewogen** oder **Tempo**. Was du danach selbst änderst, bleibt erhalten, und die Zeile zeigt dann **Angepasst**. Mehr dazu auf der Seite „Das Optionsmenü“.
- **Exakte Optimierung**: ein Schalter, der dasselbe Ergebnis wie das Spiel liefert, nur schneller oder sparsamer. Diese Schalter sind versteckt, bis du Komet › Werkzeuge › **Erweiterte Optionen zeigen** einschaltest.
- **Harmony-Patch**: Komet und viele andere Mods ändern mit einer Bibliothek namens Harmony den Code des Spiels, während es läuft. Ein Patch ist eine solche Änderung an einer Methode (einem Stück Code des Spiels).
- **Zurückgetreten**: Patcht eine andere Mod dieselbe Methode, auf die eine Komet-Funktion baut, schaltet Komet diese Funktion ab und lässt das Spiel die Arbeit wie gewohnt erledigen. HUD und Log sagen es dir.
- **Zurückgehalten**: Eine andere Mod hat Komet gebeten, eine Funktion beim Verhalten des Spiels zu lassen. Der Schalter der Funktion ist dann gesperrt und nennt die Mod.
- **Engine-Fingerabdruck**: eine Prüfsumme über den Code des Spiels, den eine Funktion ersetzt. Hat ein Spiel-Update diesen Code geändert, passt der Fingerabdruck nicht mehr, und Komet installiert die Funktion nicht, damit nichts kaputtgeht.

## Messen

- **HUD**: Komets Messanzeige. Sie besteht aus dem Overlay und dem HUD-Fenster. Mehr dazu auf der Seite „HUD und Messen“.
- **Overlay**: die kleine Anzeige in einer Ecke des Bildschirms. F7 schaltet sie ein und aus. Was sie zeigt (FPS, Lows, Frametime, Graph, Mods), stellst du unter Komet › HUD ein.
- **HUD-Fenster**: ein Fenster mit Tabs wie Frames, Render und Log, das alle Messwerte genauer zeigt. Strg+F7 öffnet es.
- **Debug-Fenster**: ein Fenster, in dem du ein Debug-Protokoll aufnimmst, liest oder kopierst. Strg+F8 öffnet es.
- **Debug-Protokoll**: ein Textbericht über alles, was Komet während einer Aufnahme misst, mit den Log-Zeilen dieser Zeit. Du erstellst es im Debug-Fenster oder mit `.komet debug`. Es landet in der Zwischenablage und im Ordner `Logs/komet-debug`. Für Fehlerberichte hilft es sehr. Mehr dazu auf der Seite „Probleme lösen“.
- **Benchmark**: eine Messung über eine feste Zeit, gestartet unter Komet › Werkzeuge › **Benchmark** (die Länge stellst du mit **Benchmark-Dauer** ein). Die Mittelwerte landen danach in der Zwischenablage und im Client-Log.

## Modding

- **Shader-Pack**: ein Ordner mit einer `komet-shader.json` und Shader-Dateien, der eigene Bildeffekte und Regler mitbringt. Er liegt in `ModConfig/komet/shaders` oder in einer Mod. Jedes Pack bekommt eine eigene Seite unter Shader.
- **Shader-Mod**: eine Mod, die Shader des Spiels ersetzt oder eigene mitbringt. Komet zeigt sie unter Shader › Aktive Shader.
- **Vorlage**: ein fertiger Beispielordner, den Komet unter Modding › Vorlagen für dich anlegt: ein Shader-Pack, ein Stil, eine kleine Mod oder ein Shader-Paket für Vulkan. Du kannst ihn lesen, ändern und daraus lernen.
- **Stil**: das Aussehen dieses Menüs, ab Werk Pragmatic (Komet › Stil). Eigene Stile liegen in `ModConfig/komet/styles`.

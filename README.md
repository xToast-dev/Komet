# Komet 2.0.0 Nightlybuild - PRERELEASE!

Performance-Mod für den Client von Vintage Story 1.22 (C#, .NET 10). Komet ersetzt teure Stellen der Engine durch Wege mit
demselben Ergebnis, misst sich im Spiel selbst und zeigt ein HUD mit Frametimes, Lows, Spikes und den Zählern seiner Features.
Das Ziel sind die 1-%- und 0,1-%-Lows, nicht die durchschnittlichen FPS.

- **HUD**: F7 zeigt oder versteckt es, Umschalt+F7 schaltet die Detailzeilen. Panels: Frametime-Graph, System (Welt, CPU,
  Speicher), Render-Passes, Mod-Zeiten, Mods & Patches, Komet-Zähler, Haupt-Log, Debug-Log.
- **Einstellungen**: `.komet` im Chat. Seite „HUD“ (Anzeige, Panels, Messung, Version, Zurücksetzen), dazu „Rendern“, „Chunks“ und
  „Sonstiges“ mit den Schaltern der Features. Jede Feature-Seite endet mit „Aktuelle Werte“ (Zeilen der Panels in Zwischenablage
  und Client-Log) und einem Benchmark über die eingestellte Dauer (5–120 s). Gespeichert wird in `ModConfig/komet-hud.json`.
- **Update-Hinweis**: Nach Zustimmung beim ersten Start (änderbar unter „Update-Hinweise“) fragt Komet bei jedem Spielstart
  api.github.com nach einem neueren Build des eigenen Kanals; „Prüfsumme“ vergleicht die sha256 der Zip mit der veröffentlichten.

Komet läuft nur im Client (im Einzelspieler mit dem eingebauten Server im selben Prozess). Fehlt eine Engine-Stelle, die eine
Ersetzung braucht, rechnet die Engine, und Log oder HUD sagen es. Die vier Tesselier-Wege, AnimationFrames, InitOnce, ShapeInitMemo,
LightScratch und ColumnNoiseScratch treten auch zurück, wenn ein anderer Mod dieselben Methoden patcht; die übrigen prüfen das nicht.

## Features

Die Schalter stehen in `Komet/Core/Knobs.cs`. In Klammern: Name im Dialog und Standard; aus oder 0 heißt, die Engine rechnet.
Messmethoden: **A/B** = Mikro-Benchmark im selben Prozess, alt und neu im Wechsel, außerhalb des Spiels mit der echten Engine;
**golden** = Tests vergleichen Bit für Bit mit der Engine-Methode; **im Spiel** = `scripts/bench.sh`.

### Rendern (Seite „Rendern“)

- **FrustumSweep** („SIMD-Frustum-Culling“, an): Culling der Chunk-Meshes vektorisiert und mit x/z-Gitter, Ergebnis und Reihenfolge
  wie die Engine. A/B (41 Pools, ≈ 14 000 Einträge, kalter Cache): 0,97 statt 2,31–2,42 ms pro Frame. Ab 8 192 Einträgen pro Pass
  cullen die Komet-Worker-Threads (WorkerThreads) zusammen mit dem Main-Thread alle Pools eines `MeshDataPoolManager.Render` vorab,
  der Main-Thread zeichnet danach nur noch (golden gegen die Engine über 600 Frames Streaming); ohne Worker cullt der Main-Thread.
- **SunOcclusion** („Sonnen-Occlusion-Query entzerrt“, an): liest die Occlusion-Query der Sonne jeden vierten statt jeden Frame,
  denn jeder `glGet*` ist unter Mesa ein glthread-Sync. Nicht gemessen.
- **ShaderUseCache** („Shader-Uniform-Cache“, an): `Use` lädt nur geänderte Uniforms hoch statt aller. Garbage pro `Use` 208 B → 0
  (allein die Einstellungs-Lesezugriffe waren bei 118 `Use` pro Frame 1,8 MB/s); die gesparten GL-Uploads sind nicht gemessen.
- **IndirectDraw** („Indirect Draw“, an): Multi-Draws als indirekte Befehle aus einem gemappten GPU-Puffer statt als Client-Liste;
  ohne `GL_ARB_multi_draw_indirect` (etwa macOS) rechnet die Engine. Nicht gemessen.
- **WindowSizeCache** („Fenstergröße gecacht“, an): merkt sich die Fenstergröße, die die Engine ein Dutzend Mal pro Frame bei GLFW
  erfragt (unter X11 ein Roundtrip zum Display-Server). Nicht gemessen.
- **MeshPool** („SIMD-Chunk-Einbau, Fragmentierung inkrementell“, an): Einfügen und Entfernen in den Mesh-Pools ohne Lauf über alle
  Einträge, bitgleich. A/B: 1000 Entfernungen 28 → 0,8 ms; bei 30 neuen Meshes pro Frame Einbau (Median) 1,55/1,42 → 0,14/0,24 ms.
- **MeshRecycle** („Chunk-Mesh-Puffer wiederverwenden“, an): Recycelte Meshes behalten die Arrays ihrer Extradaten, die die Engine
  bei jedem Klon neu anlegt (nach der Analyse im Code zwei Drittel der Allokationen des Clients). Wirkung nicht gemessen.
- **UploadCap** („Zeitlimit für Chunk-Uploads pro Frame (0 = Engine, kein Limit)“, 3 ms, bis 20): Die normale Warteschlange stoppt
  nach 3 ms im Frame, Vorrang-Chunks nie. Das Budget der Engine kostet bei Sichtweite 1536 und Limiter 5 geschätzt 1,3 ms bei kurzer
  Warteschlange, 3 ms bei 100 und 6 ms bei 300 Chunks; das Limit greift also nur bei Rückstau (Teleport). Kein A/B im Spiel.

### Chunks und Tesselierung (Seite „Chunks“)

Beim Laden ist der Tesselier-Thread zu 96–99 % ausgelastet; im Trace waren 38 % von `NowProcessChunk` `BuildExtendedChunkData`,
je rund 14 % `CalculateVisibleFaces` und `CalcBlockFaceLight`. ExtendedRows, VisibleFaces, FaceLight und OccludedChunks sind golden
geprüft und installieren sich nur, wenn ein IL-Fingerabdruck der nachgebauten Methoden dem von 1.22.7 gleicht; nach einem
Spiel-Update und bei fremden Patches (alle 2 s geprüft) rechnet die Engine. Im Spiel sparen die vier gut ein Viertel pro Durchlauf.

- **ChunkLookup** („Chunk-Suche ohne Lock“, an): beantwortet `GetChunk` und `GetChunkAtBlockPos` aus einem Spiegel statt unter
  `chunksLock`, den alle Client-Threads nehmen; was fehlt, beantwortet die Engine. Im Spiel nicht gemessen.
- **DecompressScratch** („Chunk-Ebenen ohne Wegwerfkopie entpacken“, an): pro Ebene (2–6 Bitebenen) 8 256–24 880 → 40–280 B und
  1,5–4,4 → 0,4–1,3 µs; rund 8 MB/s weniger Garbage, solange Chunks ankommen.
- **TessSchedule** („Chunks nahe am Spieler zuerst tesselieren“, an): Die Engine tesseliert in Ankunftsreihenfolge, Komet den
  nächsten Chunk zuerst (gewichtet nach Blickrichtung); Budget und Vorrang bleiben die der Engine. Nahe Chunks warten halb so lange.
- **WorkerThreads** („Komet-Worker-Threads für Culling und Tesselierung“, Standard Kerne − 2, mindestens 1, höchstens 8; 0 = nur
  die Threads des Spiels): ein gemeinsamer Pool langlebiger Threads (`komet-worker-N`) statt eigener Tesselier-Threads und
  `Parallel.For`. Frame-Aufgaben haben Vorrang: das Vorab-Culling eines Render-Aufrufs verteilt der Main-Thread über einen gemeinsamen
  Zähler und arbeitet selbst mit; er wartet nie auf einen Thread, der gerade tesseliert, der hilft erst nach seinem Durchlauf. Sonst
  nimmt jeder Thread normale Durchläufe aus der Warteschlange von TessSchedule (nächster Chunk zuerst, ein Chunk nie auf zwei Threads
  zugleich) mit einem eigenen `ChunkTesselator`, der beim ersten Durchlauf auf dem Main-Thread entsteht und an den Thread gebunden
  bleibt; Vorrang-Chunks und Durchläufe, die die Engine wiederholen will, bleiben beim Tesselier-Thread der Engine. Tesselierung nur
  zusammen mit TessSchedule, und gleichzeitig nur auf **TessJobs** Threads (Standard 2, 0 = keiner): Acht zugleich brauchten im Spiel
  3,9 statt 1,5 ms pro Durchlauf und halbierten die 1-%-Lows im Flug. Erst ab 3 000 wartenden Chunks (Welt betreten, Teleport; im
  Flug wartete die Engine allein höchstens 2 600) tesselieren alle Threads, bis weniger als 500 warten. Für ein Frame weckt der
  Main-Thread nur so viele ruhende Threads, wie die Pools hergeben (einen je 4 096 Einträge über den ersten), ruhende Threads
  schlafen bis zu 50 ms statt alle 5 ms nachzusehen; im Test sank der CPU-Mehraufwand pro Culling-Frame von 36 auf 3 %. TessSafety macht, was die Engine zwischen Durchläufen teilt, pro Thread (Palettentabelle,
  Kreuz-Rotation) oder sperrt es (Block-Entities, Blöcke mit eigener `OnJsonTesselation`, Decor, Shapes, Sonnenlicht, Mesh-Recycler,
  Texturatlas, Klimakarte), gesperrt wird nur, solange Worker tesselieren. Tests: dieselbe Welt über den Engine-Tick mit und ohne vier
  Worker ergibt jedes Mesh bitgleich; ein Frame-Batch endet, auch wenn jeder Worker in einer langen Hintergrundaufgabe steckt. Im
  Spiel (Pool mit 8 Threads, noch ohne TessJobs, gegen 0 im selben Prozess): Warteschlange nach dem Betreten 37 statt ≈ 3 400,
  Ø 10,5 statt 13,8 ms, im Stehen bessere Lows (49,6 statt 42,6 FPS), im Flug deutlich schlechtere (1-%-Low 12,0 statt 21,0); die
  Begrenzung auf TessJobs ist die Antwort darauf und noch nicht gemessen.
- **ExtendedRows** („Chunks für den Tesselator zeilenweise kopieren“, an): kopiert Chunk und Nachbarschale 32 Zellen am Stück statt
  Zelle für Zelle, jeden Lock einmal pro Zeile. A/B: 400–600 statt 820–1 250 ns pro Zeile.
- **VisibleFaces** („Sichtbare Blockseiten ohne Fallunterscheidung pro Seite“, an): Bitformel für `FaceCullMode.Default`, sonst
  Engine-Logik. A/B pro Chunk: unter Tage 370–470 → 100–120 µs, Oberfläche 280–360 → 115–135 µs, mit Schnee 300–310 → 155–160.
- **FaceLight** („Licht der Blockseiten (weiche Schatten) ohne virtuelle Aufrufe“, an): Umgebungslicht aus Bits, vier Ecken zugleich,
  für Blöcke mit den AO-Methoden der Basisklasse (Vanilla: alle außer Mikroblöcken). A/B: 90–95 → 50–57 ns pro Seite.
- **OccludedChunks** („Rundum umschlossene Chunks nicht tesselieren“, an): Ein Chunk aus undurchsichtigen Blöcken, dessen Nachbarn
  zu ihm hin undurchsichtig sind, ergibt ohne Durchlauf 0 Vertices; 6 877 in 4 Minuten `smoke`, Zeit nicht einzeln gemessen.
- **LightScratch** („Licht ohne Wegwerfobjekte ausbreiten“, an): Blocklicht mit wiederverwendeten Objekten. Laterne setzen und
  entfernen 4 564 120 → 2 392 B und 10,7 → 9,5 ms (Testlauf).
- **ParticleLight** („Partikel-Licht ohne Warten auf den Chunk-Lock“, an): Partikel auf dem Render-Thread nehmen den Chunk-Lock nur,
  wenn er frei ist, sonst das zuletzt gelesene Licht. Beseitigt minutenlange 8–13-ms-Frames bei Bienenkörben (Messung unten).

### Entities (Seite „Sonstiges“)

- **AnimationFrames** („Animations-Frames gecacht“, an): Frames aus dem Cache, sonst schnell kompiliert, sonst von der Engine.
  A/B pro Animation: Treffer 14–85 µs gegen 50–275 µs der schnellen Kompilierung; warm „erel fly-idle“: Engine 146–159, schnell < 3 ms.
- **InitOnce** („Doppelte Shape-Initialisierung des Spielers überspringen“, an): lässt die zweite, gleiche Initialisierung der
  Spieler-Shape bei jeder Neutesselierung weg. Ohne belastbare Messung.
- **ShapeInitMemo** („Wiederholte Shape-Initialisierung unveränderter Entity-Shapes überspringen“, an): warm und im Wechsel `moose`
  0,49–0,51 → 0,09 ms, `wolf` 0,33 → 0,06 ms; golden über 880 animierte Vanilla-Shapes.
- **EntityTessBudget** („Budget für Entity-Tesselierung pro Frame (0 = Engine, alle auf einmal)“, 4 ms, bis 50): verteilt fällige
  Entity-Tesselierungen auf mehrere Frames (ein Spike von 23,7 ms war jede Entity in einem Frame). Kein A/B des Budgets.

### Allokationen und Start (Seite „Sonstiges“)

- **ClimateCache** („Klimakarten-Cache nach Sichtweite (statt fester 10 Regionen à 1 MiB)“, an): Bei Sichtweite 1536 erreicht der
  Tesselator 49 Regionen, jenseits von 10 verfehlt jede Abfrage und allokiert 1 MiB; Komet hält alle in Sicht (bis 144). Nicht gemessen.
- **ColumnNoiseScratch** („Geländerauschen mit wiederverwendeten Arrays berechnen“, an): nur Weltgenerierung im Einzelspieler;
  928 → 0 B pro Blocksäule, kein CPU-Gewinn.
- **CloudTileScratch** (nur Benchmark-Schalter, an): ≈ 34 000 Wolkenkacheln bauten alle 40 ms je ein `Vec3d`, dauerhaft 33 MB/s,
  etwa die Hälfte der Allokation eines ruhenden Clients nach dem Laden.
- **ChunkThreadClosure** (kein Schalter, Einzelspieler): Der Chunk-Thread des eingebauten Servers legt pro Anfrage eine Closure an,
  7,5 GB pro Weltbeitritt, ein Fünftel aller Allokation auf dem Heap, den der Client mitnutzt. Komet legt sie erst bei Bedarf an.
- **PreJit** („Engine beim Weltbeitritt vorab kompilieren (Hintergrund-Thread)“, an): Keine Spiel-DLL ist ReadyToRun, jede
  Engine-Methode wird beim ersten Aufruf übersetzt, für Renderer und Dialoge im Frame. Komet übersetzt API, Lib und Vanilla-Mods vorab
  auf dem Thread `komet-prejit` (Linux: nice 10), ohne etwas auszuführen. Außerhalb des Spiels (je 3 frische Prozesse im Wechsel):
  ≈ 42 000 Methoden in 3,7–3,9 s, bis +79 MB; JIT auf dem Main-Thread für 16 SVG-Slot-Icons 54–55 → 6 ms. Im Spiel nicht gemessen.

### Diagnose (keine Feature-Schalter)

- **TessAccounting**: Prefix und Postfix auf `TesselateChunk`, immer installiert, buchen jeden Durchlauf nach Art (voll, Nur-Rand,
  Vorrang, leer, zurückgestellt, ohne Vertex), solange die Zähler laufen (unten); daher `tessMsPerPass`, in jedem Arm. Buchung pro
  Durchlauf 20,5 → 4,6–6,9 ns (A/B alte gegen neue Zählung); die Zeitmessung selbst (zwei Zeitstempel, Hooks) ist nicht gemessen.
- HUD und Benchmark lesen dieselbe Frame-Uhr. 1-%-Low = 1000·k / (Summe der k längsten Frametimes in ms) mit k = n/100, beim
  0,1-%-Low k = n/1000. Spikes ordnet das HUD einer Ursache zu: GC, JIT, Warten auf einen Kern, zwischen den Frames, teuerste Marke.
- „Mod-Zeiten“ patcht Render-Stufen und Tick-Listener erst beim ersten Einschalten und misst nur jeden achten Frame (hochgerechnet);
  jeden Frame zu stoppen kostete bei tausenden Block-Entity-Renderern ein Viertel des Main-Threads. Die Feature-Zähler zählen nur, solange
  „Komet-Zähler“ sichtbar ist oder ein Benchmark läuft, und zeigen Schnitte über etwa 1 s.

## Gemessene Entscheidungen

### Partikel-Licht

Bei rund 150 Bienen-Partikeln schlief der Render-Thread in etwa einem Drittel der Benchmark-Läufe sekunden- bis minutenlang
8–13 ms pro Frame in `Monitor.Enter`; im Thread-Zeit-Trace waren 76 % von `particles-tick` Warten auf den Chunk-Lock. Auch
`TryEnter` dreht (8–11 ms in `TryEnter_Slowpath`), daher 20 ms Pause pro belegtem Chunk. 2560×1440, alle Panels, Server-GC 4 MiB,
je 6 getrennte Starts, denn der Stau kommt zufällig, höchstens einmal pro Lauf, und macht fast die ganze Streuung ohne Fix aus:

| | ohne | mit |
|---|---|---|
| 1-%-Low | 45,6 FPS (36,6–50,2) | 50,2 FPS (49,4–51,4) |
| 0,1-%-Low | 37,1 FPS | 40,9 FPS |
| Frames über 25 ms pro Minute | 28,1 (1,2–156) | 1,1 (0,6–1,8) |
| 1-%- / 0,1-%-Low beim Drehen | 54,4 / 50,3 FPS | 69,7 / 65,1 FPS |
| Starts mit Partikel-Stau | 3 von 6 | 0 von 6 |

### Tesselierung im Spiel

Ein inzwischen entfernter Prüfmodus rechnete im Spiel beide Wege (`smoke`, etwa 4 Minuten): 70,5 Mio. Chunkzeilen, 55 891 Chunks,
116 Mio. Seiten und Blöcke, 6 877 umschlossene Chunks, keine Abweichung. Tempo bei Sichtweite 1536, Server-GC, die vier an gegen aus:

| | Engine | Komet |
|---|---|---|
| Laden: Warteschlange nach dem Betreten wieder unter 50 (zwei Starts je Seite) | 72,5 / 74,2 s | 54,4 / 54,8 s |
| Laden: Höchststand der Warteschlange | 22 767 / 22 790 | 13 216 / 14 137 |
| Laden: Zeit pro Tesselierung | 2,21 / 2,29 ms | 1,65 / 1,68 ms |
| Flug in neues Gelände, Zeit pro Tesselierung (ein Prozess, im Wechsel) | 2,57–2,59 ms | 1,85–2,02 ms |
| Rückflug, Zeit pro Tesselierung | 1,95–1,98 ms | 1,38–1,47 ms |

Eine Tesselierung kostet gut ein Viertel weniger, die Welt steht nach dem Betreten rund 19 s früher. Leer wird die Warteschlange
nie: Wachsende Pflanzen und tickende Blöcke machen 25–40 Nachtesselierungen pro Sekunde, mit und ohne Komet.

### Reihenfolge der Tesselierung (TessSchedule)

A/B im selben Prozess am 25.09.2026, Engine-Reihenfolge gegen „nah zuerst“, 12 Runden plus 2 zum Aufwärmen, ABBA, Stand vor dem
Umbau. Build-Agenten liefen parallel, die absoluten FPS sind deshalb niedrig.

| Wartende Chunks im Umkreis von 6 Chunks (Mittel) | Engine | nah zuerst |
|---|---|---|
| Hinflug | 3,85 | 2,10 |
| Rückflug | 4,07 | 2,09 |
| Einschwingen nach dem Rundenwechsel | 19,7 | 4,9 |

Frametime Ø −1,12 ms, p99 −4,1 ms, 1-%-Low +2,3 FPS, 0,1-%-Low +2,4 FPS, Frames über 25 ms −115 pro Minute, keine Differenz
signifikant; die Zeit pro Tesselierung blieb gleich. Lows nicht schlechter, nahe Chunks warten halb so lange: TessSchedule bleibt.

### Server-GC im Spiel

`scripts/bench.sh`, Sichtweite 1536, je zwei Runden Stehen, Drehen und Flug mit 11 Blöcken/s, vier Starts Workstation – Server –
Server – Workstation, Komet 2.0.0 mit allen Features. Je Zelle beide Starts; das Working Set blieb bei 6,3–6,4 GB.

| | Workstation (Standard) | Server-GC, 4 Heaps, 8 MiB |
|---|---|---|
| 1-%-Low, gesamt | 45,4 / 43,5 FPS | 57,8 / 57,6 FPS |
| 0,1-%-Low, gesamt | 30,9 / 29,7 FPS | 40,9 / 42,2 FPS |
| Frames über 25 ms pro Minute | 11,1 / 15,3 | 0,8 / 1,7 |
| Frametime Ø | 8,95 / 9,13 ms | 8,69 / 8,61 ms |
| GC-Pause, Flug in neues Gelände (45 s) | 2,50 / 2,58 s | 0,63 / 0,66 s |
| 1-%-Low im Stehen | 81 / 89 FPS | 100 / 99 FPS |

Budget und Heaps (gleicher Flug, 1920×1080, zwei Runden je Start, 14 Starts in den Reihenfolgen A B C D D C B A und B E F F E B):

| Server-GC | 4 Heaps, 8 MiB | **4 Heaps, 4 MiB** | 4 Heaps, 2 MiB | 6 Heaps, 8 MiB | 6 Heaps, 4 MiB |
|---|---|---|---|---|---|
| 1-%-Low, gesamt | 60,3 / 51,6 | 64,4 / 60,7 / 64,4 / 65,2 | 49,7 / 65,6 | 61,5 / 63,8 | 62,1 / 61,5 |
| 0,1-%-Low, gesamt | 45,8 / 44,1 | 51,3 / 50,8 / 50,7 / 52,6 | 45,5 / 52,1 | 43,8 / 42,4 | 52,1 / 45,8 |
| Frames über 25 ms pro Minute | 0,8 / 0,6 | 0,3 / 0,0 / 0,3 / 0,0 | 0,0 / 0,0 | 0,6 / 1,1 | 0,0 / 0,8 |
| längste gen1-Pause | 13,8 / 32,8 ms | 7,2–10,2 ms | 5,3 / 9,2 ms | 12,8 / 14,8 ms | 11,3 / 10,0 ms |
| GC-Pause gesamt | 397–408 ms/min | 444–468 ms/min | 593–603 ms/min | 300–308 ms/min | 339–354 ms/min |

Mit 4 MiB kommen die GCs doppelt so oft und befördern je halb so viel: Die längste Pause halbiert sich, die Pausenzeit steigt um
gut 10 %, alle vier Starts lagen über allen mit 8 MiB. 2 MiB kostet ein weiteres Drittel Pausenzeit und schwankt stärker, 6 Heaps
senken die Pausenzeit, nicht die Spitzen. `DOTNET_GCGen1MaxBudget=0x400000` mit 8 MiB gen0 brachte nichts (0,1-%-Low 46,9 / 42,3).

### Refactor 2026-09: Stand davor gegen danach

Vier Starts im Wechsel (alt, neu, neu, alt), je 150 s Laden, 5 Runden A/A, Workstation-GC, 1920×1080. Der alte Build lief mit seinem
eigenen Harness (Frametime aus dem `dt` der Engine), der neue mit dem neuen (Frametime aus der `FrameClock`); sonst gleiche Route und
Dauer. Werte: Mittel der zwei Starts je Build, in Klammern die beiden Starts.

| gesamt | vorher | nachher |
|---|---|---|
| Frametime Ø | 11,53 ms (11,79 / 11,26) | 11,24 ms (11,32 / 11,17) |
| p99 | 22,86 ms | 22,21 ms |
| 1-%- / 0,1-%-Low | 37,9 / 28,7 FPS | 39,6 / 30,0 FPS |
| Frames über 25 ms pro Minute | 28,1 (29,9 / 26,4) | 21,4 (27,5 / 15,3) |
| Rückflug 1-%- / 0,1-%-Low | 37,7–38,9 / 27,8–30,0 FPS | 40,6–40,7 / 31,7–31,8 FPS |
| Hinflug über 25 ms pro Minute | 49–54 | 27–42 |
| Allokation im Stehen | 20 MB/s | 26 MB/s (CookingScratch/ClimateEvents entfernt, siehe unten) |

Beim Drehen und Stehen lagen die Lows im Bereich der Streuung (ein Neu-Start hatte im Stehen eine Spike-Serie). Flug und Gesamtwert
sind in beiden Neu-Starts besser als in beiden Alt-Starts.

## Entfernt und warum

- **EmptyArrivalNoEdge** (keine Randdurchläufe um leere Chunks, war aus): Die Prüfung im Spiel fand 17, die nicht überflüssig waren.
- **SendRadiusResume** (Senderadius nach Chunkwechsel fortsetzen, war aus): 4–19 % mehr Chunks pro Hinflug, dafür rund 3 FPS weniger
  bei 1-%- und 0,1-%-Low (ein Prozess, Flug mit 11 Blöcken/s).
- **MainThreadBudget** mit TaskMarks (war an, 4 ms): A/B im selben Prozess gegen Abarbeiten: Ø −0,02 ms, 1-%-Low +0,8, 0,1-%-Low
  −0,04, über 25 ms −0,28/min, nichts signifikant. Die Warteschlange baut die Welt auf; Drosseln ließ das Wettersystem abstürzen.
- **`KOMET_TESS_VERIFY`** (Schattenmodus): im Spiel ohne Abweichung; Golden-Tests und IL-Fingerabdrücke bleiben als Schutz.
- **ParticleCensus** (`KOMET_PARTICLE_CENSUS`): Frage beantwortet, die Partikel-Spikes kamen vom Lock, nicht von der Anzahl.
- **MeshUpload**: reine Diagnose, aber immer gepatcht.
- **CookingScratch**: nur Feuerstellen-Ticks des Servers, 2 208 → 1 152 B, aber 2 872 → 2 990 ns pro `Matches`.
- **ClimateEvents**: 0,7 MB/s, etwa 2 % des Garbage im Leerlauf, für 383 Zeilen.
- Beide zusammen im Spiel (alter Build, gleicher Prozess, 4 Runden im Wechsel, Welt `testwelt`): aus statt an kostet 5,2–6,8 MB/s
  mehr Allokation auf Hintergrund-Threads (Stehen 20,5 → 27,0 MB/s, Hinflug 50,2 → 55,8 MB/s) und 5–10 % mehr GC-Pause, aber keine
  messbaren Lows: Frametime +0,03 ms, 1-%-Low +0,4, 0,1-%-Low +1,1 FPS, über 25 ms −2,8/min (alles im Rauschen). Wer viele
  Feuerstellen betreibt und Garbage sparen will, findet beide im Stand vor dem Refactor.
- **GcEvents**: Forschungswerkzeug; GC-Zeilen im HUD, GC-Anteil des schlimmsten Frames und Spike-Ursache „GC-Pause“ decken es ab.
- **A/B-Unterschalter**: FrustumSweep.Buckets/Batched/Incremental, ShaderUseCache.SettingsPerFrame, PoolFragments.SqueezeSkip,
  ExtendedRows.Faces, Hud.Stagger/Lean, EntityTessBudget.Enabled (0 ms ist die Engine), FaceLight.Fused, ChunkBudget.Limiter (in
  Vanilla: „Begrenzung der Chunk-Upload-Rate“), FrameClock- und GcEvents-Schalter. Fest bleibt der bisherige Standardweg.
- **TessAccounting-Schalter** (war an): Die Zähler sind Messwerkzeug; ohne Schalter hat auch der Arm `"engine": true` `tessMsPerPass`.
- **LightScratch für Sonnenlicht**: 3,8 der 26,8 MB/s Ersparnis im Flug in neues Gelände; der verwickeltste Code der Scratch-Features.
- **ExtendedRows `GetOne` und Null-Fill**: Einzelzellen brauchten 86 statt 21 ns der Engine; die Suche nach leeren Zellen
  10,2–10,9 → 3,4–4,1 µs pro Chunk, etwa 0,4 % eines Durchlaufs von 1,65 ms.

## Optionale Starteinstellungen

Einige Stellschrauben liest .NET nur beim Programmstart aus Umgebungsvariablen. `run.sh`, `.desktop`-Datei und
`Vintagestory.runtimeconfig.json` überschreibt jedes Update; also ein eigenes Skript wie `~/.local/bin/vintagestory-komet` (ausführbar):

```bash
#!/bin/bash
export DOTNET_TC_CallCountingDelayMs=0
exec /opt/vintagestory/run.sh "$@"
```

Oder `Exec=env VARIABLE=WERT … /opt/vintagestory/run.sh` in einer Kopie von `Vintagestory.desktop` in `~/.local/share/applications/`,
unter Windows eine `.bat` mit `set`. Prüfen: `tr '\0' '\n' < /proc/$(pidof Vintagestory)/environ | grep DOTNET_`.

### `DOTNET_TC_CallCountingDelayMs=0`

.NET übersetzt jede Methode zuerst unoptimiert (Tier 0) und optimiert sie nach 30 Aufrufen (Tier 1), zählt aber erst, wenn 100 ms
lang keine neue Methode übersetzt wurde. Solange beim Laden laufend neue dazukommen, auch vom Server und von PreJit, bleibt aller
heiße Code unoptimiert; mit `0` wird sofort gezählt. Synthetisch: Tier 0 ≈ 265 ns pro Aufruf, Tier 1 60 ns; kam alle 50 ms eine
neue Methode dazu, blieb die heiße ohne die Variable 3 s auf Tier 0, mit ihr war sie nach 250 ms optimiert (im Spiel nicht gemessen).
Kosten: beim Laden mehr Optimierungen (im Test 32–340 statt 4 Methoden), ein paar MB Code. Nicht verwenden: `DOTNET_TieredCompilation=0`
(Engine 11,4 statt 4,0 s, Methoden bis 37 statt 11 ms), `DOTNET_TC_QuickJitForLoops=0` (6,8 s, bis 27 ms), `DOTNET_TieredPGO=0`.

### Server-GC mit gedeckeltem Budget

```bash
export DOTNET_gcServer=1 DOTNET_GCHeapCount=4 DOTNET_GCDynamicAdaptationMode=0 \
       DOTNET_GCGen0MaxBudget=0x400000 DOTNET_GCNoAffinitize=1
```

Das Spiel nutzt den Workstation-GC: Nach je 8 MB Allokation aus irgendeinem Thread hält ein gen0-GC alle Threads an, und 60–75 %
jeder Pause ist Card-Marking auf einem einzigen Thread. Der Server-GC verteilt das auf 4 GC-Threads, sein Budget gilt pro Heap.
`GCDynamicAdaptationMode=0` schaltet DATAS aus, das das Budget sonst wachsen lässt, `GCGen0MaxBudget` deckelt gen0 pro Heap auf
`0x400000` (hexadezimal: 4 MiB), `GCNoAffinitize=1` bindet die GC-Threads nicht an die Kerne 0–3. Messungen: siehe oben.

- Schreibweise beachten: `DOTNET_GCGen0MaxBudget`; `DOTNET_GCgen0MaxBudget` ignoriert Linux ohne Meldung.
- Nie ohne Deckel und nie mit DATAS: Im synthetischen Modell (124 MB/s) wuchs das Budget dann auf 38–800 MB, die Pausen dauerten
  10–60 ms statt 2,2–5,1 ms beim Workstation-GC. Speicher: im Modell +50 MB, im Spiel kein messbarer Unterschied.
- `DOTNET_GCgen0size` allein macht die GCs des Workstation-GC seltener, aber länger (Modell, 32 MiB: 4-mal weniger, je 3- bis
  4-mal länger, Pausenzeit fast gleich), für die Lows also schlechter.

### Spieleinstellung „RAM optimieren“ (`optimizeRamMode`)

- **„Aggressiv optimieren“ (2) nicht verwenden.** Das Spiel fordert dann alle 602 s (ohne Fensterfokus alle 30 s) einen
  blockierenden GC samt Kompaktierung des Large Object Heap an, sobald dieser 512 MB über seinem Minimum liegt. Kommt er durch
  (nur bei weitgehend verbrauchtem gen2-Budget), steht das Bild geschätzt mehrere hundert ms; entpackte Chunks bleiben nur 4 s.
- **„Etwas optimieren“ (1, Standard)** fordert im Spielablauf keinen GC an.
- **Modus 0** gibt es nur in `clientsettings.json` (`"optimizeRamMode": 0`, bei geschlossenem Spiel): Chunks bleiben 80 statt 8 s
  entpackt, der Chunk-Datenpool hält 10 000 statt 4 000 Puffer, einige hundert MB mehr RAM. Nicht gemessen.

## Benchmark im Spiel

`scripts/bench.sh` startet das Spiel unbeaufsichtigt in einem Wegwerf-Ordner unter `~/.cache/komet-bench/runs/` auf einer Kopie
des Spielstands aus `scripts/bench.json`, fliegt eine feste Route (Stehen, 360°-Drehung, Hin- und Rückflug) und schreibt `result.json`
und `frames.csv`. Der eigene Datenordner wird nur gelesen; die kopierte `clientsettings.json` (Anmeldung) wird nach dem Lauf gelöscht.

```bash
./build.sh && scripts/bench.sh --profile smoke        # prüft die Messkette, etwa 4 Minuten mit Laden
scripts/bench.sh --profile full                        # Komet gegen alle Schalter auf Engine, etwa 25 Minuten
scripts/bench.sh --profile aa --mod ../alt/Releases/komet --env DOTNET_TC_CallCountingDelayMs=0
```

- **Profile**: `smoke` (kurz, A/A), `aa` (volle Länge, A/A: die Streuung, die jeder A/B schlagen muss), `full` (Komet gegen
  `"engine": true`), `tess` (vier Tesselier-Wege und TessSchedule an gegen aus), `gcreg` (ShapeInitMemo, EntityTessBudget und
  ParticleLight an gegen aus: Frametime und GC-Pausen), `hud` (alle Panels, eine Runde: Cairo, GL, Frame-Uhr).
- **Arme** laufen im selben Prozess in gespiegelten Blöcken (A B B A …) und setzen Schalter mit ihren Namen aus `Knobs.cs`
  (`true`/`false`, `UploadCap` 0–20, `EntityTessBudget` 0–50); `"engine": true` setzt alle auf den Engine-Wert. Standards
  (`BenchConfig.cs`): Einschwingen 150 s, dann 10 s pro Runde; Stehen 10 s, Drehen 20 s, Strecke 40 s bei 11 Blöcken/s; 8 Runden.
- **`--mod DIR`** misst einen anderen Build-Ordner (Standard `Releases/komet`); zwei Builds im Wechsel starten, A B B A, gleiches
  Profil. **`--env N=V`** setzt Umgebungsvariablen; Starteinstellungen wie der GC-Modus brauchen abwechselnde Starts, keine Arme.
- **`result.json`** (Schema `komet-bench/2`) hat pro Segment `frametime` (avgMs, p99Ms, low1Fps, low01Fps, over25PerMin …), `gc`,
  `chunks` (Warteschlangen, `tessNearAvg`, `tessMsPerPass`), `cpu` und die teuersten Spikes mit GC, JIT, Wartezeit und Marken;
  `deltas` gibt mean, sd und se gegen Arm 0. **`frames.csv`** hat eine Zeile pro Frame.

Fallen:
- Das Spiel muss geschlossen und der Desktop entsperrt sein, sonst hängt es beim Öffnen des Fensters (das Skript verweigert, `--force`
  übergeht). Ohne Bildschirm: `kwin_wayland --virtual --xwayland --no-lockscreen --width 1920 --height 1080 --exit-with-session
  skript.sh`, wobei `skript.sh` `scripts/bench.sh … --force` aufruft.
- Fremdlast verfälscht die Frametimes: Ein anderes Spiel machte GC-Pausen um 40 % länger und Frames über 25 ms zehnmal häufiger,
  in beiden Armen. Nur Vergleiche im selben Prozess überstehen das; Builds und Tests parallel nur mit `nice -n 19`.
- Nie die Spielzeit anhalten: Partikel altern mit dem Kalender und sterben mit `/time stop` nicht mehr. Der Benchmark setzt pro
  Runde `/time set 10:00`. Der erste Hinflug erzeugt neues Gelände, spätere laden bestehendes: nur Gleiches vergleichen.

## Bauen

Voraussetzungen: .NET SDK 10 (`global.json`), Vintage Story unter `VsInstall` (Standard `/opt/vintagestory`, sonst `VsInstall=…`).

```bash
./build.sh                                   # = dotnet build Komet/Komet.csproj -c Release -t:Package
VsInstall=/pfad/zu/vintagestory ./build.sh
dotnet build Komet/Komet.csproj -c Release    # kompilieren und prüfen, ohne Paket
dotnet run --project CakeBuild                # dasselbe über Cake (--configuration=Debug: nur kompilieren)
```

Unter Windows `.\build.ps1`. `Package` legt den Mod als Ordner `Releases/komet/` ab (dort lädt ihn `scripts/bench.sh`) und packt
`Releases/komet_<version>.zip`. Jede Warnung ist ein Fehler (NetAnalyzers, Roslynator, Sonar), und in jedem Build prüft der
Roslyn-Analyzer `Komet.Rules`:
- KR0001: keine `while`- und `do`-Schleifen.
- KR0002: kein `goto`.
- KR0003: keine Präprozessor-Direktiven.
- KR0004: jede `for`-Schleife mit Konstante, Länge eines statischen Arrays oder `Math.Min(x, GRENZE)` als Grenze.
- KR0005: `foreach` nur über `Contracts.Bounded(...)`, einen Collection-Ausdruck oder einen Array-Initialisierer.
- KR0006: keine Rekursion.
- KR0007: keine wechselseitige Rekursion.
- KR0008: keine Funktion länger als 60 Zeilen.
- KR0009: Assertion-Dichte über ganz Komet mindestens 2,0 (`Assert`, `NotNull`, `Finite`, `Index`, `Bounded` pro Funktion).
- KR0010: jeder Lang-Schlüssel, den der Code als Literal nennt, steht in jeder Sprachdatei; zur Laufzeit zusammengesetzte bleiben ungeprüft.
- KR0011: alle Sprachdateien haben dieselben Schlüssel.
- KR0012: kein Platzhalter in einem Text, den das HUD ohne Argumente zeichnet.
- KR0013: jeder Schalter auf einer Einstellungsseite hat seine Schlüssel `settings-<name>`, `settings-page-<seite>` und `settings-<gruppe>`.

**Tests**: `dotnet test Komet.Test`, nur lokal, etwa 1,5 Minuten; sie brauchen die Spielinstallation samt `Mods/VSEssentials.dll`,
`Mods/VSSurvivalMod.dll` und Assets. Ein Test prüft alle JSON-Assets und `modinfo.json` auf gültiges JSON ohne doppelte Schlüssel.

**CI** (`.github/workflows/build.yml`; Push auf `main` oder `nightly`, Pull Requests, manuell): prüft die Spiel-DLLs in
`.github/vintagestory/` gegen `SHA256SUMS`, ruft `./build.sh` auf und schreibt die sha256 der Zip in die Zusammenfassung, ohne Tests.
`main` (auch manuell gestartet) erzeugt einen Release-Entwurf `v<version>` (ein vorhandener Tag wird nie verschoben), `nightly` und
manuelle Läufe anderer Branches ein Prerelease `preview-<sha>` (die neuesten drei bleiben); Pull Requests veröffentlichen nichts.

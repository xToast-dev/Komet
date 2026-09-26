# Komet 2.0.0 Nightlybuild - PRERELEASE!

Performance-Mod für den Client von Vintage Story 1.22 (C#, .NET 10). Komet ersetzt teure Stellen der Engine durch Wege mit
demselben Ergebnis, misst sich im Spiel selbst und zeigt ein HUD mit Frametimes, Lows, Spikes und Zählern. Ziel sind die 1-%- und
0,1-%-Lows, nicht die durchschnittlichen FPS.

- **HUD**: F7 an/aus, Umschalt+F7 Detailzeilen. Panels: Frametime-Graph, System, Render-Passes, Mod-Zeiten, Mods & Patches,
  Komet-Zähler, Haupt-Log, Debug-Log.
- **Optionsmenü**: ersetzt im Spiel die Einstellungen (Escape → Einstellungen) durch ein Menü wie Sodium in Minecraft; auch über
  `.komet`. Details unten. Komets Einstellungen stehen in `ModConfig/komet-hud.json`.
- **Update-Hinweis**: nach Zustimmung prüft Komet beim Start auf GitHub, ob es einen neueren Build des eigenen Kanals gibt.

Komet läuft nur im Client (im Einzelspieler samt eingebautem Server). Fehlt eine Engine-Stelle oder patcht ein anderer Mod dieselbe
Methode, rechnet die Engine, und Log oder HUD sagen es.

## Features

Schalter in `Komet/Core/Features.cs`; aus oder 0 heißt: die Engine rechnet. Alle Features sind standardmäßig an.

**Rendern**
- **FrustumSweep**: Chunk-Culling vektorisiert, ab 8 192 Einträgen auf den Worker-Threads; 0,97 statt 2,3 ms pro Frame.
- **SunOcclusion**: Occlusion-Query der Sonne nur jeden vierten Frame (unter Mesa ist jeder `glGet*` ein Sync).
- **ShaderUseCache**: `Use` lädt nur geänderte Uniforms hoch; 208 B → 0 Garbage pro Aufruf.
- **IndirectDraw**: Multi-Draws als indirekte Befehle aus einem gemappten GPU-Puffer (braucht `GL_ARB_multi_draw_indirect`).
- **WindowSizeCache**: Fenstergröße gecacht statt ein Dutzend GLFW-Abfragen pro Frame.
- **MeshPool**: Einfügen/Entfernen in Mesh-Pools ohne Lauf über alle Einträge; 1000 Entfernungen 28 → 0,8 ms.
- **MeshRecycle**: recycelte Meshes behalten ihre Extradaten-Arrays.
- **UploadCap** (3 ms, 0–20): Chunk-Uploads pro Frame begrenzt, Vorrang-Chunks nie.

**Chunks und Tesselierung** — ExtendedRows, VisibleFaces, FaceLight und OccludedChunks sind bitgleich zur Engine geprüft und
installieren sich nur bei passendem IL-Fingerabdruck (1.22.7). Zusammen: ein Viertel weniger Zeit pro Tesselierung, die Welt steht
nach dem Betreten rund 19 s früher.
- **ChunkLookup**: `GetChunk` aus einem Spiegel statt unter `chunksLock`.
- **DecompressScratch**: Chunk-Ebenen ohne Wegwerfkopie entpacken; rund 8 MB/s weniger Garbage beim Laden.
- **TessSchedule**: nächster Chunk zuerst (gewichtet nach Blickrichtung); nahe Chunks warten halb so lange.
- **WorkerThreads** (Kerne − 2, 1–8) und **TessJobs** (2, 0–8): gemeinsamer Pool `komet-worker-N` für Culling und Tesselierung.
  Frame-Aufgaben haben Vorrang; ab 3 000 wartenden Chunks tesselieren alle Threads. TessSafety macht geteilten Engine-Zustand pro
  Thread oder sperrt ihn, solange Worker tesselieren.
- **ExtendedRows**: Chunk und Nachbarschale zeilenweise kopieren; 400–600 statt 820–1 250 ns pro Zeile.
- **VisibleFaces**: Bitformel für `FaceCullMode.Default`; pro Chunk 370–470 → 100–120 µs unter Tage.
- **FaceLight**: Umgebungslicht aus Bits, vier Ecken zugleich; 90–95 → 50–57 ns pro Seite.
- **OccludedChunks**: rundum umschlossene Chunks werden nicht tesseliert.
- **LightScratch**: Blocklicht mit wiederverwendeten Objekten; Laterne setzen/entfernen 4,5 MB → 2,4 KB.
- **ParticleLight**: Partikel nehmen den Chunk-Lock nur, wenn er frei ist; beseitigt minutenlange 8–13-ms-Frames bei Bienenkörben
  (1-%-Low 45,6 → 50,2 FPS, Frames über 25 ms 28 → 1 pro Minute).

**Entities**
- **AnimationFrames**: Animations-Frames aus dem Cache, sonst schnell kompiliert, sonst von der Engine.
- **InitOnce**: doppelte Shape-Initialisierung des Spielers überspringen.
- **ShapeInitMemo**: wiederholte Initialisierung unveränderter Entity-Shapes überspringen; `moose` 0,5 → 0,09 ms.
- **EntityTessBudget** (4 ms, 0–50): Entity-Tesselierungen auf mehrere Frames verteilen.

**Allokationen und Start**
- **ClimateCache**: Klimakarten-Cache nach Sichtweite statt fester 10 Regionen à 1 MiB.
- **ColumnNoiseScratch**: Geländerauschen ohne Allokation (nur Einzelspieler-Weltgenerierung).
- **CloudTileScratch**: Wolkenkacheln ohne `Vec3d` pro Kachel; spart 33 MB/s.
- **ChunkThreadClosure** (kein Schalter): der Chunk-Thread des eingebauten Servers legt keine Closure pro Anfrage mehr an.
- **PreJit**: übersetzt API, Lib und Vanilla-Mods beim Weltbeitritt auf einem Hintergrund-Thread vorab.

**Diagnose**: TessAccounting bucht jeden Tesselier-Durchlauf nach Art. HUD und Benchmark lesen dieselbe Frame-Uhr;
1-%-Low = 1000·k / (Summe der k längsten Frametimes in ms), k = n/100.

## Optionsmenü und API für andere Mods

Escape → Einstellungen öffnet Komets Optionsmenü im Stil von Sodium über den ganzen Bildschirm:
- **Aufbau:** oben die Suche über alle Optionen; links die Seitenleiste mit **Vintage Story** (Allgemein, Qualität, Leistung, Maus,
  Steuerung, Barrierefreiheit, Ton, Oberfläche, Entwickler), **Komet** (HUD, Werkzeuge, Rendern, Chunks, Sonstiges) und den Seiten anderer Mods.
- **Liste:** in der Mitte alle Seiten des gewählten Abschnitts untereinander, rechts die Beschreibung der Option unter der Maus.
- **Übernehmen:** Änderungen werden gesammelt und erst mit **Übernehmen** oder **Fertig** wirksam (Shader werden dabei nur einmal
  neu geladen); Escape verwirft sie.
- **Auswahlen:** mit wenigen Möglichkeiten wechselt ein Klick weiter (Rechtsklick zurück); mit vielen, etwa der Sprache (gilt nach
  einem Neustart), öffnet ein Klick rechts ein Fenster mit allen Einträgen untereinander.
- **Steuerung:** alle Tastenbelegungen und Mausaktionen des Spiels auf einer Seite; Klick und dann die neue Taste belegt neu
  (Escape bricht ab), Rechtsklick stellt die Standardbelegung her, doppelt belegte Tasten stehen in Rot. Wirkt sofort, wie im Spiel.
- **Original-Grafikmenü:** als Schaltfläche am Ende der Seite Oberfläche.
- **Zurück aufs Spielmenü:** mit dem Schalter `GraphicsMenu` (Sonstiges → Optionsmenü), und automatisch, wenn ein Spiel-Update einen
  der nachgebauten Reiter ändert oder ein anderer Mod ihn patcht.

Ein Mod bindet sich ein, indem er `Komet.dll` referenziert (`Private="false"`) und in `StartClientSide` seine Seite anlegt, aus
einer eigenen Klasse, die er nur bei `api.ModLoader.IsModEnabled("komet")` aufruft:

```csharp
KometOptions.Page("mymod", Lang.Get("mymod:title"))
    .Group(Lang.Get("mymod:rendering"))
    .Switch(Lang.Get("mymod:water"), () => config.Water, on => config.Water = on, Lang.Get("mymod:water-hint"))
    .Slider(Lang.Get("mymod:detail"), 1, 8, 1, () => config.Detail, v => config.Detail = (int)v)
    .Choice(Lang.Get("mymod:mode"), ["A", "B", "C"], () => config.Mode, i => config.Mode = i)
    .Button(Lang.Get("mymod:reset"), Lang.Get("mymod:do-reset"), config.Reset);
```

Speichern übernimmt der Mod in seinen Settern; Komet ruft sie beim Übernehmen auf. Der dritte Parameter von `Page` ist der Name in
der Seitenleiste (Standard: der Titel); heißt er wie der Mod, steht dessen Version darunter. `Format(...)` und `EnabledWhen(...)`
gelten für die zuletzt angelegte Zeile. `KometOptions.Applied` meldet jedes Übernehmen. Beim Schließen der Welt verwirft Komet alle
Seiten und Abonnenten.

### Features abfragen, anhalten und eigene anmelden

`KometFeatures` (gleiche Regeln wie `KometOptions`) kennt jedes Feature unter seiner Id: Komets wie oben, die anderer Mods als
`modid:name`. `Snapshot()` listet alle in Installationsreihenfolge, `StateOf(id)` liefert `Pending`, `Active`, `Off` (vom Spieler
aus), `HeldOff`, `StoodDown` (ein anderer Mod patcht dieselbe Stelle), `EngineChanged`, `NotApplicable` oder `Failed`. Ein Mod, der
sich mit einem Feature nicht verträgt, hält es auf dem Verhalten des Spiels an, statt die Einstellung des Spielers zu ändern:

```csharp
var hold = KometFeatures.HoldOff("ChunkBudget", "mymod", "eigene Upload-Steuerung"); // auch per Schalter: "UploadCap"
hold.Release(); // die Wahl des Spielers gilt wieder
```

Solange gehalten wird, ist der Schalter im Optionsmenü gesperrt (mit Mod und Grund); das HUD-Panel Mods & Patches listet alle
nicht aktiven Features. `KometFeatures.StateChanged` meldet jeden Wechsel; ein Handler, der wirft, wird einmal geloggt und
abgemeldet.

Eigene Features meldet ein Mod in `Start` oder `StartClientSide` an, danach lehnt Komet ab. Komet installiert sie nach den eigenen,
zeigt, hält und benchmarkt ihren Schalter und tritt zurück, solange ein anderer Mod die beobachteten Methoden patcht:

```csharp
KometFeatures.Register(new FeatureDefinition("mymod", "water", Lang.Get("mymod:water"))
{
    Page = KometFeatures.RenderPage, Group = Lang.Get("mymod:title"), Hint = Lang.Get("mymod:water-hint"),
    Knob = FeatureKnob.Switch(() => config.Water, on => config.Water = on, on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Shaped = () => [WaterPatch.Target], Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

Die ersten beiden Funktionen des Schalters sind der gespeicherte Wert des Spielers, die dritte das, was die Patches lesen
(angehalten der Engine-Wert). Id und Harmony-Id sind `mymod:water`, im Benchmark ebenso. `Page` ist `RenderPage`, `ChunksPage`,
`MiscPage` oder eine eigene `KometOptions`-Seite. Passt der Fingerabdruck der `Shaped`-Methoden nicht, bleibt das Feature draußen
(`EngineChanged`); den Wert pinnt ein Test des Mods mit `KometFeatures.Fingerprint(...)` gegen das installierte Spiel. Wirft
`Install`, entfernt Komet die Patches (`Failed`). Beim Schließen der Welt ruft Komet `Uninstall`, entfernt die Patches und vergisst
die Anmeldung; in der nächsten Welt meldet der Mod sich neu an.

## Starteinstellungen (optional)

.NET liest einige Stellschrauben nur beim Start aus Umgebungsvariablen. `run.sh` überschreibt jedes Update, also ein eigenes Skript:

```bash
#!/bin/bash
export DOTNET_TC_CallCountingDelayMs=0
export DOTNET_gcServer=1 DOTNET_GCHeapCount=4 DOTNET_GCDynamicAdaptationMode=0 \
       DOTNET_GCGen0MaxBudget=0x400000 DOTNET_GCNoAffinitize=1
exec /opt/vintagestory/run.sh "$@"
```

- **`DOTNET_TC_CallCountingDelayMs=0`**: heißer Code wird auch während des Ladens sofort auf Tier 1 optimiert. Nicht verwenden:
  `DOTNET_TieredCompilation=0`, `DOTNET_TC_QuickJitForLoops=0`, `DOTNET_TieredPGO=0`.
- **Server-GC statt Workstation-GC**: 1-%-Low 45 → 58 FPS, Frames über 25 ms 13 → 1 pro Minute, GC-Pause im Flug 2,5 → 0,65 s;
  4 statt 8 MiB gen0 halbiert zusätzlich die längsten Pausen.
  Schreibweise `DOTNET_GCGen0MaxBudget` beachten (falsch geschrieben ignoriert Linux sie still); nie ohne Deckel oder mit DATAS.
- **„RAM optimieren“**: nicht auf „Aggressiv“ (2) stellen, das fordert blockierende GCs samt LOH-Kompaktierung an.

## Benchmark

`scripts/bench.sh` startet das Spiel unbeaufsichtigt auf einer Kopie des Spielstands aus `scripts/bench.json`, fliegt eine feste
Route (Stehen, Drehen, Hin- und Rückflug) und schreibt `result.json` und `frames.csv` nach `~/.cache/komet-bench/runs/`.

```bash
./build.sh && scripts/bench.sh --profile smoke   # Messkette prüfen, etwa 4 Minuten
scripts/bench.sh --profile full                  # Komet gegen Engine, etwa 25 Minuten
```

Profile: `smoke`, `aa` (Streuung), `full`, `tess`, `gcreg`, `hud`. Arme laufen im selben Prozess im Wechsel (A B B A) und setzen
Schalter mit ihren Namen aus `Features.cs` (die anderer Mods als `modid:name`); `--mod DIR` misst einen anderen Build, `--env N=V`
setzt Umgebungsvariablen. Das Spiel muss geschlossen und der Desktop entsperrt sein; Fremdlast verfälscht Frametimes, die Spielzeit
nie anhalten.

## Bauen

.NET SDK 10 und Vintage Story unter `/opt/vintagestory` (sonst `VsInstall=…`).

```bash
./build.sh                    # Release-Paket: Releases/komet/ und Releases/komet_<version>.zip
dotnet test tests/Komet.Test  # nur lokal, braucht die Spielinstallation
```

**Aufbau:** `Komet/` der Mod, `tests/Komet.Test` seine Tests, `tests/Komet.Testing` die Test-Rigs, `tools/Komet.Rules` der Analyzer,
`scripts/` der Benchmark.

**Test-Rigs:** `Komet.Testing` bündelt die Rigs der Tests ohne Abhängigkeit von Komet (Harmony-Ids, Logger, fremde Patches, Chunk-
und Licht-Welten, Animationsformen), auch für die Tests anderer Mods. `GameInstall` findet das Spiel über `VINTAGE_STORY`, sonst den
`VsInstall` des Builds, sonst `/opt/vintagestory`; ohne Assets überspringt `RequireAssets()` den Test. `ResolveAssemblies()` lädt wie
das Spiel dessen Abhängigkeiten (protobuf-net, cairo, …) aus der Installation.

Jede Warnung ist ein Fehler (NetAnalyzers, Roslynator, Sonar). Der Analyzer `Komet.Rules` erzwingt zusätzlich: keine `while`/`do`,
kein `goto`, keine Präprozessor-Direktiven, keine Rekursion, begrenzte `for`/`foreach`, höchstens 60 Zeilen pro Funktion,
Assertion-Dichte ≥ 2,0 und vollständige, gleiche Sprachdateien (KR0001–KR0013).

CI (`.github/workflows/build.yml`) prüft die Spiel-DLLs gegen `SHA256SUMS` und ruft `./build.sh` auf, ohne Tests. `main` erzeugt einen
Release-Entwurf `v<version>`, andere Branches ein Prerelease `preview-<sha>` (die neuesten drei bleiben).

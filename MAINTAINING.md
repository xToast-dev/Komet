# Komet warten

Wie Komet aufgebaut ist und wie du es änderst. Code und Kommentare sind Englisch, diese Seite und die `README.md` der
Module Deutsch. Spieler-Doku steht nur im Spiel (`komet-docs`), Doku für Mod-Autoren nur auf der Entwickler-Seite
(`docs/site`).

## Der Aufbau in einem Bild

Komet ist eine Mod aus zehn Projekten, jedes eine eigene DLL im selben Mod-Zip. Ein Projekt darf nur Projekte **unter**
sich benutzen; ein Verweis nach oben baut nicht.

```text
Komet            die Mod: Einstieg (KometModSystem), Serverteil (Host/), Modul-Liste (Modules.cs), modinfo.json, assets/
  └─ Komet.Ui          HUD und Optionsmenü (Hud/, Options/)
       └─ Komet.Vulkan     der Vulkan-Renderer
            ├─ Komet.Shapes     Formen, Animationen, Inventar
            ├─ Komet.Chunks     Ankommen der Chunks, Budgets
            │    └─ Komet.Tessellation   Chunk-Aufbau und seine Planung
            └─ Komet.Rendering  OpenGL-Pfad: Culling, Pools, Shader, Schatten
                 └─ Komet.Diagnostics   Messen: Frame-Uhr, Kosten, Spikes, Mod-Zeiten
                      └─ Komet.Core     öffentliche API, Funktions-Register, Verträge, Helfer
  Komet.World     Welt: Licht, Karte, Events (braucht nur Komet.Core)
```

Von unten nach oben: Core, Diagnostics, Rendering, Tessellation, Chunks, Shapes, World, Vulkan, Ui, Komet. Wer wen
benutzen darf, steht in den `ProjectReference`-Zeilen jeder `<Modul>.csproj` und in
`tests/Komet.Test/Core/ModulesTests.cs`.

Daneben: `Komet.Bench` (der Benchmark, eine eigene Mod, nie im Release), `tests/`, `tools/Komet.Rules` (der Analyzer,
läuft bei jedem Build).

## Wo liegt was

| Du suchst | Dort |
|---|---|
| Welche Funktionen es gibt, in welcher Reihenfolge sie starten | `Komet/Modules.cs` (`InstallOrder`) |
| Eine Funktion und ihr Schalter | die `*Features.cs` des Moduls (der Eintrag `new("Name")`) und die Klasse `Name.cs` im Modul |
| Die öffentliche API für andere Mods | `Komet.Core/Api/` (Namespace `Komet.Api`, DLL `Komet.Core.dll`) |
| Die Verträge `Assert`, `NotNull`, `Finite`, `Index`, `Bounded` | `Komet.Core/Contracts.cs` |
| Texte im Menü und HUD | `Komet/assets/komet/lang/de.json` und `en.json` (Schlüssel `settings-…`, `hud-…`) |
| Die Hilfeseiten im Spiel | `Komet/assets/komet/config/komet-docs/<de|en>/*.md` |
| Die Entwickler-Seite (API-Referenz, Artikel) | `docs/site`, gebaut mit `scripts/apidocs.sh` |
| Vorlagen für Mod-Autoren | `Komet/assets/komet/config/komet-templates/` |
| Was ein Modul von einem höheren braucht | `<Modul>/Links.cs` und `Komet/Modules.cs` (`Wire`) |
| Gemeinsame Build-Einstellungen aller Module | `Komet.Module.props` |

## Bauen und testen

```bash
./build.sh
```

Baut alles im Release, prüft die Regeln und legt die Mod unter `Releases/komet/` und als `Releases/komet_<version>.zip`
ab. Jede Warnung ist ein Fehler.

```bash
scripts/test.sh
```

Die schnellen Tests; `scripts/test.sh --all` läuft alle. Vor einem Release beides und einmal den Debug-Build
(`dotnet build Komet/Komet.csproj -c Debug`), der ein paar Regeln mehr prüft.

```bash
scripts/bench.sh --build --profile quick
```

Startet das Spiel in einer Sandbox mit dem frischen Build und fliegt eine Runde: die einzige Prüfung, dass das Spiel die
Mod wirklich lädt. Nie, während dein eigenes Spiel läuft.

```bash
scripts/seed-pipelines.sh
```

Erneuert die Pipeline-Rezepte, die Komet mitliefert (`Komet/assets/komet/config/pipelines.json`). Sie sind an die
Shader-Quellen gebunden: Nach einer Änderung an einem Shader-Patch einmal laufen lassen, sonst baut Vulkan beim ersten
Betreten einer Welt jede Pipeline erst, wenn sie gezeichnet wird (Ruckler).

## Eine Funktion ändern

1. Finde sie: Der Name im Menü ist der Lang-Schlüssel `settings-<name>` in `de.json`; der Schalter heißt im Code so wie
   der Schlüssel ohne `settings-`. Suche nach `new("Name")` in den `*Features.cs`.
2. Der Eintrag in `*Features.cs` sagt, was beim Start passiert (`Install`), welcher Schalter dazugehört (`Knobs`), was beim
   Stoppen passiert (`Stop`) und wie die Funktion ihren Zustand meldet (`Probe`).
3. Die Arbeit selbst steht in der Klasse gleichen Namens im selben Modul.
4. Baue (`./build.sh`). Der Analyzer meldet fehlende Texte (`KR0010`: Schlüssel fehlt in einer Sprache).

## Eine Funktion hinzufügen

1. Lege die Klasse im passenden Modul an (siehe das Bild oben: Wo würde sie hingehören? Was braucht sie?).
2. Trage sie in die `*Features.cs` des Moduls ein, an der Stelle ihrer Installationsreihenfolge innerhalb des Moduls.
3. Trage ihren Namen in `Komet/Modules.cs` bei `InstallOrder` ein. Fehlt er, startet Komet nicht und sagt im Log, welcher
   Name fehlt.
4. Lege die Texte an: `settings-<name>` und `settings-<name>-hint` in `de.json` **und** `en.json`.
5. Baue und schreib einen kurzen Test unter `tests/Komet.Test/<Modul>/`.

## Eine Funktion entfernen

1. Lösche den Eintrag in `*Features.cs`, den Namen in `Modules.cs` (`InstallOrder`), die Klasse und die Lang-Schlüssel.
2. Baue. Der Compiler nennt jede Stelle, die die Funktion noch benutzt (meist das HUD in `Komet.Ui`).
3. Ein alter `komet.json` eines Spielers mit dem Schalter stört nicht: Unbekannte Schlüssel werden ignoriert.

## Ein ganzes Modul entfernen

1. Lösche den Projektordner und seine Zeile in `Komet.slnx`.
2. Nimm seine `ProjectReference` aus `Komet/Komet.csproj` und aus den Modulen, die es benutzten.
3. Nimm seine Liste aus `Modules.cs` (`All`), seine Funktionsnamen aus `InstallOrder` und seine Zeilen aus `Wire`.
4. Baue. Der Compiler nennt jede Stelle, die das Modul noch benutzt. Was ein tieferes Modul von ihm brauchte, hatte einen
   Haken (`Links.cs`); der Haken behält seine Antwort "ohne das Modul".

## Haken statt Verweise nach oben (`Links.cs`)

Braucht ein tiefes Modul etwas von einem höheren (`Komet.Rendering` fragt zum Beispiel, ob der Vulkan-Renderer zeichnet),
hat es einen **Haken**: ein `Func<…>` oder `Action<…>` in seiner `Links.cs` mit einer Antwort für den Fall, dass das obere
Modul fehlt. `Komet/Modules.cs` setzt beim Start (`Wire`) jeden Haken auf die echte Methode.

Neue Abhängigkeit nach oben? Nie ein `using` auf das obere Modul, sondern:

1. Haken in der `Links.cs` des tiefen Moduls anlegen, mit Kommentar und einer Antwort ohne das Modul.
2. In `Modules.cs` bei `Wire` setzen.

## Die Regeln (Komet.Rules)

Der Analyzer `tools/Komet.Rules` prüft bei jedem Build Regeln nach dem Muster "Power of Ten". Die wichtigsten:

| Regel | Heißt | Was du tust |
|---|---|---|
| KR0001 | keine `while`-Schleifen | `for` mit fester Obergrenze, zum Beispiel `Math.Min(list.Count, MaxItems)` |
| KR0004 | `for` braucht eine konstante Grenze | Grenze als `const` oder `Math.Min(…, Konstante)` |
| KR0005 | `foreach` nur über `Bounded(…)` | `foreach (var x in list.Bounded(MaxItems))` |
| KR0008 | eine Methode höchstens 60 Zeilen | aufteilen |
| KR0010/11 | Lang-Schlüssel fehlt oder nur in einer Sprache | in `de.json` und `en.json` ergänzen |

Verträge (`Assert`, `NotNull`, `Index`, `Finite`, `Bounded`) gehören dorthin, wo ein falscher Wert ein Fehler wäre, nicht
überall.

## Typische Compiler-Meldungen

| Meldung | Bedeutet | Lösung |
|---|---|---|
| `CS0234: Namespace "X" ist im Namespace "Komet" nicht vorhanden` | Ein Modul greift nach oben | Haken in `Links.cs` (siehe oben) oder die Klasse ins tiefere Modul verschieben |
| `CS0122: … ist aufgrund der Schutzstufe nicht zugänglich` | Ein `internal` aus einem anderen Modul | Alle Komet-Projekte sehen sich gegenseitig (`InternalsVisibleTo` in `Komet.Module.props`); fehlt dein Projekt dort, trage es ein |
| `IDE0005: Using-Direktive ist unnötig` | Ein `using` ohne Verwendung | Zeile löschen |
| `Found multiple .dll files with ModSystems` (im Spiel-Log) | Ein Modul enthält eine Klasse `: ModSystem` | Sie gehört ins Projekt `Komet` (`ModulesTests` prüft das) |

## Für Mod-Autoren

Andere Mods binden **`Komet.Core.dll`** ein, nicht `Komet.dll`: Dort liegt die öffentliche API (`Komet.Api`). Was in
`Komet.Core/Api/` `public` ist, ist Teil der API und ändert sich nur mit Bedacht; ihre XML-Kommentare werden die
API-Referenz auf `docs/site`.

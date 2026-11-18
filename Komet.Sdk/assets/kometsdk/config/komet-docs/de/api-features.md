# KapiFeatures · KapiFeature

## Methoden und Verwendung

Eine Funktion ist ein Teil von Komet, den der Spieler schalten kann, zum Beispiel `ChunkBudget`. Komets Funktionen
heißen so, wie `KapiFeatures.Snapshot()` sie auflistet, die anderer Mods `modid:name`.

- `KapiFeatures.Snapshot()` listet alle Funktionen in Installationsreihenfolge, Komets zuerst, als `KapiFeatureInfo` mit `Id`, `Title`,
  `Owner`, `State`, `CanHoldOff`, `HeldBy` und `Reason`. Die Liste ist eine Momentaufnahme und ändert sich danach nicht
  mehr.
- `KapiFeatures.StateOf(id)` gibt einen `KapiFeatureState` zurück. Statt der ID geht auch der Name eines ihrer Schalter oder Regler
  (`UploadCap` findet `ChunkBudget`).
- `komet.Features.StateChanged` meldet jede Änderung mit `Id`, `Previous` und `State`. `Snapshot` und
  `StateOf` sind statisch: Sie brauchen keine Verbindung.

| KapiFeatureState | Bedeutung                                                                                                |
|-----------------|----------------------------------------------------------------------------------------------------------|
| `Unknown`       | Keine Funktion hat diese ID                                                                              |
| `Pending`       | Noch nicht installiert                                                                                   |
| `Active`        | Installiert und bei der Arbeit                                                                           |
| `Off`           | Der Spieler hat sie ausgeschaltet                                                                        |
| `HeldOff`       | Eine Mod hält sie beim Verhalten des Spiels                                                              |
| `StoodDown`     | Eine andere Mod patcht, worauf sie baut; sie hält sich heraus                                            |
| `EngineChanged` | Der Code des Spiels ist nicht der, für den sie geschrieben ist: nicht installiert                        |
| `NotApplicable` | Server-Code, aber der Server läuft nicht in deinem Spiel; oder der Hardware oder dem Treiber fehlt etwas |
| `Failed`        | Sie hat eine Ausnahme geworfen, oder ihr eigener Wächter hat aufgegeben                                  |

### Funktion zurückhalten

Verträgt sich deine Mod nicht mit einer Funktion, hältst du sie beim Verhalten des Spiels, statt die Einstellung des
Spielers zu ändern. Solange du sie hältst, ist ihr Schalter im Menü gesperrt und zeigt deine Mod und deinen Grund.

```csharp
KapiHold hold = komet.Features.HoldOff("ChunkBudget", "own upload control");
if (!hold.IsHolding) { /* refused: the log says why */ }
hold.Dispose(); // the player's value applies again
```

- Komet lehnt ab, wenn es die Funktion nicht gibt, wenn sie keinen Schalter oder Regler hat (`KapiFeatureInfo.CanHoldOff`)
  oder wenn schon zu viele Zurückhaltungen bestehen. Dann ist `IsHolding` false.
- Zurückhalten geht auch, bevor Komet gestartet ist. `Dispose()` wirkt nur einmal; spätere Aufrufe tun nichts.
  `komet.Dispose()` und das Ende der Welt lösen jede Zurückhaltung deiner Verbindung.
- Halten mehrere Mods dieselbe Funktion, nennen `HeldBy` und `Reason` die erste.

### Eigene Funktion anmelden

Komet installiert deine Funktion nach seinen eigenen, zeigt ihren Schalter oder Regler, lässt andere Mods sie
zurückhalten und lässt sie zurücktreten, solange eine andere Mod die beobachteten Methoden patcht.

```csharp
komet.Features.Register(new KapiFeature("water", Lang.Get("mymod:water"))
{
    Page = KapiFeatures.RenderPage,
    Group = Lang.Get("mymod:title"),
    Hint = Lang.Get("mymod:water-hint"),
    Knob = KapiKnob.Switch(() => config.Water, on => config.Water = on,
        on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Uninstall = () => WaterPatch.Enabled = false,
    Shaped = () => [WaterPatch.Target],
    Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

- Der Konstruktor nimmt Name und Titel; die Mod-ID kommt aus der Verbindung. Die ID der Funktion und ihre Harmony-ID
  sind `modid:name`. Der Titel
  steht so in der Zeile, wie du ihn übergibst: Übersetze ihn selbst.
- `Page`: `RenderPage` (Komet › Rendern), `ChunksPage` (Komet › Chunks), `MiscPage` (Komet › Sonstiges), die ID einer
  deiner Seiten (`KapiPage.Id`) oder `null` für keine Zeile.
- `Group`: die Überschrift über der Zeile. Ohne sie steht auf Komets Seiten deine Mod-ID darüber; auf deiner eigenen
  Seite kommt die Zeile ans Ende.
- `Knob`: `KapiKnob.Switch(get, set, apply)` oder `KapiKnob.Range(min, max, engine, get, set, apply, unit)`. `get`
  und `set` tragen den Wert des Spielers, den deine Mod speichert. `apply` bekommt, was deine Patches lesen sollen:
  normalerweise den Wert des Spielers; solange die Funktion zurückgehalten wird oder zurücktritt, den Spielwert
  (`engine`, beim Schalter aus). Ohne `Knob` ist die Funktion immer an, solange sie installiert ist.
- `Install` bekommt eine Harmony-Instanz unter der ID der Funktion und einen Logger. `Uninstall` macht rückgängig, was
  kein Patch ist. Die Patches entfernt Komet selbst: wenn die Welt schließt oder wenn dein Code eine Ausnahme wirft
  (`Failed`).
- `Shaped` nennt die Methoden des Spiels, die du ersetzt; `Fingerprint` ist die Prüfsumme des Codes, für den du die
  Funktion geschrieben hast. Weicht der Code ab, bleibt die Funktion draußen (`EngineChanged`). Den Wert liefert
  `KapiFeatures.FingerprintOf(methods)`; halte ihn in einem Test fest. 0 schaltet die Prüfung ab.
- `Watched`: Methoden, die keine andere Mod patchen soll. `StandDownFor` sagt, welche Patch-Arten zählen (Standard
  `KapiPatchKinds.Replacing`). Tritt die Funktion zurück, bleiben deine Patches drin, aber `apply` bekommt den Spielwert,
  bis Komet die Methoden wieder frei findet. Darum braucht `Watched` einen `Knob`.
- `ServerOnly`: Die Funktion patcht Server-Code. Sie braucht den eingebauten Server (Einzelspieler); sonst ist sie
  `NotApplicable`.
- Deine Funktion bleibt, bis die Welt schließt, auch nach `komet.Dispose()`: Harmony-Patches lassen sich nicht alle auf
  einmal zurücknehmen.

`Register` gibt false zurück und loggt den Grund, wenn es ablehnt:

- Der Name besteht nicht aus Buchstaben, Ziffern, `_` und `-`, die Mod-ID nicht aus Kleinbuchstaben und Ziffern, oder
  der Titel ist leer.
- Die Verbindung ist weg, oder die Welt ist schon geladen. Melde deine Funktion in `StartClientSide` an.
- Die ID ist vergeben, oder es gibt schon 32 Funktionen anderer Mods.
- Der Bereich eines Reglers ist leer oder enthält seinen Spielwert nicht.
- `Fingerprint` ohne `Shaped`, `Watched` ohne `Knob` oder `Watched` mit `StandDownFor` gleich `None`.

### Patch-Arten

`KapiPatchKinds` sind Flags; du kannst sie mit `|` verbinden.

| KapiPatchKinds                | Bedeutung                                                              |
|-------------------------------|------------------------------------------------------------------------|
| `Prefix`                      | Läuft vor dem Original und kann es überspringen                        |
| `Postfix`                     | Läuft nach dem Original und kann das Ergebnis ändern                   |
| `Transpiler`                  | Schreibt den IL-Code des Originals um                                  |
| `Finalizer`                   | Läuft nach dem Original und sieht seine Ausnahme                       |
| `InnerPrefix`, `InnerPostfix` | An einem Aufruf im Original                                            |
| `Body`                        | `Transpiler`, `InnerPrefix` und `InnerPostfix`                         |
| `Replacing`                   | `Prefix` oder `Body`: alles, was das Original ersetzen kann (Standard) |
| `All`                         | Jede Art                                                               |
| `None`                        | Keine Art                                                              |

[API-Index](docs-api)

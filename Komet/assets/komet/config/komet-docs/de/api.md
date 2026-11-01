# API für Mods

Komet hat eine kleine öffentliche API im Namespace `Komet.Api`. Damit kann deine Mod:

- eigene Seiten in Komets Optionsmenü einfügen (`KometOptions`),
- Komets Funktionen abfragen, zurückhalten oder eigene anmelden (`KometFeatures`),
- die Shader des Spiels ändern und eigene Bild-Effekte zeichnen (`KometShaders`),
- Menü-Stile aus Code anmelden (`KometStyles`),
- eigene Zeitmessungen ins Debug-Protokoll schreiben (`KometDebug`).

Wörter wie Define, Uniform oder Pass erklärt die Seite „Begriffe“ unter Hilfe.

## Komet optional einbinden

Deine Mod soll auch ohne Komet laden. Drei Regeln sorgen dafür:

1. Verweise auf `Komet.Core.dll` (dort liegt Komets öffentliche API; die `Komet.dll` startet die Mod nur), ohne sie mitzuliefern: in der `.csproj` mit `Private="false"`.
2. Halte jeden Komet-Aufruf in einer eigenen Klasse. Nur diese Klasse nennt Komets Typen.
3. Ruf diese Klasse nur auf, wenn `api.ModLoader.IsModEnabled("komet")` wahr ist.

```xml
<Reference Include="Komet.Core" HintPath="$(Komet)" Private="false" />
```

```csharp
public override void StartClientSide(ICoreClientAPI api)
{
    if (api.ModLoader.IsModEnabled("komet")) KometBridge.Hook(api, config);
}

// in its own file: the only class that names Komet's types
internal static class KometBridge
{
    public static void Hook(ICoreClientAPI api, MyConfig config)
    {
        // KometOptions.Page(...), KometFeatures.Register(...), ...
    }
}
```

Die Brücke (`KometBridge`) bekommt, was sie braucht, hier `api` und deine Einstellungen. `$(Komet)` ist der Pfad zu `Komet.Core.dll`, beim Bauen gesetzt mit `-p:Komet=<Pfad>`. Die Vulkan-Vorlage (Modding › Vorlagen › **Vulkan-Vorlage anlegen**) zeigt das Muster in einem ganzen Projekt, in ihrem Ordner `code/`.

## Regeln

- Ruf die API vom Haupt-Thread auf, am besten in `StartClientSide`. Nur `KometDebug.Measure` darf jeder Thread aufrufen.
- Alles endet, wenn die Welt schließt: Seiten, Funktionen, Zurückhaltungen, Shader-Anmeldungen, Stile aus Code und Event-Abos. In der nächsten Welt meldest du alles neu an; `StartClientSide` läuft ohnehin jedes Mal, wenn du eine Welt betrittst.
- Wirft ein Event-Handler eine Ausnahme, loggt Komet sie und meldet den Handler ab.

## KometOptions

`KometOptions.Page(id, title, section)` legt eine leere Seite an und gibt sie zurück. Die Zeilen hängst du der Reihe nach an. Jede Methode gibt die Seite zurück, so lassen sich die Aufrufe verketten.

- `id`: über alle Mods hinweg eindeutig. Eine schon vergebene ID fängt die Seite neu an, so verdoppeln sich ihre Zeilen nicht, wenn du eine Welt erneut betrittst.
- `title`: der Seitentitel, schon übersetzt.
- `section`: der Abschnitt, unter dem die Seite steht (ein Reiter oben im Menü). Ohne ihn bekommt die Seite einen eigenen Abschnitt mit ihrem Titel.
- Soll deine Seite in jeder Sprache in Komets Abschnitt Shader stehen, gib als `section` den Wert `Lang.Get("komet:settings-section-shaders")` an.

```csharp
KometOptions.Page("mymod", Lang.Get("mymod:title"))
    .Group(Lang.Get("mymod:rendering"))
    .Switch(Lang.Get("mymod:water"), () => config.Water,
        on => config.Water = on, Lang.Get("mymod:water-hint")).Default(1)
    .Slider(Lang.Get("mymod:detail"), 1, 8, 1, () => config.Detail,
        v => config.Detail = (int)v, hint: Lang.Get("mymod:detail-hint")).Default(4)
    .Format(v => v >= 8 ? "max" : v.ToString())
    .EnabledWhen(() => config.Water)
    .Choice(Lang.Get("mymod:mode"), ["A", "B", "C"], () => config.Mode,
        i => config.Mode = i)
    .Button(Lang.Get("mymod:reset"), Lang.Get("mymod:do-reset"), config.Reset);

KometOptions.Applied += (_, _) => config.Save();
```

| Mitglied | Was es tut |
|---|---|
| `Group(title)` | Eine Überschrift über den folgenden Zeilen |
| `Switch(label, get, set, hint)` | An oder aus: `Func<bool>`, `Action<bool>` |
| `Slider(label, min, max, step, get, set, unit, hint)` | Eine Zahl: `Func<double>`, `Action<double>`; `max` größer als `min`, `step` größer als 0 |
| `Choice(label, names, get, set, hint)` | Einer von 1 bis 40 Namen; der Wert ist der Index (`Func<int>`, `Action<int>`) |
| `Button(label, text, click, hint)` | Eine Zeile mit Knopf; wirkt sofort, ohne **Übernehmen** |
| `Default(value)` | Standardwert der letzten Zeile; bei einem Schalter 0 oder 1, bei einer Auswahl der Index |
| `Format(format)` | Wie die letzte Zeile ihren Wert zeigt |
| `EnabledWhen(enabled)` | Die letzte Zeile nimmt nur Eingaben an, solange `enabled` true liefert; sonst ist sie gedimmt |

`hint` und `unit` sind optional. Übergib sie mit Namen, zum Beispiel `unit: " ms"`. Komet hängt `unit` direkt an die Zahl, es sei denn, die Zeile hat ein `Format`: Dann zeigt sie nur dessen Text.

### Werte und Speichern

- Getter, Setter, Speichern und Übersetzung sind Sache deiner Mod.
- Änderungen an Schaltern, Reglern und Auswahlen merkt sich Komet, bis der Spieler sie übernimmt (**Enter** oder **Übernehmen** in der Fußzeile). Dann ruft es deine Setter auf.
- Danach feuert `KometOptions.Applied`, wenn sich etwas geändert hat: der richtige Moment zum Speichern.
- Die Taste R (**Standard**) setzt die ganze Seite auf die Werte aus `Default` zurück. Einen einzelnen Regler setzt ein Rechtsklick auf seine Spur zurück. Ein Regler markiert seinen Standardwert auf der Spur.
- Grenzen: `KometOptions.MaxPages` (32) Seiten für alle Mods zusammen, Komets eigene Shader-Seiten zählen mit; `OptionPage.MaxOptions` (40) Zeilen pro Seite. Mehr lehnt Komet ab.

## KometFeatures

Eine Funktion ist ein Teil von Komet, den der Spieler schalten kann, zum Beispiel `ChunkBudget`. Komets Funktionen heißen wie im README, die anderer Mods `modid:name`.

- `Snapshot()` listet alle Funktionen in Installationsreihenfolge, Komets zuerst, als `FeatureInfo` mit `Id`, `Title`, `Owner`, `State`, `CanHoldOff`, `HeldBy` und `Reason`. Die Liste ist eine Momentaufnahme und ändert sich danach nicht mehr.
- `StateOf(id)` gibt einen `FeatureState` zurück. Statt der ID geht auch der Name eines ihrer Schalter oder Regler (`UploadCap` findet `ChunkBudget`).
- `StateChanged` meldet jede Änderung mit `Id`, `Previous` und `State`.

| FeatureState | Bedeutung |
|---|---|
| `Unknown` | Keine Funktion hat diese ID |
| `Pending` | Noch nicht installiert |
| `Active` | Installiert und bei der Arbeit |
| `Off` | Der Spieler hat sie ausgeschaltet |
| `HeldOff` | Eine Mod hält sie beim Verhalten des Spiels |
| `StoodDown` | Eine andere Mod patcht, worauf sie baut; sie hält sich heraus |
| `EngineChanged` | Der Code des Spiels ist nicht der, für den sie geschrieben ist: nicht installiert |
| `NotApplicable` | Server-Code, aber der Server läuft nicht in deinem Spiel; oder der Hardware oder dem Treiber fehlt etwas |
| `Failed` | Sie hat eine Ausnahme geworfen, oder ihr eigener Wächter hat aufgegeben |

### Funktion zurückhalten

Verträgt sich deine Mod nicht mit einer Funktion, hältst du sie beim Verhalten des Spiels, statt die Einstellung des Spielers zu ändern. Solange du sie hältst, ist ihr Schalter im Menü gesperrt und zeigt deine Mod und deinen Grund.

```csharp
FeatureHold hold = KometFeatures.HoldOff("ChunkBudget", "mymod", "own upload control");
if (!hold.IsHolding) { /* refused: the log says why */ }
hold.Release(); // or Dispose(): the player's value applies again
```

- Komet lehnt ab, wenn es die Funktion nicht gibt, wenn sie keinen Schalter oder Regler hat (`FeatureInfo.CanHoldOff`) oder wenn schon zu viele Zurückhaltungen bestehen. Dann ist `IsHolding` false.
- Zurückhalten geht auch, bevor Komet gestartet ist. `Release()` und `Dispose()` wirken nur einmal; spätere Aufrufe tun nichts.
- Halten mehrere Mods dieselbe Funktion, nennen `HeldBy` und `Reason` die erste.

### Eigene Funktion anmelden

Komet installiert deine Funktion nach seinen eigenen, zeigt ihren Schalter oder Regler, lässt andere Mods sie zurückhalten und lässt sie zurücktreten, solange eine andere Mod die beobachteten Methoden patcht.

```csharp
KometFeatures.Register(new FeatureDefinition("mymod", "water", Lang.Get("mymod:water"))
{
    Page = KometFeatures.RenderPage,
    Group = Lang.Get("mymod:title"),
    Hint = Lang.Get("mymod:water-hint"),
    Knob = FeatureKnob.Switch(() => config.Water, on => config.Water = on,
        on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Uninstall = () => WaterPatch.Enabled = false,
    Shaped = () => [WaterPatch.Target],
    Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

- Der Konstruktor nimmt Mod-ID, Name und Titel. Die ID der Funktion und ihre Harmony-ID sind `modid:name`. Der Titel steht so in der Zeile, wie du ihn übergibst: Übersetze ihn selbst.
- `Page`: `RenderPage` (Komet › Rendern), `ChunksPage` (Komet › Chunks), `MiscPage` (Komet › Sonstiges), die ID einer deiner `KometOptions`-Seiten oder `null` für keine Zeile.
- `Group`: die Überschrift über der Zeile. Ohne sie steht auf Komets Seiten deine Mod-ID darüber; auf deiner eigenen Seite kommt die Zeile ans Ende.
- `Knob`: `FeatureKnob.Switch(get, set, apply)` oder `FeatureKnob.Range(min, max, engine, get, set, apply, unit)`. `get` und `set` tragen den Wert des Spielers, den deine Mod speichert. `apply` bekommt, was deine Patches lesen sollen: normalerweise den Wert des Spielers; solange die Funktion zurückgehalten wird oder zurücktritt, den Spielwert (`engine`, beim Schalter aus). Ohne `Knob` ist die Funktion immer an, solange sie installiert ist.
- `Install` bekommt eine Harmony-Instanz unter der ID der Funktion und einen Logger. `Uninstall` macht rückgängig, was kein Patch ist. Die Patches entfernt Komet selbst: wenn die Welt schließt oder wenn dein Code eine Ausnahme wirft (`Failed`).
- `Shaped` nennt die Methoden des Spiels, die du ersetzt; `Fingerprint` ist die Prüfsumme des Codes, für den du die Funktion geschrieben hast. Weicht der Code ab, bleibt die Funktion draußen (`EngineChanged`). Den Wert liefert `KometFeatures.Fingerprint(methods)`; halte ihn in einem Test fest. 0 schaltet die Prüfung ab.
- `Watched`: Methoden, die keine andere Mod patchen soll. `StandDownFor` sagt, welche Patch-Arten zählen (Standard `PatchKinds.Replacing`). Tritt die Funktion zurück, bleiben deine Patches drin, aber `apply` bekommt den Spielwert, bis Komet die Methoden wieder frei findet. Darum braucht `Watched` einen `Knob`.
- `ServerOnly`: Die Funktion patcht Server-Code. Sie braucht den eingebauten Server (Einzelspieler); sonst ist sie `NotApplicable`.

`Register` gibt false zurück und loggt den Grund, wenn es ablehnt:

- Die ID ist nicht `modid:name` (Mod-ID aus Kleinbuchstaben und Ziffern; im Namen Buchstaben, Ziffern, `_` und `-`), oder der Titel ist leer.
- Die Welt ist schon geladen. Melde deine Funktion in `StartClientSide` an.
- Die ID ist vergeben, oder es gibt schon 32 Funktionen anderer Mods (`KometFeatures.MaxExternal`).
- Der Bereich eines Reglers ist leer oder enthält seinen Spielwert nicht.
- `Fingerprint` ohne `Shaped`, `Watched` ohne `Knob` oder `Watched` mit `StandDownFor` gleich `None`.

### Patch-Arten

`PatchKinds` sind Flags; du kannst sie mit `|` verbinden.

| PatchKinds | Bedeutung |
|---|---|
| `Prefix` | Läuft vor dem Original und kann es überspringen |
| `Postfix` | Läuft nach dem Original und kann das Ergebnis ändern |
| `Transpiler` | Schreibt den IL-Code des Originals um |
| `Finalizer` | Läuft nach dem Original und sieht seine Ausnahme |
| `InnerPrefix`, `InnerPostfix` | An einem Aufruf im Original |
| `Body` | `Transpiler`, `InnerPrefix` und `InnerPostfix` |
| `Replacing` | `Prefix` oder `Body`: alles, was das Original ersetzen kann (Standard) |
| `All` | Jede Art |
| `None` | Keine Art |

## KometShaders

Ändert die Shader des Spiels und zeichnet eigene Vollbild-Effekte, unter OpenGL wie unter Komets Vulkan-Renderer.

| Mitglied | Was es tut |
|---|---|
| `Define(modId, name, value, programs)` | Setzt `#define name value` in Programme; `value` wird beim Laden gelesen, ruf also nach einer Änderung `Reload()` auf |
| `Uniform(modId, name, value)` | Eine Uniform, die Komet in jedem Bild setzt (1 bis 4 Zahlen) |
| `Edit(modId, program, vertex, fragment)` | Schreibt den Quelltext eines Programms beim Laden um |
| `Reload()` | Baut die Shader des Spiels mit allen Defines und Edits neu |
| `AddPass(pass)` | Ein eigener Vollbild-Pass (`KometPass`) |
| `Renderer`, `RendererChanged` | Welcher Renderer gerade zeichnet und wann er wechselt |

```csharp
IDisposable hour = KometShaders.Uniform("mymod", "mymodHour", () => api.World.Calendar.HourOfDay);
hour.Dispose(); // takes it back before the world closes
```

- Jede Anmeldung gibt ein `IDisposable` zurück. `Dispose()` nimmt sie vorzeitig zurück.
- Jedes Programm hat `#define KOMET 1`, unter Vulkan dazu `#define KOMET_VULKAN 1`.
- Für alle Mods zusammen nimmt Komet bis zu 128 Defines, 256 Uniforms, 64 Edits und 32 Passes.

Alles Weitere mit Beispielen steht auf der Seite „Shader-Referenz“, was unter Vulkan gilt, auf „Shader für Vulkan“.

## KometStyles

Meldet einen Menü-Stil aus Code an. Eine schon vergebene ID ersetzt den Stil, danach feuert `KometStyles.Changed`. So kannst du bis zu 32 Stile anmelden (`KometStyles.MaxStyles`).

```csharp
KometStyles.Register(new KometStyle
{
    Id = "mymod-night",
    Name = "Night",
    Base = "pragmatic",
    Colors = { ["accent"] = "#7A5CFF" }
});
```

`KometStyle` hat `Id`, `Name`, `Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo`, `Background`, `BackgroundSource`, `Folder` und `Domain`. Was sie bedeuten und wo relative Dateien liegen, steht auf der Seite „Menü-Stile“.

## KometDebug

Schreibt eigene Zeitmessungen ins Debug-Protokoll.

```csharp
using (KometDebug.Measure("mymod:pathfinding")) FindPath();
```

- `Measure(name)` geht auf jedem Thread. Es gibt ein `KometTiming` zurück; sein `Dispose()` (oder `using`) verbucht die Zeit. Außerhalb einer Aufnahme kostet es nur das Lesen eines Felds.
- Eine Aufnahme startest du mit `.komet debug` oder im Debug-Fenster (Komet › HUD › **Debug-Fenster (Strg+F8)**). Das Protokoll listet jeden Namen mit Gesamtzeit, Anzahl der Aufrufe und Zeit pro Aufruf, die teuersten zuerst.
- `KometDebug.Capturing` sagt, ob gerade aufgenommen wird. Prüf es vor Arbeit, die sich nur für das Protokoll lohnt.
- Nimm Namen wie `modid:was`, höchstens 128 Zeichen; längere misst Komet nicht. Eine Aufnahme behält bis zu 1024 Namen (`KometDebug.MaxTimings`).

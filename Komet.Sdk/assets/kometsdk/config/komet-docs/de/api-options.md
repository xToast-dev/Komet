# KapiOptions · KapiPage

## Methoden und Verwendung

`komet.Options.Page(title)` legt die Seite deiner Mod an und gibt sie zurück. Die Zeilen hängst du der Reihe nach an.
Jede Methode gibt die Seite zurück, so lassen sich die Aufrufe verketten.

- `Page(title)`: eine Seite, ID = deine Mod-ID, im Reiter **Mods** neben denen der anderen Mods.
- `Page(id, title, section)`: eine von mehreren Seiten; ihre volle ID ist `modid:id`. `section` ist der Abschnitt, unter
  dem die Seite steht (ein Reiter oben im Menü); `null` legt sie in den Reiter **Mods**.
- Fragst du noch einmal nach derselben Seite, fängt sie neu an, so verdoppeln sich ihre Zeilen nicht, wenn du eine Welt
  erneut betrittst.
- `title` und alle Beschriftungen erscheinen so, wie du sie übergibst: übersetze sie selbst.
- Soll deine Seite in jeder Sprache in Komets Abschnitt Shader stehen, gib als `section` den Wert
  `Lang.Get("komet:settings-section-shaders")` an.

```csharp
komet.Options.Page("main", Lang.Get("mymod:title"), null)
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

komet.Options.Applied += (_, _) => config.Save();
```

| Mitglied                                              | Was es tut                                                                                    |
|-------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| `Group(title)`                                        | Eine Überschrift über den folgenden Zeilen                                                    |
| `Switch(label, get, set, hint)`                       | An oder aus: `Func<bool>`, `Action<bool>`                                                     |
| `Slider(label, min, max, step, get, set, unit, hint)` | Eine Zahl: `Func<double>`, `Action<double>`; `max` größer als `min`, `step` größer als 0      |
| `Choice(label, names, get, set, hint)`                | Einer von 1 bis 40 Namen; der Wert ist der Index (`Func<int>`, `Action<int>`)                 |
| `Button(label, text, click, hint)`                    | Eine Zeile mit Knopf; wirkt sofort, ohne **Übernehmen**                                       |
| `Default(value)`                                      | Standardwert der letzten Zeile; bei einem Schalter 0 oder 1, bei einer Auswahl der Index      |
| `Format(format)`                                      | Wie die letzte Zeile ihren Wert zeigt                                                         |
| `EnabledWhen(enabled)`                                | Die letzte Zeile nimmt nur Eingaben an, solange `enabled` true liefert; sonst ist sie gedimmt |
| `Id`, `Title`                                         | Die volle ID der Seite (für `KapiFeature.Page`) und ihr Titel                                 |

`hint` und `unit` sind optional. Übergib sie mit Namen, zum Beispiel `unit: " ms"`. Komet hängt `unit` direkt an die
Zahl, es sei denn, die Zeile hat ein `Format`: Dann zeigt sie nur dessen Text.

### Werte und Speichern

- Getter, Setter, Speichern und Übersetzung sind Sache deiner Mod.
- Änderungen an Schaltern, Reglern und Auswahlen merkt sich Komet, bis der Spieler sie übernimmt (**Enter** oder
  **Übernehmen** in der Fußzeile). Dann ruft es deine Setter auf.
- Danach feuert `komet.Options.Applied`, wenn sich etwas geändert hat: der richtige Moment zum Speichern.
- Die Taste R (**Standard**) setzt die ganze Seite auf die Werte aus `Default` zurück. Einen einzelnen Regler setzt ein
  Rechtsklick auf seine Spur zurück. Ein Regler markiert seinen Standardwert auf der Spur.
- Grenzen: `KapiOptions.MaxPages` (32) Seiten für alle Mods zusammen, Komets eigene Shader-Seiten zählen mit;
  `KapiPage.MaxRows` (40) Zeilen pro Seite. Mehr lehnt Komet ab.
- `komet.Dispose()` und das Ende der Welt nehmen deine Seiten weg.

[API-Index](docs-api)

# KapiOptions · KapiPage

## Members and usage

`komet.Options.Page(title)` creates your mod's page and returns it. You add the rows in order. Every method returns
the page, so you can chain the calls.

- `Page(title)`: one page, id = your mod id, in the **Mods** tab beside every other mod's.
- `Page(id, title, section)`: one of several pages; its full id is `modid:id`. `section` is the section the page goes
  under (a tab at the top of the menu); `null` puts it in the **Mods** tab.
- Asking again for the same page starts it over, so its rows do not double when you join a world again.
- `title` and every label are shown as you pass them: translate them yourself.
- To put your page into Komet's Shaders section in every language, pass `Lang.Get("komet:settings-section-shaders")` as
  `section`.

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

| Member                                                | What it does                                                                                |
|-------------------------------------------------------|---------------------------------------------------------------------------------------------|
| `Group(title)`                                        | A heading over the rows that follow                                                         |
| `Switch(label, get, set, hint)`                       | On or off: `Func<bool>`, `Action<bool>`                                                     |
| `Slider(label, min, max, step, get, set, unit, hint)` | A number: `Func<double>`, `Action<double>`; `max` greater than `min`, `step` greater than 0 |
| `Choice(label, names, get, set, hint)`                | One of 1 to 40 names; the value is the index (`Func<int>`, `Action<int>`)                   |
| `Button(label, text, click, hint)`                    | A row with a button; acts at once, without **Apply**                                        |
| `Default(value)`                                      | The last row's default value; for a switch 0 or 1, for a choice the index                   |
| `Format(format)`                                      | How the last row shows its value                                                            |
| `EnabledWhen(enabled)`                                | The last row takes input only while `enabled` returns true; otherwise it is dimmed          |
| `Id`, `Title`                                         | The page's full id (for `KapiFeature.Page`) and its title                                   |

`hint` and `unit` are optional. Pass them by name, for example `unit: " ms"`. Komet puts `unit` right after the number,
unless the row has a `Format`: then it shows only that text.

### Values and saving

- Getters, setters, saving and translation are up to your mod.
- Komet keeps changes to switches, sliders and choices until the player applies them (**Enter** or **Apply** in the
  footer). Then it calls your setters.
- Afterwards `komet.Options.Applied` fires if anything changed: the right moment to save.
- The R key (**Default**) resets the whole page to the values from `Default`. A right-click on a slider's track resets
  just that slider. A slider marks its default value on its track.
- Limits: `KapiOptions.MaxPages` (32) pages for all mods together, Komet's own shader pages included;
  `KapiPage.MaxRows` (40) rows per page. Komet refuses more.
- `komet.Dispose()` and the end of the world take your pages away.

[API index](docs-api)

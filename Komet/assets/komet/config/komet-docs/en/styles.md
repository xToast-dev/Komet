# Menu styles

Komet draws its options menu in a **style**. A style sets the colours, fonts, sizes, logo and background. Komet ships two styles: **Pragmatic** (tabs on top, below them the page list, the rows and the details, over a planet in orbit) and **Vintage** (the brown, parchment and the fonts Lora and Montserrat of the game's dialogs, over the blurred world).

You pick the style under Komet › Style › **Style**. It changes at once; you do not need to press **Apply**. The style also passes its colours and fonts on to Komet's HUD and its windows. Anyone can make more styles, without any code.

**Pragmatic's look and name are a nod to the menus of Capcom's game Pragmata. Komet has no connection to Capcom and is not endorsed by Capcom.**

## Quick start

You need neither code nor a restart. Start from one of two templates:

- Modding › Templates › **Create example style** writes a small style into `ModConfig/komet/styles/example/` and picks it at once. It changes only four colours and one size.
- Komet › Style › **Copy this style** copies the style you are using now into `ModConfig/komet/styles/<id>-copy/` (if that name is taken: `<id>-copy2` and so on). Komet picks the copy and opens the folder. The copy spells out every colour, size and font, plus `background.fsh` if the style has a background shader. It inherits the logo and font files from the original.

From there, the steps are always the same:

```flow
Create a template
Change style.json in an editor
Save the file
Komet › Style › Reload styles
See the new look at once
```

Errors are written to `client-main.log`, in lines with `Komet styles:`.

## Where styles come from

Komet looks for styles in three places, in this order. When two styles share an id, the later one wins.

| Source | Place |
|---|---|
| Any mod's assets | `assets/<domain>/config/komet-styles/<id>/style.json` |
| Your folder | `ModConfig/komet/styles/<id>/style.json` |
| Code | `KometStyles.Register(...)` |

- The id is the folder's name in lower case, unless `style.json` sets `"id"`.
- Komet reads up to 32 folders from `ModConfig/komet/styles`.
- File names in a style (logo, shader, fonts) are relative to the style's folder. With a colon they name an asset directly, for example `komet:config/fonts/SairaSemiCondensed-Regular.ttf`.
- A content mod needs no code: it puts the style folder into its assets, and every player gets the style with the mod.

## The example style

**Create example style** (Modding › Templates) creates this `style.json`. A style names only what it changes. Everything else comes from its base style.

```json
{
  "$schema": "https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet-style.schema.json",
  "id": "example",
  "name": "komet:example-style",
  "author": "Komet",
  "base": "pragmatic",
  "colors": {
    "accent": "#E8A23A",
    "select": "#B8741C",
    "title": "#F2C27A",
    "changed": "#FFD08A"
  },
  "sizes": {
    "tracking": 4
  }
}
```

| Part | What it does |
|---|---|
| `$schema` | Lets your editor suggest the keys as you type. Komet itself does not need the line |
| `id` | The name Komet knows the style by |
| `name` | Shown in the style choice. `komet:example-style` is a lang key; plain text works too |
| `author` | Free text |
| `base` | The base style, Pragmatic here. Anything missing here looks as it does there |
| `accent` | Active controls, the tab underline and flashes, amber here |
| `select` | The background of the selected row |
| `title` | Headings and button captions |
| `changed` | Changes you have not applied yet, and their count |
| `tracking` | The letter spacing of the KOMET lettering and the page titles |

The file also has a `_comment` line with a note; Komet skips it. Change a colour, save and press **Reload styles** under Komet › Style.

## style.json

| Key | Meaning |
|---|---|
| `id` | Unique name; without it, the folder's name |
| `name` | Shown in the style choice: text or a lang key (`domain:key`); without it, the id |
| `author` | Free text |
| `base` | Base style: `pragmatic` (default) or any other id |
| `colors` | Colours as `#RRGGBB` or `#RRGGBBAA`, keys below |
| `fonts` | Font families, keys below; `""` takes the game's font |
| `fontFiles` | `.ttf` or `.otf` files Komet registers before drawing |
| `sizes` | Sizes in GUI pixels before the game's GUI scale, keys below |
| `logo` | PNG, GIF or WebP for the header |
| `background` | `blur` (default), `none` or a shader file |

- If `base` names the style's own id, Komet means the version it found before. That way a folder `pragmatic/` with `"base": "pragmatic"` restyles Pragmatic itself.
- A base style may have a base style of its own, up to 8 levels deep. If one is missing or the chain runs in a circle, the log says so.
- Unknown keys in `colors`, `fonts` and `sizes` as well as invalid colours go to the log, so that a typo stands out. Komet skips unknown keys at the top level without a message; the `$schema` line helps there.

### Colours

| Key | Used for |
|---|---|
| `backdrop` | Dims the world under a shader or blur |
| `backdropPlain` | Dims the world when neither shader nor blur shows |
| `panel` | Panels |
| `band` | Header, footer, section bands, buttons |
| `row` | Row background (reserved; Pragmatic draws none) |
| `line` | Hairlines, outlines, check boxes |
| `text`, `soft`, `faint` | Main, secondary and dimmed text |
| `accent` | Active controls, tab underline, flashes |
| `select` | Background of the selected row |
| `selectText` | Text and corner marks of the selected row |
| `title` | Headings, button captions |
| `changed` | Changes not yet applied, their count |
| `good`, `bad` | Good and bad in the HUD; in the menu `good` for "Copied" and for mods that talk to Komet |
| `glow` | Glow under the mouse |
| `cell`, `chip`, `bubble` | Unused, value chips, the slider's bubble |

`panel`, `band` and `row` get denser or more see-through with Komet › Style › **Panel opacity**.

### Fonts

Four keys: `text` (rows, hints), `heading` (row titles, tabs, controls), `title` (page titles, KOMET lettering) and `numbers` (the big value in the details).

- Pragmatic ships **Saira Semi Condensed** (SIL Open Font License) and names the families `Saira SemiCondensed` and `Saira SemiCondensed Medium`.
- A family named in `fonts` without its file in `fontFiles` must be installed on the player's computer.
- On Linux Komet registers font files with fontconfig, on Windows as private fonts. Where that does not work (on macOS, for example), the game's font stands in.
- To make your own font from Saira, change it in a font editor and give the family a new name: the OFL forbids keeping the reserved name "Saira".

### Sizes

| Key | Meaning |
|---|---|
| `header`, `footer` | Height of header and footer |
| `logo` | The logo's longest side in the menu header; `0` hides it there |
| `row` | Row height |
| `gap` | Space between panels |
| `ticks` | Size of the corner marks on panels, above `0` also on the selected row; `0` shows none |
| `tracking` | Letter spacing of the KOMET lettering and the page titles |
| `scene` | How strongly the shader covers the world, 0 to 1 |
| `veil` | How much Komet darkens the shader behind panels, 0 to 1 |
| `pan`, `slide`, `decode` | `1` turns on camera moves, lists sliding in and names building up |
| `corner`, `pad` | Reserved: allowed, but they do nothing yet |

Komet › Style › **Animations** always has the last word over `pan`, `slide` and `decode`. When it is set to **Off**, nothing moves.

## The logo

The header shows the first image in this list that exists: the style's `logo` (also one inherited from its base style), then `ModConfig/komet/logo.png` (yours, in every style), then Komet's icon. The logo also shows in the header of Komet's HUD windows.

Komet enlarges a logo only in whole steps (double, triple) and shrinks it to a half, a third and so on. That keeps pixel art sharp. The longer side counts, so a square logo works best. A logo file may be at most 4 MiB.

## Background shaders

`background` names a GLSL 330 fragment shader. Write only your functions and `main()`. Komet puts a prelude in front that already declares every input below. Line numbers in error messages refer to your file. The file may be at most 64 KiB.

### Inputs

| Name | Meaning |
|---|---|
| `uv` | Position in the image, 0 to 1, (0,0) bottom left |
| `color` | Your output; alpha says how strongly it covers the world |
| `time` | Seconds since the menu opened |
| `fade` | 0 to 1 while fading in, already multiplied by `scene` |
| `page` | Open page as section × 10 + page; eases smoothly only with `pan` |
| `resolution` | Image size in pixels |
| `camera` | x and y offset and zoom, easing per page (only with `pan`) |
| `accent` | The style's accent colour |
| `scene` | The blurred world (sampler) |
| `world(at)` | The blurred world at the position `at`, counted like `uv` |

### In a world

Once you are in a world, there is more:

- `worldAt(at)` gives the world map you have explored: a square around the spot where Komet read the map (x to the east, y to the south). Alpha is 0 where nothing is explored. If the server does not allow a map, it stays empty.
- `worldTint(kind, own)` gives the average colour of the ground around you for 0 water, 1 sand, 2 rock, 3 snow, 4 green. If Komet knows no colour for a kind, or there is no world map, you get `own` back.
- `worldPlace`: latitude, the sun's hour angle from noon and its declination (all in radians); w is 1 in a world.
- `worldWeather`: cloud cover and precipitation (0 to 1), temperature in °C / 40; w is 1 in a world.
- `planet` eases to 1 once the world map is read, and to 0 without a map.
- `worldScale`: x is the planet's radius in blocks (the world's width is the circumference), y the map's reach in blocks, z and w your offset from the map's centre.
- `worldChunk`: x and y where the map's centre lies inside its chunk, z and w that chunk's number.
- `worldYaw`: the direction you are looking in, in radians.

### What Komet does next

- Write `fade` into alpha to cover the world fully.
- After your `main()` Komet darkens the picture behind the panels by `veil` and makes it a little greyer there. Your shader does not need to do anything for that.
- The prelude also declares `panels`, `panelCount`, `veil`, `worldMap`, `worldKnown` and `worldTints`. Do not declare these names again.
- If the shader does not compile, the errors go to the log and the blur stands in. **Reload styles** compiles it again.

Pragmatic's shader is a complete example. The easiest way to get it is **Copy this style** while Pragmatic is active: the copy holds it as `background.fsh`.

## From code

Call this in `StartClientSide`, and only when `api.ModLoader.IsModEnabled("komet")` is true (see "API for mods"):

```csharp
KometStyles.Register(new KometStyle
{
    Id = "mymod-night",
    Name = "Night",
    Base = "pragmatic",
    Domain = "mymod",
    Colors = { ["accent"] = "#7A5CFF", ["select"] = "#4B33B8" },
    Sizes = { ["tracking"] = 4 },
    Logo = "logo.png",
    Background = "background.fsh" // or BackgroundSource = shaderText
});
```

- `KometStyle` has the same fields as `style.json`: `Id`, `Name`, `Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo` and `Background`. On top of that come `BackgroundSource` (the shader as text; takes precedence over `Background`), `Folder` and `Domain`.
- Komet looks for relative files in `Folder` (a folder on disk), otherwise in the assets of `Domain` under `config/komet-styles/<Id>/`. In the example, the logo therefore sits at `assets/mymod/config/komet-styles/mymod-night/logo.png`. If both are missing, only asset names with a colon and absolute paths work.
- Registering an id that is already taken replaces that style. Afterwards `KometStyles.Changed` fires. Code can register up to 32 styles (`KometStyles.MaxStyles`).
- Styles from code are removed when the world closes. In the next world, register them again.

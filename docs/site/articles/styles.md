# Menu styles

A style decides how Komet's options screen looks: colours, fonts, sizes, logo and a background shader.
Styles are data; every value a style leaves out comes from its base.

| Source | Path | Relative files resolve against |
|---|---|---|
| any mod's assets | `assets/<domain>/config/komet-styles/<id>/style.json` | the same asset folder |
| the player's folder | `ModConfig/komet/styles/<id>/style.json` (reloads in game) | that folder |
| code | <xref:Komet.Api.KometStyles.Register*> | `Folder`, else the assets of `Domain` |

Later sources win when two styles share an id. The id is the folder's name unless `style.json` sets `id`. A
`base` naming the style's own id means the definition found before it, so a folder `pragmatic/` restyles
Pragmatic itself. A file may also name an asset (`komet:config/fonts/...`) or an absolute path.

```json
{
  "$schema": "https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet-style.schema.json",
  "name": "Ember",
  "base": "pragmatic",
  "colors": { "accent": "#E2672F", "select": "#B6461F", "changed": "#FFB08D" }
}
```

Every key is described on <xref:Komet.Api.KometStyle>; the schema `docs/schema/komet-style.schema.json` lets
editors complete them. Unknown keys are logged once. In game, *Modding › Menu styles* walks through the example
style (*Modding › Templates › Create example style* writes it).

## Keys

| Colour | Paints |
|---|---|
| `backdrop`, `backdropPlain` | the dim over the world, with and without a background shader or blur |
| `panel`, `band`, `row` | panels, header/footer/buttons, row grounds (scaled by the player's panel opacity) |
| `line` | hairlines, outlines, check boxes |
| `text`, `soft`, `faint` | main, secondary and dimmed text |
| `accent` | active controls, tab underline, flashes |
| `select`, `selectText` | the lit row's ground, its text and corner marks |
| `title` | headings, button captions |
| `changed` | changes not yet applied, their count |
| `good`, `bad` | effects that help or cost |
| `glow` | hover glow |
| `cell`, `chip`, `bubble` | unused, value chips, the slider's bubble |

Colours are `#RRGGBB` or `#RRGGBBAA`. Fonts: `text`, `heading`, `title`, `numbers`; `""` takes the game's font.
A family must be installed or come from `fontFiles` (registered with fontconfig on Linux, as private fonts on
Windows; elsewhere the game's font stands in).

| Size | Meaning |
|---|---|
| `header`, `footer`, `row` | heights |
| `logo` | the logo's square; `0` hides it |
| `gap`, `pad` | space between and inside panels |
| `ticks`, `corner` | corner marks' length, corner radius |
| `tracking` | letter spacing of brand and page titles |
| `scene`, `veil` | how strongly the background shader covers the world, how much it darkens behind panels (0..1) |
| `pan`, `slide`, `decode` | `1` turns on the page camera, sliding lists, decoding names |

Sizes are GUI pixels before the game's GUI scale. The header's logo is the style's `logo`, else
`ModConfig/komet/logo.png`, else Komet's icon.

## Background shaders

`background` names a GLSL 330 fragment shader. Write only your functions and `main()`; Komet puts this in front
(line numbers in errors are your file's):

```glsl
in vec2 uv;                 // 0..1, (0, 0) bottom left
out vec4 color;             // alpha: how much covers the world; write fade for all
uniform float time;         // seconds since the screen opened
uniform float fade;         // 0..1 while fading in, times the style's scene size
uniform float page;         // the open page, eased: section * 10 + page
uniform vec2 resolution;    // in pixels
uniform vec3 camera;        // x, y offset and zoom, eased per page
uniform vec4 accent;        // the style's accent colour
uniform sampler2D scene;    // the world, blurred
vec3 world(vec2 at);        // the blurred world at uv coordinates
// in a world only:
vec4 worldAt(vec2 at);      // the explored world map around the player, north up; alpha 0 where unexplored
vec3 worldTint(int kind, vec3 own); // average colour of 0 water, 1 sand, 2 rock, 3 snow, 4 green; own if unknown
uniform vec4 worldPlace;    // latitude, sun hour angle from noon, declination (radians); w 1 in a world
uniform vec4 worldWeather;  // cloud cover, precipitation, temperature (°C / 40); w 1 in a world
uniform float planet;       // eases to 1 once the world map is read
uniform vec4 worldScale;    // planet radius in blocks, map reach in blocks, zw offset from the map's centre
uniform vec4 worldChunk;    // xy map centre inside its chunk, zw that chunk
uniform float worldYaw;     // where the player looks
```

The prelude also declares `panels`, `panelCount`, `veil`, `worldMap`, `worldKnown` and `worldTints`; do not
declare them again. After `main()` Komet darkens behind the panels by `veil`. A shader that does not compile is
logged and the blur stands in. Komet's Pragmatic ships a complete example in
`assets/komet/config/komet-styles/pragmatic/background.fsh`.

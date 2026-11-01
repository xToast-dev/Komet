# Shader reference

This page is for looking things up. If you are new, start with "Your mod for Komet": there you build a mod from Komet's template step by step. What a shader needs so that it also runs under Komet's Vulkan renderer is on the page "Shaders on Vulkan". Words like pass, define or uniform are explained on the page "Glossary" under Help.

## komet.json at a glance

The file sits in your mod at `assets/<your-modid>/config/komet.json`. Every field is optional. Even `{}` on its own announces your mod to Komet: it then shows under Shaders › Active shaders in the group "Made for Komet".

| Field | What it does |
|---|---|
| `$schema` | Makes your editor suggest fields and show mistakes (address below) |
| `_comment` | A note for readers; Komet does not read it |
| `name`, `author` | Title of your page under Shaders, and the author in the panel on the right under Active shaders; without them, your `modinfo.json` applies |
| `settings` | Switches, sliders and choices (see "Settings") |
| `programs` | Which shaders get your defines (see "Settings") |
| `passes` | Picture effects over the finished picture (see "Passes") |
| `renderer` | `"both"` (default) or `"opengl"`: with `"opengl"`, Komet uses OpenGL instead of Vulkan while your mod runs |
| `config` | Controls your mod's own settings window (see "Own settings windows") |
| `holds` | Komet features that are held back while your mod runs (see the same section) |

The schema address for `komet.json` is `https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet.schema.json`. For a pack to try things out, use the same address with `komet-shader.schema.json` at the end.

Your mod gets a page of its own under Shaders as soon as `settings` or `passes` has at least one entry.

## Komet's own path

- With Komet, every shader the game loads has the define `KOMET`, and under Komet's Vulkan renderer `KOMET_VULKAN` too. Your passes get both as well. Without Komet they are missing: with `#ifdef KOMET` the same shader runs with and without Komet.
- While Komet runs, `assets/<domain>/config/komet/shaders/<file>` replaces `assets/<domain>/shaders/<file>`; the same goes for `shaderincludes`. This works for the domain `game` too. The file it replaces must exist, or the log shows a warning. Without Komet, the game never reads these files.
- In C# code, `api.ModLoader.IsModEnabled("komet")` tells you whether Komet is running.

## Trying without a mod

A folder `ModConfig/komet/shaders/<name>/` with a `komet-shader.json` becomes a page of its own under Shaders. The fields are the same as in `komet.json`, except that `renderer`, `config` and `holds` do not exist here. Pass files sit in the same folder. Without `name`, the page is named after the folder, in lower case.

Modding › Templates › **Create example pack** creates a ready-made pack. It writes to `ModConfig/komet/shaders/example` (if the name is taken, to `example-2` and so on) and reloads the packs. The pack shows at once as the page "Example pack" under Shaders.

After changing a file, press Shaders › Active shaders › **Reload packs**. You do not need to leave the world.

## The example pack

The example pack has two files: `komet-shader.json` describes the settings, and `tint.fsh` does the drawing. Here is the first one (without the `_comment` line):

```json
{
  "$schema": "https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet-shader.schema.json",
  "name": "komet:shaderpack-example",
  "author": "Komet",
  "programs": [
    "final"
  ],
  "settings": [
    {
      "type": "group",
      "label": "komet:shaderpack-example-look"
    },
    {
      "key": "tint",
      "label": "komet:shaderpack-example-tint",
      "hint": "komet:shaderpack-example-tint-hint",
      "type": "switch",
      "default": 0,
      "define": "EXAMPLE_TINT"
    },
    {
      "key": "strength",
      "label": "komet:shaderpack-example-strength",
      "hint": "komet:shaderpack-example-strength-hint",
      "type": "slider",
      "min": 0,
      "max": 1,
      "step": 0.05,
      "default": 0.6,
      "uniform": "exampleStrength"
    }
  ],
  "passes": [
    {
      "id": "tint",
      "fragment": "tint.fsh",
      "when": "tint"
    }
  ]
}
```

| Part | What it does |
|---|---|
| `name`, `author` | Title of the page under Shaders, and the author in the panel on the right under Active shaders. `komet:shaderpack-example` is a lang key; plain text works too |
| `programs` | The game shader that gets the define: here `final`, the finished picture |
| `group` | A heading on the page |
| `tint` | A switch. Through `when` it turns the pass on and off. It also sets `EXAMPLE_TINT` in `final`, so every change rebuilds the shaders |
| `strength` | A slider from 0 to 1. It arrives as `uniform float exampleStrength` and takes effect at once |
| `hint` | The explanation the menu shows for a row |
| `passes` | A picture effect from the file `tint.fsh`, drawn over the finished picture |

This is `tint.fsh`:

```glsl
// Komet's example pass: the left half of the picture in red, the sky at half strength (depth 1 = sky).
// Komet puts the prelude in front: uv, color, scene, depth, resolution, time. exampleStrength is the pack's slider.
uniform float exampleStrength;

void main()
{
    vec3 c = texture(scene, uv).rgb;
    float sky = texture(depth, uv).r >= 1.0 ? 0.5 : 1.0;
    vec3 red = vec3(dot(c, vec3(0.3, 0.59, 0.11)) * 1.6, 0.0, 0.0);
    color = vec4(mix(c, red, exampleStrength * step(uv.x, 0.5) * sky), 1.0);
}
```

1. `uniform float exampleStrength` fetches the slider's value.
2. `scene` is the picture so far, `uv` the position on screen, from 0 to 1. Komet puts both in front of your file.
3. `depth` is the depth; 1 means sky. There, `sky` is 0.5, elsewhere 1: in the sky the effect is half as strong.
4. `red` is the picture's brightness, in the red channel only.
5. `color` is what the pass draws. `step(uv.x, 0.5)` is 1 left of the middle and 0 right of it. On the left the picture is mixed towards red, as strongly as the slider says.

Modding › Templates › **Create mod template** creates a whole mod with a pass. "Your mod for Komet" shows how to make it your own.

## Settings

Each entry in `settings` becomes a row on your pack's page.

| Field | Meaning |
|---|---|
| `key` | Unique name in the pack; Komet stores the player's value under it |
| `label` | The row's name in the menu |
| `hint` | The row's explanation |
| `type` | `switch` (default), `slider`, `choice` or `group` |
| `default` | The starting value (default `0`) |
| `min`, `max`, `step` | A slider's range and step (default `0`, `1` and `0.1`) |
| `unit` | The unit after a slider's value, for example `EV` |
| `options` | The names in a choice |
| `define` | Becomes `#define NAME value`; a change rebuilds the shaders |
| `uniform` | Becomes `uniform float NAME`; takes effect at once |

- `switch` gives 0 or 1, `choice` the number of the entry in `options` from 0, `slider` a number from `min` to `max`.
- As a define, a switch or a choice becomes a whole number, a slider a number with a decimal point, for example `0.6`.
- `group` is only a heading with the text from `label`.
- A slider needs `max` greater than `min` and `step` greater than 0; a choice needs `options`. Otherwise Komet shows the row as a switch.
- `name`, `label`, `hint` and the names in `options` may be lang keys from your lang files (`domain:key`) or plain text.
- `programs` names the shaders that get the defines: game programs like `final` or `chunkopaque`, or one of your passes as `pass:<id>`. Without `programs`, every shader gets the defines, all your passes included.
- Uniforms reach every shader and every pass that declares them, whatever `programs` says.
- A pack has at most 40 settings. The players' values are kept in `ModConfig/komet/shader-settings.json`.

## Passes

A pass is a fragment file Komet draws over the whole picture. It runs after the game has finished mixing its picture (bloom and god rays included) and before the GUI.

| Field | Meaning |
|---|---|
| `id` | The pass's name, unique within your mod |
| `fragment` | The file: in `komet.json` from `assets/<your-modid>/`, for example `shaders/grade.fsh`; in a pack for trying things out, in the same folder |
| `when` | The `key` of a switch: the pass draws while it is on |
| `order` | The order across all passes of all mods, lower values first (default `0`) |

- A pass reads a copy of the picture so far (`scene`) and overwrites every pixel with `color`.
- A path with `..` or an absolute path is not allowed.
- All mods together have at most 32 passes.

Komet puts these lines in front of your file. You only write `main()` and set `color`. Do not declare these names again:

```glsl
#version 330 core
#define KOMET 1
#define KOMET_VULKAN 1          // only while Vulkan draws
// then the defines your komet.json gives this pass
in vec2 uv;                     // 0..1, (0, 0) bottom left
out vec4 color;                 // what the pass draws
uniform sampler2D scene;        // the picture so far (a copy)
uniform sampler2D depth;        // the scene's depth, 0..1, 1 = sky
uniform vec2 resolution;        // in pixels
uniform float time;             // seconds, for animation
```

Then comes `#line 1`: line numbers in error messages count from your file.

## From code: KometShaders

In C#, you register the same and more through `KometShaders`:

```csharp
var q = KometShaders.Define("mymod", "MY_QUALITY", () => quality.ToString(), "final");
var b = KometShaders.Uniform("mymod", "myBloom", () => bloom);
var e = KometShaders.Edit("mymod", "final", null, src => src.Replace("a", "b"));
var p = KometShaders.AddPass(new KometPass("mymod", "grade", KometPassStage.Final, source)
{
    Order = 10,
    Enabled = () => config.Grade,
    Uniforms = { ["myStrength"] = () => [config.Strength] }
});
KometShaders.Reload(); // after a define's value changed
```

- Only call the API when `api.ModLoader.IsModEnabled("komet")` is true, and only on the main thread.
- `Define`, `Uniform`, `Edit` and `AddPass` return an `IDisposable`. `Dispose()` takes the registration back. Every registration ends with the world: register again in `StartClientSide` each time you join a world.
- `Define` reads its value each time the shaders load. When it changes, call `Reload()`. You name the programs as in `programs`.
- `Uniform` takes one number or 1 to 4 numbers (`float` to `vec4`). Komet asks for the value once per frame.
- `Edit` rewrites a program's source as it loads. `"*"` stands for every program. Komet's own edits come first, so do not search for whole lines.
- `AddPass` draws a pass like one from `passes`. Komet checks `Enabled` every frame. If it throws, the pass turns off.
- `KometShaders.Renderer` tells you which renderer is drawing. `RendererChanged` reports a switch. Komet rebuilds the shaders itself when that happens.
- All mods together have at most 128 defines, 256 uniforms, 64 edits and 32 passes.

Build a settings page of your own with `KometOptions` (page "API for mods").

## Mods from the database

Shader mods that do not know Komet need no changes. Komet finds them by their shader files when you enter a world and lists them under Shaders › Active shaders in the group "Other shader mods".

- Komet shows whether each of their shaders compiles under OpenGL. Under Vulkan, Komet translates them itself, including the ones not drawn yet, and shows whether that worked. A red exclamation mark after the name means that one did not make it. The panel on the right names it and the reason.
- A mod that replaces shaders of the game or of another mod is **off** at first: Komet does not start it, and the game uses its own files. A note in the chat says so when you enter the world. Its switch starts it at once.
- Switching such a mod off pauses it at once: its effects go and the game draws with its own shaders again. Switch it on and it carries on where it was. From the next time you enter a world it stays off entirely. This holds for mods made only of shader files too.
- If a mod it depends on is missing or loaded in another major version, Komet does not start it, and its card says why.

A mod with a `komet.json` shows under "Made for Komet" instead and runs until you switch it off. Its settings and passes then stop at once, and so does what its code registered through `KometShaders`. The code itself runs until you next enter a world, unless the mod replaces the game's shaders: then it pauses at once, as above. A mod from the database gets sliders of its own only once it has a `komet.json` (see "Your mod for Komet").

## What the symbols mean

Small symbols follow each name under Active shaders. The first, coloured symbol shows who steers whom:

| Symbol | Meaning |
|---|---|
| Chain link on green | **Integrated**: the mod announces itself, with a `komet.json`, as a pack or through the C# API |
| Comet on blue | **Steered by Komet**: the mod knows nothing of Komet; Komet steers it through a bundled adapter |
| Eye on grey | **Not integrated**: Komet recognises the mod by its files and steers nothing |

The second, coloured symbol shows where the mod runs now. A held-back mod has none:

| Symbol | Meaning |
|---|---|
| Volcano on red | **Vulkan**: runs on Komet's Vulkan renderer, every shader translated |
| Volcano on orange | **Vulkan, partial**: not every shader could be translated |
| Cube on teal | **OpenGL**: runs on OpenGL; if the mod draws with OpenGL only, its card says so |

The grey symbols after it give details:

| Symbol | Meaning |
|---|---|
| Sheet with lines | **komet.json**: announced through its `komet.json` |
| Folder | **Local pack**: a shader pack from your folder |
| `< >` | **C# API**: announced through `KometShaders` |
| Two sheets | **Komet variant**: brings files of its own for Komet (`config/komet/`) |
| Two bars | **Paused**: Komet holds the mod back; it is not running now |
| Red exclamation mark | **Problem**: a shader does not compile or could not be translated, or a dependency is missing or does not fit |

With no row chosen, the panel on the right shows this legend. Choose a mod and it shows the mod's card: how it is bound in, where it runs, how many shaders compile, what it replaces and whether a dependency is missing or does not fit. A triangle marks its shaders there, two arrows what it replaces.

## Own settings windows

Some shader mods have a settings window of their own, SheyderMod for example. Komet can tie their settings file to Komet's preset. The `"config"` block in their `komet.json` controls this:

| Field | Meaning |
|---|---|
| `file` | The mod's settings file in `ModConfig` |
| `reload` | The command that makes the mod read the file again |
| `levels` | For `quality`, `balanced` and `speed`: `from` names a profile file from `assets/<modid>/config/`, `set` changes values in it |
| `switches` | Switches for single values: `key` is a dotted path like `Fog.Enabled`, with `label` and `hint` |

```json
{
  "config": {
    "file": "mymod.json",
    "reload": ".mymod reload",
    "levels": {
      "quality": { "from": "default.json" },
      "speed": { "from": "fast.json", "set": { "Fog.Enabled": false } }
    },
    "switches": [ { "key": "Fog.Enabled", "label": "mymod:fog" } ]
  },
  "holds": [ { "feature": "FrustumSweep", "reason": "Our shadows need the game's own culling" } ]
}
```

While such a mod runs, it has a page of its own under Shaders:

- **Level**: "As Komet" follows Komet's preset (Komet › Overview › **Settings**). If that is set to "Automatic (recommended)", it counts as Balanced, or as Speed on a weak computer. "Quality", "Balanced" and "Speed" fix the level. "Own" leaves the mod's file alone; then its own window applies.
- **Effects**: the switches from `switches`. They show the value Komet wrote last. With "Own" they are locked. A new level resets them.

Komet builds the file anew when you enter the world, when you change something on the page and, with "As Komet", when the preset changes. For this it takes the profile from `from` (without `from`, the file as it is), puts `set` on top and your switches on top of that. It writes the file to `ModConfig/<file>` and runs the command from `reload`. The mod then applies it at once. If `levels` lacks the level, Komet writes nothing. Before it first overwrites the file, Komet keeps a copy ending in `.before-komet`, for example `sheydermod.json.before-komet`.

`"holds"` holds Komet features back while the mod runs: `feature` is the name of a Komet feature as for `KometFeatures.HoldOff` (see "API for mods"), `reason` says why. `"renderer": "opengl"` holds Komet's Vulkan renderer back the same way.

For mods without a `komet.json` of their own, an adapter file under `assets/<domain>/config/komet-adapters/<name>.json` in any mod does the same. There the fields are `mod` (the mod's id), `config` (instead of `file`), `reload`, `levels`, `settings` (instead of `switches`) and `holds`. Komet ships such a file for SheyderMod. It holds the Vulkan renderer back, as SheyderMod draws into OpenGL framebuffers that Komet's Vulkan renderer cannot show.

## When something fails

Nearly every problem shows up in `client-main.log`. Search for these lines:

| Line in the log | Meaning |
|---|---|
| `Komet shaders: ... is not a valid komet-shader.json` | A JSON mistake with line and position, in a `komet.json` too |
| `Komet shaders: pack ... names the pass file ...` | The file from `fragment` is missing, or its path is not allowed |
| `Komet shaders: pass ... is left out` | The pass does not compile or link, or its `Enabled` throws. The reason follows; the line number counts from your file |
| `Komet shaders: ... which is no GLSL name` | A `define` or `uniform` is not a valid GLSL name (64 characters at most) |
| `Komet: Vulkan cannot draw` | Something is missing only under Vulkan (see "Shaders on Vulkan") |

- When a shader the game loads does not compile, Komet writes its full source to `Logs/komet-shader-<file>.txt`. The line number in the error matches that file exactly.
- If the menu shows a slider as a switch, check `min`, `max` and `step`. A choice without `options` becomes a switch too.
- Komet adds **Distant shadows** to the include files `shadowcoords.vsh` and `fogandlight.fsh` (values with `kometDistant` in their names). If your mod replaces these files and Komet does not recognise their text, distant shadows stay off. The log then says `Komet: the shadow shaders are not the engine's, distant shadows stay off`.

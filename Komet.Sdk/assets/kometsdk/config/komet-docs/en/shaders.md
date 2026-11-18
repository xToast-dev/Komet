# Shaders and passes

[API: KapiShaders](docs-api-shaders)

## komet.json at a glance

The file sits in your mod at `assets/<your-modid>/config/komet.json`. Every field is optional. Even `{}` on its own
announces your mod to Komet: it then shows under Shaders › Active shaders in the group "Made for Komet".

| Field            | What it does                                                                                                                               |
|------------------|--------------------------------------------------------------------------------------------------------------------------------------------|
| `$schema`        | Makes your editor suggest fields and show mistakes (address below)                                                                         |
| `_comment`       | A note for readers; Komet does not read it                                                                                                 |
| `name`, `author` | Title of your page under Shaders, and the author in the panel on the right under Active shaders; without them, your `modinfo.json` applies |
| `settings`       | Switches, sliders and choices (see "Settings")                                                                                             |
| `programs`       | Which shaders get your defines (see "Settings")                                                                                            |
| `passes`         | Picture effects over the finished picture (see "Passes")                                                                                   |
| `renderer`       | `"both"` (default) or `"opengl"`: with `"opengl"`, Komet uses OpenGL instead of Vulkan while your mod runs                                 |
| `config`         | Controls your mod's own settings window (see "Own settings windows")                                                                       |
| `holds`          | Komet features that are held back while your mod runs (see the same section)                                                               |

The schema address for `komet.json` is
`https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet.schema.json`. For a pack to try things out,
use the same address with `komet-shader.schema.json` at the end.

Your mod gets a page of its own under Shaders as soon as `settings` or `passes` has at least one entry.

## Settings

Each entry in `settings` becomes a row on your pack's page.

| Field                | Meaning                                                           |
|----------------------|-------------------------------------------------------------------|
| `key`                | Unique name in the pack; Komet stores the player's value under it |
| `label`              | The row's name in the menu                                        |
| `hint`               | The row's explanation                                             |
| `type`               | `switch` (default), `slider`, `choice` or `group`                 |
| `default`            | The starting value (default `0`)                                  |
| `min`, `max`, `step` | A slider's range and step (default `0`, `1` and `0.1`)            |
| `unit`               | The unit after a slider's value, for example `EV`                 |
| `options`            | The names in a choice                                             |
| `define`             | Becomes `#define NAME value`; a change rebuilds the shaders       |
| `uniform`            | Becomes `uniform float NAME`; takes effect at once                |

- `switch` gives 0 or 1, `choice` the number of the entry in `options` from 0, `slider` a number from `min` to `max`.
- As a define, a switch or a choice becomes a whole number, a slider a number with a decimal point, for example `0.6`.
- `group` is only a heading with the text from `label`.
- A slider needs `max` greater than `min` and `step` greater than 0; a choice needs `options`. Otherwise Komet shows the
  row as a switch.
- `name`, `label`, `hint` and the names in `options` may be lang keys from your lang files (`domain:key`) or plain text.
- `programs` names the shaders that get the defines: game programs like `final` or `chunkopaque`, or one of your passes
  as `pass:<id>`. Without `programs`, every shader gets the defines, all your passes included.
- Uniforms reach every shader and every pass that declares them, whatever `programs` says.
- A pack has at most 40 settings. The players' values are kept in `ModConfig/komet/shader-settings.json`.

## Passes

A pass is a fragment file Komet draws over the whole picture. It runs after the game has finished mixing its picture
(bloom and god rays included) and before the GUI.

| Field      | Meaning                                                                                                                                     |
|------------|---------------------------------------------------------------------------------------------------------------------------------------------|
| `id`       | The pass's name, unique within your mod                                                                                                     |
| `fragment` | The file: in `komet.json` from `assets/<your-modid>/`, for example `shaders/grade.fsh`; in a pack for trying things out, in the same folder |
| `when`     | The `key` of a switch: the pass draws while it is on                                                                                        |
| `order`    | The order across all passes of all mods, lower values first (default `0`)                                                                   |

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

## What the symbols mean

Small symbols follow each name under Active shaders. The first, coloured symbol shows who steers whom:

| Symbol              | Meaning                                                                                         |
|---------------------|-------------------------------------------------------------------------------------------------|
| Chain link on green | **Integrated**: the mod announces itself, with a `komet.json`, as a pack or through the C# API  |
| Comet on blue       | **Steered by Komet**: the mod knows nothing of Komet; Komet steers it through a bundled adapter |
| Eye on grey         | **Not integrated**: Komet recognises the mod by its files and steers nothing                    |

The second, coloured symbol shows where the mod runs now. A held-back mod has none:

| Symbol            | Meaning                                                                         |
|-------------------|---------------------------------------------------------------------------------|
| Volcano on red    | **Vulkan**: runs on Komet's Vulkan renderer, every shader translated            |
| Volcano on orange | **Vulkan, partial**: not every shader could be translated                       |
| Cube on teal      | **OpenGL**: runs on OpenGL; if the mod draws with OpenGL only, its card says so |

The grey symbols after it give details:

| Symbol               | Meaning                                                                                                       |
|----------------------|---------------------------------------------------------------------------------------------------------------|
| Sheet with lines     | **komet.json**: announced through its `komet.json`                                                            |
| Folder               | **Local pack**: a shader pack from your folder                                                                |
| `< >`                | **C# API**: announced through KAPI (`komet.Shaders`)                                                                  |
| Two sheets           | **Komet variant**: brings files of its own for Komet (`config/komet/`)                                        |
| Two bars             | **Paused**: Komet holds the mod back; it is not running now                                                   |
| Red exclamation mark | **Problem**: a shader does not compile or could not be translated, or a dependency is missing or does not fit |

With no row chosen, the panel on the right shows this legend. Choose a mod and it shows the mod's card: how it is bound
in, where it runs, how many shaders compile, what it replaces and whether a dependency is missing or does not fit. A
triangle marks its shaders there, two arrows what it replaces.

## When something fails

Nearly every problem shows up in `client-main.log`. Search for these lines:

| Line in the log                                       | Meaning                                                                                                               |
|-------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------|
| `Komet shaders: ... is not a valid komet-shader.json` | A JSON mistake with line and position, in a `komet.json` too                                                          |
| `Komet shaders: pack ... names the pass file ...`     | The file from `fragment` is missing, or its path is not allowed                                                       |
| `Komet shaders: pass ... is left out`                 | The pass does not compile or link, or its `Enabled` throws. The reason follows; the line number counts from your file |
| `Komet shaders: ... which is no GLSL name`            | A `define` or `uniform` is not a valid GLSL name (64 characters at most)                                              |
| `Komet: Vulkan cannot draw`                           | Something is missing only under Vulkan (see "Renderer integration for mods")                                                      |

- When a shader the game loads does not compile, Komet writes its full source to `Logs/komet-shader-<file>.txt`. The
  line number in the error matches that file exactly.
- If the menu shows a slider as a switch, check `min`, `max` and `step`. A choice without `options` becomes a switch
  too.
- Komet adds **Distant shadows** to the include files `shadowcoords.vsh` and `fogandlight.fsh` (values with
  `kometDistant` in their names). If your mod replaces these files and Komet does not recognise their text, distant
  shadows stay off. The log then says `Komet: the shadow shaders are not the engine's, distant shadows stay off`.

## Shader Studio

The Shader Studio comes with Komet: open it with **Ctrl+F8** or `.komet dev`. Every other tool lives in the HUD window
(Ctrl+F7).

- **One tree** from your Vintagestory folder: game files are read-only, projects are folders under
  `ModConfig/komet/shaders`. A project holds copies of game shaders that replace the originals while it is on. The
  survival and creative shaders (galaxy, rift, machinegear) are editable too.
- **Editor** with suggestions, search, history and the last good state; problems are listed per renderer with the line.
  **TEST** (Ctrl+S) builds now, **AUTO** builds on every change. A shader that fails keeps the last good state.
- **Game runs: My version / Vanilla** chooses what the game draws; it stays as chosen when the Studio closes.
- **Force in the game** draws the open shader although its conditions hide it: aurora, god rays, SSAO, bloom, FXAA,
  clouds, shadows and particles. Other shaders are always drawn.
- The **3D preview** shows the shader on a model, with climate and season sliders.
- **Export as mod** writes the project as a shader mod. `.komet shadertest` renders every installed shader in the 3D view
  and saves pictures and a report under `Logs/komet-shadertest`.

A mod shader under `assets/<your-modid>/shaders/` stays a pass of its own and replaces no game shader by itself. Name it
in your `komet.json`.

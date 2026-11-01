# My Shaders: a Komet shader mod template

A complete shader mod that runs on the game's OpenGL and on Komet's Vulkan renderer, without a line of Vulkan code.
Komet ports every shader to Vulkan by itself; this template shows how to write shaders that port cleanly and how to
plug them into Komet: settings in Komet's options, passes over the finished picture, and an optional C# part.

In game, Komet's options › Modding › "Shaders on Vulkan" is the guide to this template; "Shader reference" there
lists every field of komet.json. Full documentation (API reference, render passes, Vulkan rules): the Komet developer
site, built from the Komet repository with `scripts/apidocs.sh`.

## What is in it

```
README.md                                       this file
myshaders/                                      the mod, ready for the Mods folder
  modinfo.json                                  rename "myshaders" to your id
  assets/myshaders/config/komet.json            the whole binding to Komet: settings, passes, renderer
  assets/myshaders/shaders/haze.fsh             pass 1: distance haze, reads the depth
  assets/myshaders/shaders/grade.fsh            pass 2: colour grading, a define and two uniforms
  assets/myshaders/lang/en.json, de.json        the texts of the settings (myshaders:key in komet.json)
code/                                           optional C#: feeds the game's state into the shaders
  MyShaders.csproj                              builds MyShaders.dll into myshaders/
  MyShadersSystem.cs                            the mod system: calls the bridge only when Komet runs
  KometBridge.cs                                the only class that names Komet's types
```

The code sits beside the mod, not in it: the game refuses a zipped mod that holds `.cs` files outside `src/`.

## Try it

1. Copy the folder `myshaders` into your Mods folder (VintagestoryData/Mods). Komet's options › Modding › Templates ›
   Mods folder opens it.
2. Enter a world. Shaders › Active shaders lists "My Shaders" under "Made for Komet", and Shaders has a page of its
   own for it.
3. Change Exposure: it changes at once (a uniform). Change Quality: the shaders are built again (a define).
4. Switch Komet › Overview › Renderer between OpenGL and Vulkan. The picture must look the same.

## Make it yours

1. Pick an id (lower case, no spaces) and replace `myshaders` everywhere: the folder under `assets/` (it must match
   the modid), `modid` in `modinfo.json`, the `myshaders:` lang keys in `komet.json`, the uniform names and the
   `MYSHADERS_` defines.
2. Edit the passes. Save, then Shaders › Active shaders › Reload packs shows the change without leaving the world.
3. A pass that does not compile is left out; the detail panel of Shaders › Active shaders and `client-main.log`
   (lines with `Komet shaders: pass`) name the line. Line numbers are your file's.

## How the pieces connect

| In komet.json | Reaches the shader as | Changes |
|---|---|---|
| `"uniform": "myshadersExposure"` | `uniform float myshadersExposure;` you declare | at once |
| `"define": "MYSHADERS_QUALITY"` | `#define MYSHADERS_QUALITY 0` or `1` before your file | rebuilds the shaders |
| `"programs": ["pass:grade", ...]` | which passes (or game programs) get the defines | |
| `"when": "grade"` on a pass | the switch that turns the pass on and off | at once |
| `"order"` on a pass | lower draws first: haze (10) before grade (20) | |

Every pass gets `#define KOMET 1`, and `#define KOMET_VULKAN 1` while Komet's Vulkan renderer draws.

## Rules for shaders that run on Vulkan

Komet compiles your GLSL with OpenGL as always; under Vulkan it ports the same program to SPIR-V. Keep to these and
the port never fails:

- Write `#version 330 core` GLSL (the pass prelude does it for you): `in`/`out`, `texture()`, no `gl_FragColor`,
  `varying`, `attribute` or `texture2D`.
- Uniforms: `float`, `vec2` to `vec4`, `int` to `ivec4` and `mat4`. Avoid `uint` uniforms, `mat2`/`mat3` uniforms and
  uniform structs: their values do not reach Vulkan.
- No interface blocks (`out Block { ... }`), no geometry or tessellation shaders, no sampler arrays.
- Samplers: `sampler2D`, `sampler2DShadow`, `samplerCube`, `sampler2DArray`; at most 32 per program.
- Array sizes must be plain numbers (or `+` and `*` of them) after the preprocessor.
- Coordinates are the same on both renderers: `uv` (0, 0) is bottom left, depth is 0..1 with 1 the sky,
  `gl_FragCoord` counts from the bottom. Do not flip anything for Vulkan.
- Test once with each renderer. Under Vulkan a program that does not port is logged as
  `Komet: Vulkan cannot draw ... (no Vulkan port: ...)` and is missing from the picture.

## The optional code part

`code/` adds C# for what a JSON file cannot do; here it hands the hour of the day to the haze pass.

1. Build: `dotnet build -c Release -p:VintageStory=<game folder> -p:Komet=<path to Komet.Core.dll>`.
2. `MyShaders.dll` lands in `myshaders/` next to `modinfo.json`; set `"type": "code"` there.
3. Komet stays optional: `MyShadersSystem` calls `KometBridge` only when `IsModEnabled("komet")`, and
   `Komet.Core.dll` (the public API) is referenced with `Private="false"`, never shipped.

Use the mod's domain as the mod id in code (`"myshaders"`): only then does Komet hand the code's defines to the passes
from `komet.json`. Uniforms reach every pass that declares them anyway.

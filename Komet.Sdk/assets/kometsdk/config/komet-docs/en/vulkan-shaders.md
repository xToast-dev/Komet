# Renderer integration for mods

[API: KapiShaders](docs-api-shaders)

[Optional Komet integration](docs-integration)

## Rules for clean ports

A port is the translated version of your shader. Keep to these rules and it always works:

- Write GLSL as for OpenGL 3.3 core (`#version 330 core`; in a pass Komet writes that line itself): `in` and `out`,
  `texture()`. No `gl_FragColor`, `varying`, `attribute` or `texture2D`.
- Uniforms: `float`, `vec2` to `vec4`, `int` to `ivec4` and `mat4`. Avoid `uint` uniforms, `mat2`, `mat3` and uniform
  structs: their values do not reach Vulkan.
- Samplers: `sampler2D`, `sampler2DShadow`, `samplerCube` and `sampler2DArray`, at most 32 per program. No sampler
  arrays: what draws with them is missing from the picture under Vulkan.
- Array sizes must be plain numbers after the preprocessor, or sums and products of them, for example `4 * 3 + 1`.
- A uniform's starting value (`uniform float a = 0.5;`) must be a number or a constructor of numbers like
  `vec3(1.0, 0.5, 0.0)`.
- No interface blocks (`out Block { ... }`), no geometry or tessellation shaders.
- Uniform blocks and storage buffers work, with a `binding` from 0 to 15.
- The inputs of a vertex shader of your own need a fixed location: `layout(location = 0) in vec3 pos;` or
  `glBindAttribLocation` in code.
- Coordinates are the same on both renderers: `uv` (0, 0) is bottom left, depth runs from 0 to 1 with 1 for the sky,
  `gl_FragCoord` counts from the bottom. Do not flip anything for Vulkan.

## What Vulkan does not show

Under Vulkan, Komet draws everything itself, including what other mods draw with OpenGL directly, as far as Vulkan can
express it. What it cannot express is **missing from the picture**. OpenGL does not fill the gap, as its picture is
never shown under Vulkan.

- A picture effect as a pass (`passes` in `komet.json` or `komet.Shaders.AddPass`) runs on both renderers. That is the
  safe way.
- Komet often takes over a mod's own OpenGL framebuffers, but not always. The log names what fails in a line with
  `Komet: Vulkan cannot draw`.
- If you cannot change that, write `"renderer": "opengl"` in your `komet.json`. While your mod runs, Komet then uses
  OpenGL instead of Vulkan, and the mod's card under Active shaders says why. Komet does the same for SheyderMod.

## Testing and the log

1. Set Komet › Overview › **Renderer** to OpenGL and look at your picture. Then set it to Vulkan: the picture must look
   the same. Once applied, the switch takes effect at once.
2. Choose your mod under Shaders › Active shaders. Its card says whether every shader is translated for Vulkan, or names
   the first one that did not make it.
3. Search `client-main.log` for these lines:

| Line in the log                                                 | Meaning                                                                        |
|-----------------------------------------------------------------|--------------------------------------------------------------------------------|
| `Komet: Vulkan cannot draw X (no Vulkan port: ...)`             | The port of X failed; the reason names the rule. X is missing from the picture |
| `Komet: Vulkan cannot draw ...: it is missing from the picture` | A draw that Vulkan cannot take over                                            |
| `Komet: Vulkan port of X: 35 ms ...`                            | Timing only: the port worked                                                   |
| `Komet shaders: pass ... is left out`                           | A pass does not even compile under OpenGL                                      |

X is the program's name, for example `chunkopaque`. For a pass, or a program a mod builds with OpenGL directly, a number
appears there, such as `program 123`. Programs a mod registers through the game's shader API carry their name. If a
`Vulkan port of` line shows a reason instead of the milliseconds, the port failed too.

## The Vulkan template

Modding › Templates › **Shader mod project with optional C# integration** writes a complete example to
`ModConfig/komet/mod-template/myshaders-dev` and opens the folder. It runs on OpenGL and Vulkan, follows every rule
above, and its `README.md` explains each file. The C# part sits in `code/` beside the mod, because the game refuses a
zipped mod that holds `.cs` files outside `src/`.

1. Copy the folder `myshaders` into the Mods folder (Modding › Templates › **Mods folder**) and join the world again.
2. Change **Exposure** (a uniform, at once) and **Quality** (a define, rebuilds the shaders).
3. Switch Komet › Overview › **Renderer** between OpenGL and Vulkan. The picture must look the same.

After validating your mod on both renderers, set `"renderer": "both"` in your `komet.json`. This also overrides a
Vulkan hold from an adapter Komet ships for your mod (as for SheyderMod).

## How your shader runs

You do not write a single line of Vulkan code. You write GLSL as for the game, and Komet does the rest:

```flow
The game loads your shader as always
Komet puts KOMET and your defines in front
OpenGL compiles it as always
Under Vulkan, Komet reads the same source
shaderc translates it to SPIR-V
Vulkan draws it in the same frame
```

- `KOMET` is always set with Komet, `KOMET_VULKAN` only while Komet's Vulkan renderer draws. When the renderer switches,
  Komet rebuilds every shader and pass itself, so `KOMET_VULKAN` is right again.
- For mods with shader files of their own, Komet also translates the shaders not drawn yet. Shaders › Active shaders
  shows whether it worked.
- With **Vulkan: save shaders** (Komet › Vulkan, shown with Komet › Tools › **Show advanced options**) Komet keeps
  translated shaders on disk. When you change your shader, Komet translates it again.
- Most mods never need `#ifdef KOMET_VULKAN`. Use it only for work that pays off only under Vulkan, or to work around a
  difference you found.

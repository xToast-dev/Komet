# Shaders on Vulkan

This page is for shader authors. It shows how Komet draws your shaders under its Vulkan renderer, which rules apply, and what is in the Vulkan template. How Vulkan works for players is on the page "Vulkan explained" under Help. The fields of `komet.json` are on the page "Shader reference", words like pass or uniform on "Glossary" under Help.

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

- `KOMET` is always set with Komet, `KOMET_VULKAN` only while Komet's Vulkan renderer draws. When the renderer switches, Komet rebuilds every shader and pass itself, so `KOMET_VULKAN` is right again.
- For mods with shader files of their own, Komet also translates the shaders not drawn yet. Shaders › Active shaders shows whether it worked.
- With **Vulkan: save shaders** (Komet › Vulkan, shown with Komet › Tools › **Show advanced options**) Komet keeps translated shaders on disk. When you change your shader, Komet translates it again.
- Most mods never need `#ifdef KOMET_VULKAN`. Use it only for work that pays off only under Vulkan, or to work around a difference you found.

## Rules for clean ports

A port is the translated version of your shader. Keep to these rules and it always works:

- Write GLSL as for OpenGL 3.3 core (`#version 330 core`; in a pass Komet writes that line itself): `in` and `out`, `texture()`. No `gl_FragColor`, `varying`, `attribute` or `texture2D`.
- Uniforms: `float`, `vec2` to `vec4`, `int` to `ivec4` and `mat4`. Avoid `uint` uniforms, `mat2`, `mat3` and uniform structs: their values do not reach Vulkan.
- Samplers: `sampler2D`, `sampler2DShadow`, `samplerCube` and `sampler2DArray`, at most 32 per program. No sampler arrays: what draws with them is missing from the picture under Vulkan.
- Array sizes must be plain numbers after the preprocessor, or sums and products of them, for example `4 * 3 + 1`.
- A uniform's starting value (`uniform float a = 0.5;`) must be a number or a constructor of numbers like `vec3(1.0, 0.5, 0.0)`.
- No interface blocks (`out Block { ... }`), no geometry or tessellation shaders.
- Uniform blocks and storage buffers work, with a `binding` from 0 to 15.
- The inputs of a vertex shader of your own need a fixed location: `layout(location = 0) in vec3 pos;` or `glBindAttribLocation` in code.
- Coordinates are the same on both renderers: `uv` (0, 0) is bottom left, depth runs from 0 to 1 with 1 for the sky, `gl_FragCoord` counts from the bottom. Do not flip anything for Vulkan.

## What Vulkan does not show

Under Vulkan, Komet draws everything itself, including what other mods draw with OpenGL directly, as far as Vulkan can express it. What it cannot express is **missing from the picture**. OpenGL does not fill the gap, as its picture is never shown under Vulkan.

- A picture effect as a pass (`passes` in `komet.json` or `KometShaders.AddPass`) runs on both renderers. That is the safe way.
- Komet often takes over a mod's own OpenGL framebuffers, but not always. The log names what fails in a line with `Komet: Vulkan cannot draw`.
- If you cannot change that, write `"renderer": "opengl"` in your `komet.json`. While your mod runs, Komet then uses OpenGL instead of Vulkan, and the mod's card under Active shaders says why. Komet does the same for SheyderMod.

## Testing and the log

1. Set Komet › Overview › **Renderer** to OpenGL and look at your picture. Then set it to Vulkan: the picture must look the same. Once applied, the switch takes effect at once.
2. Choose your mod under Shaders › Active shaders. Its card says whether every shader is translated for Vulkan, or names the first one that did not make it.
3. Search `client-main.log` for these lines:

| Line in the log | Meaning |
|---|---|
| `Komet: Vulkan cannot draw X (no Vulkan port: ...)` | The port of X failed; the reason names the rule. X is missing from the picture |
| `Komet: Vulkan cannot draw ...: it is missing from the picture` | A draw that Vulkan cannot take over |
| `Komet: Vulkan port of X: 35 ms ...` | Timing only: the port worked |
| `Komet shaders: pass ... is left out` | A pass does not even compile under OpenGL |

X is the program's name, for example `chunkopaque`. For a pass, or a program a mod builds with OpenGL directly, a number appears there, such as `program 123`. Programs a mod registers through the game's shader API carry their name. If a `Vulkan port of` line shows a reason instead of the milliseconds, the port failed too.

## The Vulkan template

Modding › Templates › **Create Vulkan template** writes a complete shader package to `ModConfig/komet/mod-template/myshaders-dev` (if the name is taken, to `myshaders-dev-2` and so on) and opens the folder. It runs on OpenGL and on Komet's Vulkan renderer and follows every rule above.

### What is in it

```text
myshaders-dev/
  README.md                         the guide, in English
  myshaders/                        the mod, ready for the Mods folder
    modinfo.json                    rename "myshaders" to your id
    assets/myshaders/
      config/komet.json             settings, passes, renderer
      shaders/haze.fsh              pass 1: distance haze from the depth
      shaders/grade.fsh             pass 2: colour grading with a define
      lang/en.json, lang/de.json    the texts of the settings
  code/                             optional C# part
    MyShaders.csproj
    MyShadersSystem.cs
    KometBridge.cs
```

The code sits beside the mod, not in it: the game refuses a zipped mod that holds `.cs` files outside `src/`.

### Try it

1. Open Modding › Templates › **Mods folder** and copy the folder `myshaders` from `myshaders-dev` into it.
2. Enter the world again. Shaders › Active shaders lists "My Shaders" in the group "Made for Komet". The mod also has a page of its own under Shaders.
3. Change **Exposure**: the picture changes at once (a uniform). Change **Quality**: the shaders are built again (a define).
4. Switch Komet › Overview › **Renderer** between OpenGL and Vulkan. The picture must look the same.

### Make it yours

1. Pick an id in lower case without spaces. Replace `myshaders` everywhere: the folder under `assets/`, `modid` in `modinfo.json`, the `myshaders:` lang keys in `komet.json`, the uniform names, the `MYSHADERS_` defines and, in the code part, the value of `Mod` in `KometBridge.cs`.
2. Change the passes and save. Shaders › Active shaders › **Reload packs** shows the change without a restart.
3. If a pass does not compile, Komet leaves it out. The panel on the right under Active shaders and `client-main.log` (lines with `Komet shaders: pass`) name the line. Line numbers count from your file.

### From komet.json to shader

| In komet.json | In the shader | Takes effect |
|---|---|---|
| `"uniform": "myshadersExposure"` | `uniform float myshadersExposure;`, declared by you | at once |
| `"define": "MYSHADERS_QUALITY"` | `#define MYSHADERS_QUALITY 0` or `1` before your file | rebuilds the shaders |
| `"programs": ["pass:grade", "pass:haze"]` | which passes get the defines | |
| `"when": "grade"` on a pass | the switch that turns the pass on and off | at once |
| `"order"` on a pass | lower values first: haze (10) before grade (20) | |

Every pass gets `#define KOMET 1` and, while Komet's Vulkan renderer draws, `#define KOMET_VULKAN 1` too.

### The code part

`code/` gives the mod C# for what a JSON file cannot do. In the template it hands the game's time of day to the haze pass. That way the haze turns dark at night. The heart of it sits in `KometBridge.cs`:

```csharp
KometShaders.Define(Mod, "MYSHADERS_CODE", () => "1", "pass:haze");
KometShaders.Uniform(Mod, "myshadersHour", () => api.World.Calendar.HourOfDay);
```

1. Build the part in the folder `code/` with `dotnet build -c Release -p:VintageStory=<game folder> -p:Komet=<path to Komet.Core.dll>`. Instead of `-p:VintageStory`, the environment variable `VINTAGE_STORY` works too.
2. `MyShaders.dll` lands in `myshaders/` next to `modinfo.json`. Set `"type": "code"` there.
3. Komet stays optional: `MyShadersSystem` calls `KometBridge` only when `IsModEnabled("komet")` is true. `Komet.Core.dll` is referenced with `Private="false"` and never shipped.
4. Use your mod's domain as the mod id in code, here `"myshaders"`. Only then does Komet hand the code's defines to the passes from `komet.json` too.
5. Komet forgets every registration when the world closes. `StartClientSide` registers them again each time you join a world.

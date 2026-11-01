# Your mod for Komet

In seven steps, this page takes you to a shader mod that works together with Komet. At the end your mod is in Komet's shader list, has sliders of its own and takes a path of its own when Komet runs. Without Komet it runs as before. You need no C#.

## Step 1: Make the template

The quickest start is Komet's ready-made template:

1. Press Modding › Templates › **Create mod template**. Komet creates the folder `ModConfig/komet/mod-template/mymod` and opens it.
2. Copy `mymod` into your Mods folder (`VintagestoryData/Mods`). The button **Mods folder** on the same page opens it.
3. Enter a world. If you are already in one, leave it and enter it again. Shaders › Active shaders now lists **My Komet Mod** under "Made for Komet", and Shaders has a page of its own for it with a switch and a slider. The picture turns warm.

If you already have a mod, you can also just look at the template and take its parts over into your mod.

## Step 2: What is inside

```text
mymod/
  README.md                          a short guide to this folder
  modinfo.json                       name, id and version, as in every mod
  assets/mymod/
    config/komet.json                the one file Komet reads
    config/komet/shaders/README.txt  Komet versions of your shader files go here
    shaders/warm.fsh                 the picture effect Komet draws
```

Everything for Komet is in `config/komet.json`: a group "Look", the switch `warm`, the slider `warmth` and a pass that draws `shaders/warm.fsh` over the finished picture while the switch is on. The file sits in the `config` folder, which the game loads anyway. Without Komet it does nothing, so your mod does not need Komet as a dependency.

## Step 3: Your own id

Replace `mymod` everywhere with your mod's id, for example `sunnyday`:

1. In `modinfo.json` at `modid`. Change `name` and `authors` there too.
2. In the folder name: `assets/mymod` becomes `assets/sunnyday`. The folder must be named exactly like `modid`, because Komet matches the `komet.json` to your mod by this name.
3. In the uniform `mymodWarmth`, in `komet.json` and in `warm.fsh`. A prefix of your own, such as your id, keeps your names apart from those of other mods.

Komet takes the name and author from your `modinfo.json`, unless `komet.json` gives its own (`name`, `author`).

## Step 4: Add sliders

Each entry under `settings` becomes a row on your page. `"type"` is `switch`, `slider`, `choice` (a list to pick from) or `group` (a heading). An entry hands its value to your shaders in one of two ways:

| Way | In the shader | When it takes effect |
|---|---|---|
| `"define": "SUNNY_RAYS"` | `#if SUNNY_RAYS` | after a short shader reload |
| `"uniform": "sunnyStrength"` | `uniform float sunnyStrength;` | at once |

```json
{
  "settings": [
    { "key": "rays", "label": "Sun rays", "type": "switch", "default": 1, "define": "SUNNY_RAYS" },
    { "key": "strength", "label": "Strength", "type": "slider", "min": 0, "max": 2, "step": 0.1,
      "default": 1, "uniform": "sunnyStrength" }
  ]
}
```

A define gets 0 or 1 from a switch, and from a choice the number of the option, counting from 0. It reaches the shaders that `programs` names; without `programs`, every shader and every pass of yours. You name a pass in `programs` as `pass:<id>`. A uniform reaches every shader and every pass that declares it. Texts such as `label` and `hint` may be lang keys, like `sunnyday:strength`.

Without Komet the define does not exist. So give it a default value in the shader, and your mod runs without Komet too:

```glsl
#ifndef SUNNY_RAYS
#define SUNNY_RAYS 1
#endif
```

## Step 5: Your own path

While Komet runs, your mod can leave the game's normal path and take a version you tuned for Komet. Pick what fits your mod:

**A small difference:** Komet sets the define `KOMET` in every shader and every pass, and also `KOMET_VULKAN` under its Vulkan renderer.

```glsl
#ifdef KOMET
    // the path tuned for Komet
#else
    // the game alone
#endif
```

**A whole file differs:** put the Komet version under `config/komet/` with the same path. This works for `shaders` and `shaderincludes`. With Komet it stands in for the normal file; without Komet the game never sees it. The normal file must still exist, or the log says the Komet version has nothing to stand in for.

| Normal file | Komet version |
|---|---|
| `assets/sunnyday/shaders/sky.fsh` | `assets/sunnyday/config/komet/shaders/sky.fsh` |
| `assets/game/shaders/final.fsh` | `assets/game/config/komet/shaders/final.fsh` |

**A picture effect:** an entry under `passes` draws a file of your own over the finished picture, like `warm.fsh`. This needs Komet and runs on OpenGL and Vulkan. What Komet puts in front of your pass file (`uv`, `color`, `scene`, `depth`, `resolution`, `time`) is on the page "Shader reference".

## Step 6: Try it

1. Change `komet.json` or a shader and save.
2. Press Shaders › Active shaders › **Reload packs**. You do not need to leave the world.
3. Under Shaders › Active shaders, your mod shows whether each shader compiles and, on Vulkan, whether Komet could translate it. A red exclamation mark means that something does not work, such as a shader. The detail says what it is. A pass that does not compile gets no mark: the detail shows it as "is off" with the reason. The log has the exact error.
4. Switch the renderer once (Komet › Overview › **Renderer**) and check that your mod looks the same on OpenGL and on Vulkan. What can differ on Vulkan is explained on the page "Shaders on Vulkan".

Errors are in `client-main.log`, in lines with `Komet shaders:` or `Shader compile error`. When a shader does not compile, Komet writes its full source once per session to `Logs/komet-shader-<file>.txt`; the line numbers in the error refer to that file. For a pass, they refer straight to your file.

## Step 7: Publish

Check briefly before you upload:

- `komet.json` is under `assets/<your-id>/config/` and is valid JSON. With the `"$schema"` line from the template, an editor such as VS Code shows mistakes at once.
- Every define has a default value in the shader for players without Komet.
- Your uniforms and defines carry your own prefix.
- If your mod draws with OpenGL only, `komet.json` says `"renderer": "opengl"` (the default is `"both"`). Komet then draws with OpenGL while your mod runs; the row **Renderer** is locked and names your mod.
- Your mod is tested: without Komet, and with Komet on both renderers.

## Common mistakes

| What happens | Why |
|---|---|
| Your mod is missing from "Made for Komet" | The file is not exactly at `assets/<id>/config/komet.json` |
| Your mod has no page of its own | `komet.json` is not valid JSON (the log names the line) or has neither `settings` nor `passes` |
| A slider changes nothing | The name is spelt differently in the shader, or for a define the shader is missing from `programs` |
| A pass never draws | `when` does not name the `key` of a switch, the switch is off, or the file in `fragment` is missing (the log says so) |
| Without Komet the shader does not compile | A define without a default value in the shader |
| A pass does not compile | It declares `uv`, `color`, `scene`, `depth`, `resolution` or `time` again |
| Your effect is missing on Vulkan | Your mod draws into OpenGL framebuffers of its own. Use a pass or set `"renderer": "opengl"` |

## Read on

- "Shader reference": every field of `komet.json`, passes and the code API `KometShaders`.
- "Shaders on Vulkan": the rules for shaders that run cleanly on Vulkan, and the Vulkan template.
- "API for mods": pages, features and styles of your own from C#.

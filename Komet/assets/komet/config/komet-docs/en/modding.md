# Getting started

Start here when you want to extend Komet or connect your mod to Komet. For most things you need no C#: a JSON file and your shader files are enough. Words like define, uniform or pass are explained on the page "Glossary" under Help.

## What do you want to do?

| You want to | Read | Code needed |
|---|---|---|
| Create an example and learn from it | "Templates" | No |
| Connect your shader mod to Komet | "Your mod for Komet" | No |
| Look up every field of `komet.json` | "Shader reference" | No |
| Write shaders that also run on Vulkan | "Shaders on Vulkan" | No |
| Change how this menu looks | "Menu styles" | No |
| Add pages, features or shaders from C# | "API for mods" | Yes, C# |
| Understand how Komet draws with Vulkan | "Vulkan explained" under Help | No |

## Ready-made templates

You create each template with one button under Modding › Templates. Until then your folders stay empty. If a name is taken, Komet adds a number, such as `example-2`.

- **Create example pack**: a small shader pack in `ModConfig/komet/shaders/example`, no mod of your own needed. It shows at once as its own page under Shaders.
- **Create example style**: a menu style with an amber accent colour in `ModConfig/komet/styles/example`. Komet picks it at once.
- **Create mod template**: a small shader mod made for Komet in `ModConfig/komet/mod-template/mymod`. Komet opens the folder, and you copy it into `VintagestoryData/Mods`. The page "Your mod for Komet" walks through it step by step.
- **Create Vulkan template**: a complete shader package for developers in `ModConfig/komet/mod-template/myshaders-dev`, with two passes, sliders, a define, texts in two languages and an optional C# part. The `README.md` in the folder explains each file, and the page "Shaders on Vulkan" explains the rules behind it.

The buttons **Templates folder** and **Mods folder** open the two folders you copy between.

## One file: komet.json

Your mod announces itself to Komet with a single file: `assets/<modid>/config/komet.json`. It holds your sliders, your picture effects (passes) and whether your mod runs on Vulkan. The game loads the `config` folder anyway but does nothing with the file. Without Komet it just sits there, and your mod does not need Komet as a dependency.

```flow
Your mod comes with a komet.json
Komet reads it when you enter a world
It is listed under "Made for Komet"
Players set your sliders
Komet hands the values to your shaders
```

"Made for Komet" is a group under Shaders › Active shaders. Your mod also gets a page of its own under Shaders, with its sliders. The page "Your mod for Komet" shows what the file looks like, and the page "Shader reference" explains every field.

## Where files live

- Your mod for Komet: `assets/<modid>/config/komet.json`
- Komet versions of your shaders: `assets/<modid>/config/komet/shaders/`
- Shader pack without a mod: `ModConfig/komet/shaders/<name>/komet-shader.json`
- Menu style without a mod: `ModConfig/komet/styles/<id>/style.json`
- Menu style in a mod: `assets/<modid>/config/komet-styles/<id>/style.json`
- Your own logo: `ModConfig/komet/logo.png`
- Templates: `ModConfig/komet/mod-template/`

`ModConfig` and `Mods` both sit in `VintagestoryData`. What is in `ModConfig` only counts on your computer. What is in a mod reaches every player who has the mod.

## With code

For everything a JSON file cannot do, Komet has a small public API in the namespace `Komet.Api`. Five classes are the way in, each with a job of its own:

| Class | What it does |
|---|---|
| `KometOptions` | Adds pages of your own to Komet's options menu |
| `KometFeatures` | Reads the state of Komet's features, holds one off or registers your own |
| `KometShaders` | Defines, uniforms, source edits and fullscreen passes for shaders |
| `KometStyles` | Registers a menu style from code |
| `KometDebug` | Writes timings of your own into the debug protocol |

The page "API for mods" shows each class with examples.

## Before you start

Two rules apply to all code that uses Komet. Both are on "API for mods", with examples:

- **Komet stays optional**: your mod still loads without Komet if only one class of its own names Komet's types and you call it only when `api.ModLoader.IsModEnabled("komet")` is true. The Vulkan template shows the whole pattern in its `code/` folder.
- **Everything ends with the world**: pages, features, holds, shader registrations, styles and event subscriptions end when the world closes. Register them on the main thread in `StartClientSide`; it runs each time you enter a world anyway.

## Other mods

Komet changes the game's code with Harmony patches, like many mods. To avoid clashes:

- **Komet stands down**: if another mod patches a method that a Komet feature relies on, Komet turns that feature off (state `StoodDown`). The game then does the work as usual. The log and the HUD tell you.
- **Fingerprints**: a feature that replaces game code checks a fingerprint of that code first. If a game update changed that code, the feature stays off (state `EngineChanged`) instead of breaking something.
- **Holding a feature off**: if your mod does not get along with one of Komet's features, ask Komet to keep it at the game's behaviour: without C# with `holds` in `komet.json` (see "Shader reference"), with C# through `KometFeatures.HoldOff`. The player's setting stays saved; the feature's row in the menu is locked and names your mod and the reason.
- **Your own features**: `KometFeatures.Register` registers features of your own. Komet shows their switch and guards them by the same rules: holding off, standing down and the fingerprint. How that works is on "API for mods".

Every debug protocol also puts a `harmony.txt` into its folder under `Logs/komet-debug/`: a list of which mod patches which method. It helps when two mods get in each other's way.

## Where to read more

- "Templates": every example to create, and the two folders.
- "Your mod for Komet": step by step from the template to publishing.
- "Shader reference": every field of `komet.json`, shader packs, passes and finding errors.
- "Shaders on Vulkan": how to write shaders that Komet translates cleanly for Vulkan.
- "Menu styles": every key of `style.json`, fonts, the logo and background shaders.
- "API for mods": each class from the table above, with code.
- "Vulkan explained" under Help: how Komet's Vulkan renderer draws.

# Komet for developers

Komet is a performance and rendering mod for Vintage Story. It draws the world with the game's OpenGL or with its
own Vulkan renderer, and it opens both to other mods.

- **Shader mods**: settings in Komet's options, defines and uniforms for the game's shaders, fullscreen passes of
  your own. One file, `komet.json`, binds a mod in; no code needed.
- **Vulkan without Vulkan code**: Komet ports every GLSL program to Vulkan by itself. Write GLSL 330 as usual.
- **Options, features and styles** from C#: your own pages in Komet's options window, features that Komet
  installs, holds and stands down, and styles for the options screen.

## Start here

| I want to | Read |
|---|---|
| make a shader mod that runs on OpenGL and Vulkan | [The Vulkan shader template](articles/vulkan-template.md) |
| know every field of `komet.json` | [komet.json reference](articles/komet-json.md) |
| understand when and where things draw | [Render passes and stages](articles/render-pipeline.md) |
| know what the Vulkan port accepts | [GLSL on Komet's Vulkan renderer](articles/vulkan-rules.md) |
| call Komet from C# | [Getting started](articles/getting-started.md), then the [API reference](api/index.md) |

## Download

[komet-vulkan-template.zip](downloads/komet-vulkan-template.zip): the shader template as a mod folder. In game,
Modding › Templates › *Shader mod project with optional C# integration* writes the same files.

## Shader Studio

Komet's Shader Studio: **Ctrl+F8** or `.komet dev` in game.
Edit copies of the game's shaders live, choose what the game runs (My version or Vanilla), force conditional effects
into the picture, preview in 3D with climate and season sliders, and run `.komet shadertest` for pictures of every
shader. The other tools sit in the dockable HUD window (**Ctrl+F7**).

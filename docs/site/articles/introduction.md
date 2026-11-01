# Introduction

Komet replaces parts of Vintage Story's client to make it faster, and adds a renderer of its own that draws the
world with Vulkan. Other mods meet Komet in three places:

1. **Shaders.** Komet loads the game's shader programs like the game does, adds its own edits, and lets mods add
   defines, uniforms, source edits and fullscreen passes. Under the Vulkan renderer the same programs are ported to
   SPIR-V and drawn in Vulkan's frame. A shader mod writes GLSL once.
2. **The options window.** Komet's options replace the game's graphics settings. Mods add pages, and shader mods
   get a page from their `komet.json` without code.
3. **Features.** Komet's speed-ups are features with a state. A mod can hold one at the game's own behaviour, and
   register features of its own that Komet installs, shows and stands down when another mod gets in the way.

## Two ways in

| | for | needs code |
|---|---|---|
| `assets/<modid>/config/komet.json` | shader mods, resource packs | no |
| `Komet.Api` from C# | code mods | yes, with Komet optional |

A mod can use both: `komet.json` for its settings and passes, code for what a file cannot say.

## Words used here

| Word | Meaning |
|---|---|
| program | one of the game's shader programs, named by its pass name: `chunkopaque`, `final`, `godrays` ... |
| pass | a fullscreen fragment shader of a mod's own, drawn over the finished picture |
| stage | one of the game's `EnumRenderStage` steps of a frame |
| renderer | who draws the world: the game's OpenGL or Komet's Vulkan renderer |
| port | Komet's translation of a GLSL program to Vulkan |

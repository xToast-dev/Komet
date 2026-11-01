# Render passes and stages

What the game draws in a frame, in which order, with which shader programs, and where Komet and mods come in.
Read from the game's code (1.22) and Komet's renderer.

## A frame, step by step

The game draws the world into the **primary framebuffer**, runs its post-processing there, composes the final
picture, then copies it to the window and draws the GUI on top. `EnumRenderStage` steps (in **bold**) are where
mods' `IRenderer`s run.

| # | Step | Framebuffer | Programs |
|---|---|---|---|
| 1 | **Before** | primary | `chunkliquiddepth`; early renderers. Komet's Vulkan frame starts here |
| 2 | **ShadowFar**, **ShadowFarDone** (shadow quality > 0) | far shadow map | `chunkshadowmap`, and entity shadows |
| 3 | **ShadowNear**, **ShadowNearDone** (shadow quality > 1) | near shadow map | the same |
| 4 | **Opaque** | primary | `chunkopaque`, `chunktopsoil`, and the renderers of entities, sky and the rest that draw opaque |
| 5 | **OIT** (order independent transparency) | transparent | `chunkliquid`, `chunktransparent`, and other transparent renderers |
| 6 | merge of the transparent pass | primary | `transparentcompose` |
| 7 | **AfterOIT** | primary | `chunkopaque` again for plants in water |
| 8 | post-processing | bloom, god ray, SSAO, luma buffers | `findbright` and `blur` (bloom), `godrays`, `ssao` and `bilateralblur`, `luma` and `blit` (FXAA) |
| 9 | **AfterPostProcessing** | primary | |
| 10 | final composition | primary | `final`: tone mapping, bloom, god rays, SSAO, vignettes |
| 11 | **AfterFinalComposition** | primary | **mod passes** (Komet, render order 0.99) |
| 12 | copy to the window | window | `blit` (Komet's sharp upscale lives here) |
| 13 | **AfterBlit** | window | |
| 14 | **Ortho** | window | `gui`: HUD, dialogs |
| 15 | **Done** | window | |

Shadow steps run only with shadows on; the transparent step only when something transparent is in view; bloom,
god rays, SSAO and FXAA only when the player turned them on.

## Where a mod hooks in

| Way | Where it runs |
|---|---|
| `IRenderer` at a stage | the stage of the table, in render order |
| `KometShaders.Define`, `Edit` | inside the game's programs, at their next load |
| `KometShaders.Uniform` | every program that declares it, at each `Use`, and every pass |
| a pass (`komet.json` or `AddPass`) | step 11, after the final picture, before the window copy and the GUI |
| a file under `config/komet/shaders/` | replaces the mod's own shader file while Komet runs |

## Program names

`KometShaders.Define(..., programs)`, `Edit(program, ...)` and `komet.json` `programs` name programs by their pass
name, the file name without extension in `assets/game/shaders/`.

| Group | Programs |
|---|---|
| terrain | `chunkopaque`, `chunktopsoil`, `chunkliquid`, `chunktransparent`, `chunkliquiddepth`, `chunkshadowmap` |
| sky | `sky`, `nightsky`, `celestialobject`, `aurora`, `cloudvolumetric`, `cloudmap` |
| entities | `entityanimated`, `shadowmapentityanimated`, `helditem`, `instanced` |
| particles | `particlescube`, `particlesquad`, `particlesquad2d` |
| post | `findbright`, `blur`, `godrays`, `ssao`, `bilateralblur`, `luma`, `blit`, `final`, `transparentcompose`, `colorgrade` |
| GUI | `gui` |

Shared code lives in `assets/game/shaderincludes/`: `fogandlight.fsh`, `fogandlight.vsh`, `shadowcoords.vsh`,
`vertexwarp.vsh`, `colormap.vsh`, `skycolor.fsh`, `underwatereffects.fsh` and more. An include is pasted into
every program that names it.

## What Komet changes in the game's shaders

Know these before you edit the same programs: Komet's edits come before a mod's, so match lines loosely.

| Program or include | Komet edit | Switch |
|---|---|---|
| `chunkopaque`, `chunktopsoil`, `chunktransparent` | vertex outputs recomputed per pixel (`kometWorldPos`, `kometColormap` ...) | LeanVaryings |
| `blit` | sharp upscale when the scene is smaller than the window | Render resolution |
| `godrays` | fewer texture steps | Lean god rays |
| `fogandlight.vsh` | point lights | Point lights |
| `shadowcoords.vsh`, `fogandlight.fsh` | a distant shadow map behind `KOMET_DISTANT` | Distant shadows |
| every program | `#define KOMET 1`, `#define KOMET_VULKAN 1` under Vulkan | always |

## Komet's Vulkan renderer

Under *Renderer: Vulkan* the game still issues its OpenGL calls in the same order. Komet records them and draws
the frame with Vulkan; OpenGL rests. Nothing is reordered: mods' `IRenderer`s and passes draw where they always do.

| Komet's pass | Stage | Programs |
|---|---|---|
| Liquid depth | Before | `chunkliquiddepth` |
| Shadows | ShadowFar, ShadowNear | `chunkshadowmap` |
| Opaque | Opaque | `chunkopaque`, `chunktopsoil` |
| Transparent | OIT | `chunkliquid`, `chunktransparent` |
| Water plants | AfterOIT | `chunkopaque` |
| Scene: sky, entities, particles, post, other | in engine order | everything else, mod programs included |

Terrain programs have their own fast path; every other program, a mod's included, is ported when it links and
drawn in the scene in call order. What Vulkan cannot take is left out of the picture and logged once; see
[GLSL on Komet's Vulkan renderer](vulkan-rules.md).

Coordinates are OpenGL's on both renderers: clip depth -1..1, window depth 0..1, rows from the bottom. Komet flips
the picture only when it presents it.

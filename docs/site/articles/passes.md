# Writing passes

A pass is a fullscreen fragment shader of a mod's own. Komet draws it after the game's final composition and
before the GUI, over the finished picture: tone mapped, with bloom, god rays and vignettes. Passes come from
`komet.json` (`passes`) or from code (<xref:Komet.Api.KometShaders.AddPass*>).

## The prelude

Your file is the body. Komet puts this in front of it:

```glsl
#version 330 core
#define KOMET 1
#define KOMET_VULKAN 1        // only while Komet's Vulkan renderer draws
#define ...                   // your mod's defines for this pass
in vec2 uv;                   // 0..1, (0, 0) bottom left
out vec4 color;               // replaces the pixel
uniform sampler2D scene;      // the picture so far, a copy: read any pixel
uniform sampler2D depth;      // the scene's depth, 0..1, 1 = sky
uniform vec2 resolution;      // in pixels
uniform float time;           // seconds, for animation
#line 1
```

`#line 1` keeps the line numbers in compile errors your file's.

## Inputs in detail

| Input | Details |
|---|---|
| `scene` | A 16-bit float copy of the primary framebuffer, made before each pass, so a pass reads what the passes before it drew. Linear filtering, clamped at the edges |
| `depth` | The primary framebuffer's depth texture, raw window depth (not linear). 1.0 where nothing was drawn |
| `uv` | Covers the whole picture; `uv * resolution` is the pixel |
| `time` | Seconds since the passes were installed; it keeps running across frames, not across worlds |
| your uniforms | Declared by you. Set by name before each draw: the pass's own (`komet.json` `uniform`, <xref:Komet.Api.KometPass.Uniforms>) and every <xref:Komet.Api.KometShaders.Uniform*> of any mod |

Uniforms Komet sets are `float`, `vec2`, `vec3` or `vec4`, by how many numbers the value has.

## Draw state

Blending, depth test and scissor are off; depth writes are off. One triangle covers the viewport, the size of the
primary framebuffer. Komet saves the GL state before its passes and restores it after, so the engine finds what
it left.

## Order

All passes of a stage draw in `order`, lower first. Passes of different mods interleave by that number. Use
steps of 10 to leave room.

## Failures

A pass that does not compile or link is left out until the next reload, with one warning in `client-main.log`:

```
Komet shaders: pass shaderpack:myshaders:grade is left out, it did not compile (line numbers are the mod's): ...
```

The mod's row in *Active shaders* shows a warning sign and the reason. An `Enabled` callback or uniform callback
that throws turns the pass off the same way.

Under Vulkan, Komet compiles the pass with OpenGL first and ports the linked program. A port failure appears as
`Komet: Vulkan cannot draw program N (no Vulkan port: ...)`; see [GLSL on Komet's Vulkan renderer](vulkan-rules.md).

## Limits

| | |
|---|---|
| passes, all mods | 32 |
| source of one pass | 64 KiB |
| stages | `Final` only (<xref:Komet.Api.KometPassStage>) |

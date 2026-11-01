# GLSL on Komet's Vulkan renderer

Komet ports the game's GLSL programs to Vulkan by itself: the game's, a mod's edited ones, a mod's raw OpenGL
programs and passes alike. This page says what the port does, what it accepts, and what to do when a program is
missing from the picture.

## What the port does

1. **Reads the source back** from the linked OpenGL program, with every edit, define and include in it.
2. **Preprocesses** it with shaderc (macros expanded, line numbers kept).
3. **Rewrites** it for Vulkan:
   - `#version` keeps its number (at least 140; `core` from 150 on, `compatibility` dropped) and gains the
     extensions for explicit bindings. 420 and above stay as they are.
   - Loose uniforms move into one uniform block per stage. Uniforms that change on nearly every draw go into
     push constants (at most 256 bytes).
   - Samplers get bindings in declaration order.
   - Varyings get locations in order; fragment inputs are matched by name.
   - `gl_VertexID` and `gl_InstanceID` become `gl_VertexIndex` and `gl_InstanceIndex`.
4. **Compiles** it to SPIR-V for Vulkan 1.3 and caches the result on disk.

It does not touch coordinates. Komet's Vulkan pipelines keep OpenGL's conventions: clip depth -1..1, window depth
0..1, rows counted from the bottom, `gl_FragCoord` as in OpenGL. `discard` and `gl_FragDepth` work.

## Write it like this

- GLSL 330 core: `in`/`out`, `texture()`, `layout(location = n) out` for several outputs. Not `varying`,
  `attribute`, `gl_FragColor` or `texture2D`: the Vulkan compiler rejects compatibility GLSL.
- Vertex inputs need a location: `layout(location = n) in`, or the game's `glBindAttribLocation`.
- Array sizes: integer literals, or `+` and `*` of them, after the preprocessor.
- Varyings: scalars, vectors and `mat2` to `mat4`. No structs, no interface blocks.
- Uniform defaults (`uniform float x = 1.0;`) only from literal numbers, `true`, `false` or constructors of them.
- Samplers: `sampler2D`, `sampler2DShadow`, `sampler2DArray`, `samplerCube`. A sampler used in both stages must
  have the same type in both.
- Uniform blocks and storage buffers with a binding of 0 to 15.

## Limits

| What | Limit |
|---|---|
| sampler slots per program | 32 (no sampler arrays) |
| varying locations per stage | 64 |
| vertex inputs | 16 |
| colour outputs | 8 |
| push constants (hot uniforms) | 256 bytes |
| programs drawn in the scene | 256 |
| stages | vertex and fragment only: no geometry or tessellation shaders, no compute |

## Uniform values under Vulkan

Komet sees uniform writes through its OpenGL tap. These reach Vulkan:

- `glUniform1f` to `4f`, `1i` to `4i`, their `fv`/`iv` arrays, `glUniformMatrix4fv` without transpose.
- Array uniforms written element by element for the first 64 elements, or whole from element 0.

These do not, and keep the value read when Komet first saw the program:

- `glUniform*ui` (unsigned), `glUniformMatrix2fv`, `glUniformMatrix3fv`, a transposed `mat4`, `glProgramUniform*`.
- Uniform structs.

The game's own uniform helpers (`ShaderProgramBase.Uniform`, `UniformMatrix`) use the calls that work.

## Draws Vulkan refuses

Logged once as `Komet: Vulkan cannot draw <program> (...)`, and missing from the picture:

- Textures the engine never bound to a sampler the program reads.
- Lines with adjacency, patches, and other primitive modes beyond triangle fans.
- Blits within one image, format pairs Vulkan cannot convert, RGB to RGBA.
- Integer clears, compute dispatches, multi-draw and indirect draws outside terrain.
- Drawing into framebuffers whose textures belong to no framebuffer Komet knows (a mod's own render targets made
  in unusual ways). Such a mod should declare `"renderer": "opengl"`.

## Error messages of the port

A program that does not port is logged as `Komet: Vulkan cannot draw <name> (no Vulkan port: <error>)` in
`client-main.log`, with the reason first as `Komet: Vulkan port of <name>: <error>`. A line
`Komet: Vulkan port of <name>: N ms by the waiting draw` only reports the time a port took; a mod's pass shows as
`program <number>`.

| Error | Fix |
|---|---|
| `a stage Vulkan draws no port of` | remove the geometry or tessellation stage |
| `no port for the interface block` | replace the `in`/`out` block with single varyings |
| `the attribute X has no location` | add `layout(location = n)` |
| `no port for the type of X` | no struct varyings |
| `the array size ... is no constant` | a plain number |
| `no port for the default of X` | set the value from code instead of an initializer |
| `more samplers than the port has bindings for` | at most 32 sampler slots |
| `X differs between the stages` | the same sampler type in both stages |
| `more interface locations than a stage has` | fewer varyings |
| `the binding of X is out of range` | block binding 0 to 15 |
| `the hot uniforms take N bytes` | fewer uniforms that change per draw |
| a shaderc message | GLSL the Vulkan compiler rejects; often compatibility syntax |

When a program fails to compile at all (on OpenGL too), Komet writes its full source to
`Logs/komet-shader-<file>.txt`, so the line in the error can be found.

## Testing

1. Switch *Komet › Overview › Renderer* to Vulkan and back. Komet reloads the shaders on each switch, with
   `KOMET_VULKAN` set or not.
2. Your mod's row in *Shaders › Active shaders* shows a red Vulkan badge when every one of its programs
   ported, an orange one when some did not; the detail lists them.
3. On Windows, Komet fetches the shader compiler at first use; until it is there OpenGL draws.

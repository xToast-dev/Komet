# KapiShaders · KapiPass

## Your effect or your own renderer

`komet.Shaders.TryAddPasses` compiles your passes at once and adds them together, or none of them. When it refuses,
start your own renderer instead: exactly one of the two draws.

```csharp
var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", source) { Enabled = () => config.Grade });
if (!fx.Active) StartOwn(); // fx.Error and the log say why
```

[Optional Komet integration](docs-integration)

## Members and usage

`komet.Shaders` changes the game's shaders and draws fullscreen effects of your own, on OpenGL and on Komet's Vulkan
renderer alike.

| Member                                  | What it does                                                                                           |
|-----------------------------------------|--------------------------------------------------------------------------------------------------------|
| `Define(name, value, programs)`         | Puts `#define name value` into programs; `value` is read as they load, so call `Reload()` after a change |
| `Uniform(name, value)`                  | A uniform Komet sets every frame: `Func<float>`, or `Func<float[]>` with 1 to 4 numbers                |
| `Edit(program, vertex, fragment)`       | Rewrites a program's source as it loads; `*` edits every program                                       |
| `AddPass(pass)`                         | A fullscreen pass of your own, built when the renderer is ready                                        |
| `TryAddPasses(passes)`                  | Compiles the passes now and adds all or none; for a fallback to your own renderer                      |
| `EditPass(modId, passId, fragment)`     | Rewrites another mod's pass (or one of yours) before it compiles                                       |
| `RendererChanged`                       | The renderer switched; Komet reloads and rebuilds the passes itself                                    |
| `KapiShaders.Reload()`                  | Rebuilds the game's shaders with every define and edit (static)                                        |
| `KapiShaders.Renderer`                  | `KapiRenderer.OpenGL` or `KapiRenderer.Vulkan` (static)                                                |
| `KapiShaders.Available`                 | Komet's shader module runs and takes registrations (static)                                            |

```csharp
KapiRegistration hour = komet.Shaders.Uniform("mymodHour", () => api.World.Calendar.HourOfDay);
hour.Dispose(); // takes it back before the world closes
```

- Every registration returns a `KapiRegistration`: `Active` tells whether Komet took it, `Error` says why not,
  `Dispose()` takes it back early. `komet.Dispose()` takes back all of them.
- `programs` names the game's programs (`chunkopaque`, `final`, ...) or `pass:id` for one of your passes; none gives the
  define to every program and every pass of your mod.
- Every program has `#define KOMET 1`, and `#define KOMET_VULKAN 1` as well under Vulkan.
- For all mods together, Komet takes up to 128 defines, 256 uniforms, 64 edits and 32 passes
  (`KapiShaders.MaxDefines`, `MaxUniforms`, `MaxEdits`, `MaxPasses`).

[Shaders and passes](docs-shaders)

[Renderer integration for mods](docs-vulkan-shaders)

[API index](docs-api)

## KapiPass

`new KapiPass(id, fragment)`: `id` is unique within your mod (the same id replaces your pass added before), `fragment`
is GLSL 330 that defines `main()` and writes `color`. Komet puts a prelude before it with `uv`, `scene`, `depth`,
`resolution` and `time`. `Stage` is `KapiStage.Final` (after the game's final composition, before the GUI), `Order`
sorts passes of one stage (lower first), `Enabled` is asked each frame (null: always), `Uniforms` holds uniforms of this
pass by name. Line numbers in compile errors are yours. A failed reload keeps the programs that ran before.

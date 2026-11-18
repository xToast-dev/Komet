# API reference

## Namespace Komet.Api

KAPI is Komet's API for other mods. Choose a part. The search field on the left finds API names and topics.

[KapiOptions · KapiPage](docs-api-options)

[KapiFeatures · KapiFeature](docs-api-features)

[KapiShaders · KapiPass](docs-api-shaders)

[KapiRendering · KapiRenderStep](docs-api-rendering)

[KapiStyles · KapiStyle](docs-api-styles)

[KapiDebug · KapiTiming](docs-api-debug)

## Connect

`Kapi.Connect(this, "1.0")` in your mod system's `StartClientSide` returns a `KapiMod`: your connection. Everything
else hangs off it: `Shaders`, `Rendering`, `Options`, `Features`, `Debug` and `Styles`. Your mod id comes from your
`modinfo.json`; every page, pass, render step, feature, timing and style you register carries it, so no call asks for
it. Every connected mod is listed on Komet's Connected mods page.

```csharp
var komet = Kapi.Connect(this, "1.0");
if (komet is null) { StartOwn(); return; } // a Komet without KAPI 1.0: the log says why
komet.Shaders.Uniform("mymodWarmth", () => config.Warmth);
komet.Options.Page("My mod").Switch("Warm picture", () => config.Warm, v => config.Warm = v);
```

| Member                    | What it does                                                                     |
|---------------------------|----------------------------------------------------------------------------------|
| `Kapi.Connect`        | Connects your mod; null when the version is not supported or the mod has no id   |
| `Kapi.Version`        | KAPI's version as `major.minor`, here `1.0` (`Major`, `Minor` as numbers)        |
| `Kapi.Supports(v)`    | Whether a mod written against version `v` can connect                            |
| `komet.ModId`, `Version`  | Your mod id, and the version you asked for                                       |
| `komet.IsConnected`       | False once disposed or once the world closed                                     |
| `komet.Dispose()`         | Takes back everything the connection registered                                  |

## Versioning

KAPI is versioned `major.minor`; this Komet offers KAPI 1.0. Your mod names the version it was written against in
`Connect`, and nowhere else: not in `modinfo.json`, not in an attribute.

- Komet connects your mod when the major versions are equal and its own minor version is the same or newer. A mod
  written for 1.0 connects to 1.0, 1.1, 1.2 and so on; a mod written for 1.2 does not connect to 1.0 or 1.1, and none
  connects to 2.0.
- On a mismatch `Connect` returns null and logs `Komet: KAPI did not connect <modid>: ...` with both versions. Start your
  own path then, as with Komet missing. `Kapi.Supports("1.2")` asks first without logging.
- The promise: a new minor version only adds (types, members, overloads), so code written for 1.0 keeps compiling and
  running. A new major version may change or remove things; it comes with a migration table in the changelog.
- Ask for the lowest version that has everything you use. Ask for a newer minor only when you call something it added.

## Integration and lifetime

Keep Komet optional: reference `Komet.Core.dll` with `Private="false"`, name KAPI's types only in a bridge class, and
call it only when `api.ModLoader.IsModEnabled("komet")` is true. The full example is on "Optional Komet integration".

- Call KAPI from the main thread, in `StartClientSide`. Only `komet.Debug.Measure` may be called from any thread.
- A connection lasts one world. When the world closes, Komet takes back pages, passes, render steps, defines,
  uniforms, edits, holds, styles and event handlers; connect again in the next `StartClientSide`.
- `komet.Dispose()` takes them back earlier, all at once. Your features stay until the world closes, because Harmony
  patches cannot all be taken back at once.
- If an event handler throws, Komet logs it and unsubscribes the handler.
- Things that need no connection are static: `KapiShaders.Available`, `KapiShaders.Renderer`, `KapiShaders.Reload()`,
  `KapiFeatures.StateOf`, `KapiFeatures.Snapshot`, `KapiFeatures.FingerprintOf` and `KapiDebug.Capturing`.

[Optional Komet integration](docs-integration)

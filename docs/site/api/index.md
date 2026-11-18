# API reference

Everything another mod may call is **KAPI 1.0**, the namespace <xref:Komet.Api>. Reference `Komet.Core.dll` from the
player's Komet with `Private="false"`, call it only when Komet runs, and connect with
`Kapi.Connect(this, "1.0")`; [Getting started](../articles/getting-started.md) shows how and explains the
versioning.

| Class | For |
|---|---|
| <xref:Komet.Api.Kapi> | the version, and `Connect` |
| <xref:Komet.Api.KapiMod> | one mod's connection; `Dispose()` takes back everything |
| <xref:Komet.Api.KapiShaders> | defines, uniforms and edits for the game's shaders; passes of your own |
| <xref:Komet.Api.KapiPass> | one fullscreen pass |
| <xref:Komet.Api.KapiOptions> | pages in Komet's options window (<xref:Komet.Api.KapiPage>) |
| <xref:Komet.Api.KapiFeatures> | features: state, holds, registering your own |
| <xref:Komet.Api.KapiFeature> | a feature of your mod |
| <xref:Komet.Api.KapiStyles> | styles of the options screen (<xref:Komet.Api.KapiStyle>) |
| <xref:Komet.Api.KapiDebug> | your timings in `.komet debug` |

All calls are main thread only, unless a member says otherwise. A connection lasts one world: Komet takes back every
registration, hold and subscription when the world closes.

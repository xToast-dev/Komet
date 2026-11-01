# API reference

Everything another mod may call lives in the namespace <xref:Komet.Api>. Reference `Komet.dll` from the player's
Komet with `Private="false"` and call it only when Komet runs; [Getting started](../articles/getting-started.md)
shows how.

| Class | For |
|---|---|
| <xref:Komet.Api.KometShaders> | defines, uniforms and edits for the game's shaders; passes of your own |
| <xref:Komet.Api.KometPass> | one fullscreen pass |
| <xref:Komet.Api.KometOptions> | pages in Komet's options window |
| <xref:Komet.Api.KometFeatures> | Komet's features: state, holds, registering your own |
| <xref:Komet.Api.FeatureDefinition> | a feature of your mod |
| <xref:Komet.Api.KometStyles> | styles of the options screen |
| <xref:Komet.Api.KometDebug> | your timings in `/komet debug` |

All calls are main thread only, unless a member says otherwise. Komet drops every registration, hold and
subscription when the world closes.

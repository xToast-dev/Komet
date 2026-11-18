# Render steps and the Connected mods page

A render step is code of your own at a named place of Komet's frame. <xref:Komet.Api.KapiRendering> (`komet.Rendering`)
adds it; Komet runs it as a game renderer of that stage, on OpenGL and under Vulkan alike.

```csharp
var outline = komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, DrawOutline)
{
    Order = 0.6,                      // within the stage, lower runs first (0.5 by default)
    Enabled = () => config.Outline    // asked each frame; null runs always
});
if (!outline.Active) api.Logger.Warning("mymod: {0}", outline.Error);
```

`DrawOutline` gets the frame's time in seconds and draws with the game's render API, as an `IRenderer` of the same stage
would. <xref:Komet.Api.KapiRenderStage> names the places in frame order: `Before`, `Opaque`, `Transparent`,
`AfterTransparent`, `ShadowFar`, `ShadowNear`, `AfterPostProcessing`, `AfterBlit`, `Gui`, `AfterFinal`, `Done`.

## What Komet does with a step

- **Replaces**: the same id replaces your step added before.
- **Refuses**: an empty id, or more than <xref:Komet.Api.KapiRendering.MaxSteps> (64) steps of all mods together.
  The <xref:Komet.Api.KapiRegistration> then has `Active` false and `Error` says why.
- **Times**: each call is counted and timed; during a `.komet debug` capture it is booked as `modid:id`, as with
  [debug timings](debug.md).
- **Guards**: a step that throws is switched off for the world, logged as
  `Komet: render step modid:id threw and is off for this world`, and shown with its error.
- **Ends**: when the registration is disposed, `komet.Dispose()` runs or the world closes.

## The Connected mods page

*Overview › Connected mods* in Komet's options lists every mod connected through KAPI:

- a switch per mod: off stops the mod's render steps for the session, its other registrations stay;
- a card with the KAPI version the mod asked for, the counts of what it registered (render steps, passes, shader
  registrations, pages, features, holds, styles) and each render step with its stage and milliseconds per frame, or
  the error that switched it off;
- below, the mods Komet refused this world (for example written for a newer KAPI), with the reason.

Players see your mod there as soon as it connects; nothing has to be declared for it.

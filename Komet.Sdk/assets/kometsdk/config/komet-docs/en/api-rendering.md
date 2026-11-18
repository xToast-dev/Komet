# KapiRendering · KapiRenderStep

## Render steps

A render step is code of your own at a named place in Komet's frame. Komet runs it as a game renderer of that stage,
on OpenGL and under Vulkan alike, times it, books it in the debug protocol and shows it on the Connected mods page.

```csharp
var outline = komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, DrawOutline)
{
    Order = 0.6,
    Enabled = () => config.Outline
});
if (!outline.Active) api.Logger.Warning("mymod: {0}", outline.Error);
```

| Member                         | What it does                                                                  |
|--------------------------------|-------------------------------------------------------------------------------|
| `komet.Rendering.AddStep(step)`| Adds the step; returns a `KapiRegistration` (`Active`, `Error`, `Dispose()`)  |
| `KapiRenderStep(id, stage, render)` | The step: its id within your mod, its stage, and what it draws (gets the frame's seconds) |
| `Order`                        | Within the stage, lower runs first; 0.5 by default                            |
| `Range`                        | The game's render range; 0 by default                                         |
| `Enabled`                      | Asked each frame before the step runs; null runs always                       |
| `KapiRendering.MaxSteps`       | How many steps all mods together may add: 64                                  |
| `KapiRendering.Renderer`       | The renderer drawing the world now                                            |

## Stages

`KapiRenderStage` names the places, in frame order: `Before`, `Opaque`, `Transparent`, `AfterTransparent`,
`ShadowFar`, `ShadowNear`, `AfterPostProcessing`, `AfterBlit`, `Gui`, `AfterFinal`, `Done`. Draw with the game's
render API, as an `IRenderer` of the same stage would.

## Rules

- The same id replaces your step added before. A step runs from the next frame until its registration is disposed,
  `komet.Dispose()` runs or the world closes.
- An empty id or a full table is refused: `Active` is false and `Error` says why.
- A step that throws is switched off for the world. The log gets `Komet: render step modid:id threw` with the
  exception, and the Connected mods page shows the error.
- The player can switch all steps of your mod off on the Connected mods page, for the session. Your other
  registrations stay.
- During a `.komet debug` capture each step is booked as `modid:id`, as with `komet.Debug.Measure`.

## Connected mods page

*Developer › Connected mods* (with the game's developer mode on) lists every mod connected through KAPI: a switch for its render steps, and a card
with the KAPI version it asked for, what it registered (steps, passes, shader registrations, pages, features, holds,
styles) and each render step with its stage and milliseconds per frame, or its error. Mods Komet refused, for example
for a newer KAPI version, are listed below with the reason.

[API index](docs-api)

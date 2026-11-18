# HUD and measuring

## Keys

| Key | What happens |
|---|---|
| {key:komet-hud-toggle} | Show or hide the [[HUD]] |
| {key:komet-hud-details} | The details card on the right: frames, rendering, CPU, memory, GC, world |
| {key:komet-hud-window} | The HUD window with every measuring tool |
| {key:komet-hud-debug} | The [[Shader Studio]] |

## Measuring a stutter

1. Go to the place where it stutters.
2. Open the HUD window with {key:komet-hud-window}.
3. Start a [[spike capture]] and wait for the next hitch.
4. Copy the report and attach it to your report.

Watch the [[frame time]], not the FPS. A high FPS can hide single slow frames.

[Narrowing down problems](docs-problems)

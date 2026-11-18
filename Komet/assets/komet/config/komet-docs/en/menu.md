# Menu and settings

## Controls

`.komet` opens Overview. Pages are on the left, settings or article text in the center, and details or the clickable
article outline on the right.

- Search settings, help topics and API names.
- Enter applies staged values. R resets page defaults. Esc closes the menu.
- Buttons run their labeled action immediately.

## Render diagnostics

Enable developer mode and open **Render · Live (Dev)** in Komet's settings (`.komet`). Toggle terrain passes and registered Komet passes
individually. Wireframe, points, backfaces and depth testing apply to terrain. Search or filter by mod, stage and
errors. Copy exports the full render report.

Save a state as A and toggle against B to compare. Overrides last for the session; terrain overrides suspend GPU
occlusion culling.

## Connected mods

**Overview › Connected mods** lists the mods that talk to Komet through KAPI, its API for other mods. Each has a
switch: off stops that mod's render steps in Komet's frame until the game restarts; its other settings stay. Its card
shows what the mod registered and what each render step costs per frame, or the error that switched it off. Mods Komet
could not connect, for example because they need a newer Komet, are listed below with the reason; their button copies
it.

## Files

`ModConfig/komet.json` stores settings. Local styles, shaders and templates live under `ModConfig/komet/`.

[HUD and measurements](docs-hud)

# Shader Studio (workbench): maintainer notes

The Shader Studio lives in `Komet.Sdk`, Komet's top module (in Komet's zip). It joins Komet through
`Komet.Rendering/Shaders/StudioHooks.cs`; with the studio closed it does no work.

## Look

- No palette of its own: every colour comes from `HudUi` (set by `HudUi.Restyle` from the menu style the player chose).
  Surfaces `PanelBack`/`PopupBack`, selection `SelectedBack`/`SelectionBack`, brackets `BracketBack`, text and lines
  `Divider`, `Soft`, `Faint`, `Link`, `Accent`, `Ok`, `Warn`, `Err`. A new colour is a new style token, never an RGB value.
- Panels are drawn with `HudUi.Panel`; sizes go through `ui.Px(...)`, never fixed screen pixels.
- UI text uses the style's HUD fonts; source text is monospace and scales with the game UI.

## Manual checks in game

The tests cover the pure parts (include expansion, error mapping, search, editor helpers). Only the running game
shows whether a shader really changed the picture; a status line saying "compiled" is not proof.

1. **Edit a vanilla stage**: open a `game` shader's fragment stage, edit a working copy (the vanilla source stays
   read-only), test in world, check the change in a scene that uses the program, restore vanilla.
2. **Reject broken GLSL**: add a syntax error and test; the Problems panel names the file and source line from the
   driver log, and the last working program keeps rendering.
3. **Toggle a pass**: switch off a terrain or Komet pass in the Pipeline tab; the scene changes and dependent passes
   still get valid inputs; switch it back on.

Record per check: date, game version, Komet build, shader/pass, result (`passed`, `failed`, `not run`).

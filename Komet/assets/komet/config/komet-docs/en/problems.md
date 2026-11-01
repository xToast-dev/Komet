# Troubleshooting

This page helps when something is wrong. First come common problems and what helps. Then it explains what it means when Komet steps aside, and last how to report a problem. The page "Glossary" explains words such as spike or GC.

## Quick help

### Picture and world

| What you see | What helps |
|---|---|
| Terrain is missing or has holes | Click Komet › Tools › **Rebuild the terrain**. You stay in the world. After a few seconds the terrain is back. |
| Something looks wrong or is missing under Vulkan only | Set Komet › Overview › **Renderer** to **OpenGL** and compare. More on the page "Vulkan explained". |
| Komet shows the renderer as "OpenGL (Vulkan failed, see the log)" | Vulkan had an error, and OpenGL keeps drawing. Report it with a debug protocol and `client-main.log`. |
| A shader mod has no effect | Komet holds back mods that replace the game's shaders and are not made for Komet. Switch them on under Shaders › Active shaders. The chat names them when you join the world. |

### Stutter and speed

| What you see | What helps |
|---|---|
| Short stutters | Press F7. For stutters from the value of Komet › HUD › **Stutter hint from** on (50 ms by default), a card names the cause. More on the page "HUD and measuring". |
| Turning feels uneven despite a high FPS | Check that **Even FPS cap** and **Even frame times without a cap** under Komet › Rendering are on (they are by default). The first works with an FPS cap, the second without one. Both work only with VSync off. |
| Stops of several seconds, mostly at a large view distance | Check that Komet › Other › **Clean up in the background** is on. |
| The frame rate drops while chunks load | Lower Komet › Chunks › **Priority: loading chunks**. At 0 % the game runs most smoothly, but chunks load more slowly. |
| The HUD reports full video memory | Lower Vintage Story › General › **View Distance**. |
| A mod takes a lot of time | Open the HUD window with Ctrl+F7. The overview names the mod with its time; **Record** starts a recording of 10 s. The protocol's "Render and event time per mod" section shows which of the mod's calls take the time. |

### Menu and settings

| What you see | What helps |
|---|---|
| A switch is locked | Another mod needs the game's own behaviour there. See "Komet steps aside" further down. |
| You suspect a Komet switch | Test it as in "Testing a switch" further down. |
| Komet's settings are suddenly back to default | `ModConfig/komet.json` could not be read, often after an edit by hand. Komet then uses the defaults and keeps the old file as `komet.json.broken`. |
| You changed too much | Click Komet › Overview › **Use this computer's values**, or reset Komet › Tools › **Display, panels and features** (click twice). |
| The HUD reports "The installed file differs from the published checksum" | Your Komet file is not the published one. Download Komet again. Komet › Tools › **Checksum** shows both checksums. |

## Komet steps aside

Komet is made of many features (see "Glossary"). Each one checks whether it can work safely. If not, it steps aside: the game does that work itself again, just as without Komet. Nothing breaks, only this one gain is missing.

### Where you see it

- **In the HUD**: the overview shows "X Komet features stood down or failed" and names the first feature. The button **look** makes a debug protocol.
- **In the menu**: when a feature has stood down or is held off, its switch is locked. Below it reads: "Another mod needs the game's own behaviour here. Once it lets go, your choice applies again." When a mod holds the feature, its mod id and its reason are shown too.
- **In the debug protocol**: the section "Komet settings and features" names every feature that is not active, with its state. The findings name the features that stood down, whose game code changed or that failed.
- **In the log**: `client-main.log` usually has a line with `Komet` and the feature's name.

### The states

The protocol names the states in English.

| State | What happens | What you do |
|---|---|---|
| stood down (`StoodDown`) | Another mod patches the same code. Komet lets it go first. | Nothing. The debug protocol lists which mods patch the same methods. |
| held off (`HeldOff`) | Another mod asked Komet to keep the game's own behaviour here. | Nothing. Once the mod lets go of the feature, your choice applies again. |
| game code changed (`EngineChanged`) | A game update changed the code the feature replaces. It stays off. | Wait for a Komet update. With Komet › Tools › **Update notices** on, Komet tells you when there is one. |
| failed (`Failed`) | The feature hit an error. The game's own code runs now. | Report it with a debug protocol. |
| not applicable (`NotApplicable`) | It belongs to the server, and you play on a server, not in singleplayer. Or your graphics card or driver lacks something. | Nothing, this is normal. |
| off (`Off`) | All its switches are at the game's value. | Nothing, or switch it back on. |

## Testing a switch

If you suspect a Komet switch, test it like this:

1. Switch on Komet › Tools › **Show advanced options**.
2. On Komet's pages such as Rendering, Chunks and Other, the group **Exact optimisations** appears. These switches give the same picture as the game, only faster.
3. Switch off **All exact optimisations** there and click **Apply**.
4. If the problem is gone, switch them back on and then off one by one until you have found the switch.
5. Name it in your report.

You test the other switches on these pages the same way. The debug protocol lists every setting that differs from the game anyway.

## Reporting a problem

The debug protocol is a plain-text report of everything Komet measures. It helps a lot with bug reports. What else the debug window can do is on "HUD and measuring".

1. Press Ctrl+F8. The debug window opens.
2. Pick the kind: **Now** (3 s) for the state right now, **Recording** for a problem that keeps coming back, **On a spike** for rare stutters.
3. Leave the language on **English**.
4. Start the capture, and while it runs, do what causes the problem.
5. The protocol appears in the window and is in your clipboard.
6. Paste it into your report (Discord, GitHub or a chat). Say what you did and what went wrong.

### Chat commands

The same works from chat. Komet's commands start with a full stop.

| Command | What it does |
|---|---|
| `.komet` | opens Komet's options |
| `.komet debug` | records 10 s |
| `.komet debug 60` | records 60 s (1 to 600) |
| `.komet debug now` | records 3 s |
| `.komet debug spike` | waits for a frame over the limit of **Stutter hint from** (50 ms by default) |
| `.komet debug spike 40` | waits for a frame over 40 ms |
| `.komet debug stop` | ends a capture early |
| `.komet debug join` | from now on records the first minute of every world; a second time switches it off again |
| `.komet debug now de` | `de` or `en` picks the protocol's language, with any kind |

### What to attach

- The protocol itself. It is also saved as `komet-debug.txt` in `Logs/komet-debug/<time>/` in your VintagestoryData folder.
- From the same folder: `frames.csv` (every frame of the capture), `harmony.txt` (which mod patches which method) and `summary.txt` (the numbers for the next comparison).
- `client-main.log` from `VintagestoryData/Logs`, above all after an error or a crash.
- If the game froze: `komet-hang-<time>.txt` from the same `Logs` folder. Komet writes this file when no frame finishes for 10 seconds.
- If Vulkan was running: `komet-vulkan-watch.txt` from the same `Logs` folder.

Komet keeps the newest 30 protocol folders and deletes older ones. In the protocol, in `harmony.txt` and in the measurements, your home folder shows as `~` and your user name as `<user>`. So the paths in them do not give away your name.

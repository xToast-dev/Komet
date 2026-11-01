# Komet

Komet is a performance mod for Vintage Story and runs on your computer: a server does not need it. In singleplayer, Komet also helps the built-in server that runs your world in the background.

The goal is smooth play: fewer stutters and better 1 % lows, not just a higher average FPS. The 1 % lows show how smooth the slowest moments are (more on this under "Glossary"). To get there, Komet replaces costly parts of the game with code that gives the same picture, only faster or leaner.

## What Komet does

- Draws the world with less work for the CPU and the graphics card: faster view checks, less waiting on the graphics driver, leaner shaders.
- Builds and loads chunks faster, nearest first, on its own worker threads.
- Creates less memory garbage, so the game stops to clean up less often.
- Fixes some game bugs along the way: black chunks after light is recalculated (in singleplayer), light from a held torch that swims as you walk, and creatures and players that vanish behind a glass pane held in a hand.
- Adds extras: shadows up to your view distance, an experimental Vulkan renderer and a HUD that measures where the time goes.
- Replaces the game's settings menu with its own options menu, with search, presets and this help.

## First steps

1. Open the menu with Esc › **Settings** or with the chat command `.komet`.
2. Look at the preset on Komet › Overview. Out of the box it is set to **Automatic (recommended)**. That suits most computers.
3. If you like, pick the **Renderer** on the same page. **OpenGL** is the safe choice. What **Vulkan (experimental)** can do is on "Vulkan explained".
4. Apply your choice with **Enter**.
5. Press **F7** in game: the overlay shows FPS, 1 % lows and frame time. More on this under "HUD and measuring".

## When Komet steps aside

If another mod changes the same spot in the game's code as Komet, or a game update has changed that spot, Komet leaves that part out. The game works there as usual, only without Komet's speed-up. The HUD and the log tell you which part it is. More on this under "Troubleshooting".

## Where to find what

| Page | What you find there |
|---|---|
| "The options menu" | Sections, presets, keys, tools, where your settings live |
| "HUD and measuring" | Overlay, HUD window and debug window, what the numbers mean |
| "Vulkan explained" | What the Vulkan renderer is and when it pays off |
| "Troubleshooting" | What to do about stutters or visual glitches, how to report a bug |
| "Glossary" | Technical words such as chunk, shader or frame time, in plain words |
| Modding section | For mod authors and designers: templates, shaders, menu styles, API |

## Updates and checksum

The first time you join a world, Komet asks whether it may look for updates. If you say yes, Komet then asks GitHub (`api.github.com`) once each time you join a world whether there is a newer version. It also checks that your installed file is exactly the published one (sha256 checksum). If there is a newer version or your file differs, the HUD window (**Ctrl+F7**) shows a hint.

- Komet › Tools › **Update notices** turns this on or off at any time.
- Komet › Tools › **Checksum** shows your version, the newest one on GitHub and whether your file matches, in the HUD window. For this, Komet asks GitHub once, even when update notices are off.

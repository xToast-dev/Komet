# The options menu

Komet replaces the game's settings menu with its own. It holds the game's settings, Komet's options, the shaders and this help in one place, with search and presets.

## Opening the menu

- Esc › **Settings** opens Komet's menu instead of the game's. It starts on Vintage Story › General.
- The chat command `.komet` always opens it, straight on Komet › Overview.
- **Esc** closes it again. Anything you have not applied yet is dropped.

If you want the game's menu back, switch off Komet › Overview › **Replace the settings menu**. When you come through Esc › Settings, the original is also at the very bottom of Vintage Story › Interface: **Original graphics menu**.

## The five sections

The sections are the tabs at the top of the menu. The open section's pages are listed on the left.

| Section | Pages |
|---|---|
| Vintage Story | General, Quality, Performance, Mouse, Controls, Accessibility, Sound, Interface; Dev when developer mode is on |
| Komet | Overview, Rendering, Chunks, Other, Vulkan, HUD, Style, Tools |
| Shaders | Active shaders, plus one page per shader pack and per shader mod that Komet steers |
| Help | Komet, The options menu, HUD and measuring, Vulkan explained, Troubleshooting, Glossary |
| Modding | Getting started, Templates, Your mod for Komet, Shader reference, Shaders on Vulkan, Menu styles, API for mods |

Other mods can add pages or whole sections of their own. On the text pages under Help and Modding, the **Contents** column on the right jumps to a heading, and **Copy** on a code block copies it to the clipboard.

## Presets

On Komet › Overview you pick a preset in the **Settings** row. It sets the options on the Rendering, Chunks, Other and Vulkan pages at once.

- **Automatic (recommended)**: like **Balanced**, or like **Speed** on a weak computer. Weak here means: at most 4 CPU threads (the row **This computer** calls them cores), 8 GB of RAM or 4 GB of video memory.
- **Quality**, **Balanced**, **Speed**: how much of a nice picture you trade for more FPS.
- **Adjusted** appears as soon as you change one of the preset's options yourself. Your value stays. If you set the option back to its default or pick a preset again, it follows the preset and your computer again.

Komet also tunes some options to your computer, whatever the preset. The row **This computer** shows what Komet knows: cores, RAM, video memory and the refresh rate of your fastest monitor.

The button **Use this computer's values** in that row undoes all your own changes to Komet's options. This includes the **General** group on this page, the exact optimisations and the **Renderer** (it is back on **OpenGL** afterwards). After that, every option follows the preset and your computer again. HUD, style and tools stay as they are. The button acts at once.

## Changing and applying

The menu collects changes first and then lets them take effect together. That way, for example, the shaders reload only once.

```flow
Change a value
The row gets a mark
Enter or Apply
Everything takes effect at once
```

| Action | Key |
|---|---|
| Apply | Enter |
| Apply and close | Enter, then Esc |
| Undo everything | Del |
| Whole page to default | R |
| Close without applying | Esc |

- Changed rows are marked, with a small dot after the name.
- The keys are in the footer at the bottom, and you can click them too. Next to them it says how many changes are waiting. A click on the count shows the list **Your changes**, where you can change each row again. Another click or **Esc** takes you back.
- These act at once, without applying:
  - buttons and key bindings
  - the rows on Komet › Style
  - on Shaders › Active shaders, the switches under **Made for Komet** and **Other shader mods**
  - the pages that say "Komet takes over …"

## Search

- Press **Ctrl+F** or click the search field above the page list. Only then can you type. Before that, Q, E and R act as shortcuts.
- The search finds every option on every page whose name or description contains your search word. Section and page are shown above the hits. It does not search the help texts.
- **Enter** ends typing, **Esc** or **×** clears the search.

## Keys

| Key | Effect |
|---|---|
| Q / E | Previous / next section |
| Tab / Shift+Tab | Next / previous page |
| Up / Down arrow | Pick a row |
| Page Up / Page Down | Eight rows up / down |
| Left / Right arrow | Change the picked row's value |
| Shift+Left / Right arrow | Move a slider ten steps |
| Home / End | A clicked slider to its lowest / highest value |
| Enter | Press a button, bind a key, otherwise apply |
| R | Whole page to default |
| Del | Undo all changes |
| Ctrl+F | Search |
| Esc | One step back, otherwise close the menu |

The picked row follows the mouse too.

**Esc** first goes one step back: it lets go of a clicked slider, leaves the list of your changes or clears the search. Only when nothing is open any more does Esc close the menu.

## Using the rows

- **Switch**: a click turns it on or off.
- **Choice**: a click moves to the next value, a right click goes back. You can also click the arrows left and right of the value.
- **Slider**: drag it or click where you want it. A right click sets it to its default. When the value differs from it, a small mark shows where the default lies.
- **Button**: a click runs it at once.
- When you move the mouse over a row, the area on the right explains it.
- Greyed-out rows cannot do anything right now, because an option they need is off. Switch that option on first.
- If the explanation says another mod needs the game's own behaviour here, the row is locked. Once that mod lets go, your choice applies again.

## Advanced options

Normally Komet's pages only show options where you weigh one thing against another, such as picture against speed or RAM against CPU time. Komet › Tools › **Show advanced options** adds the group **Exact optimisations** to Rendering, Chunks, Other and Vulkan. It holds every other Komet switch, with the switch **All exact optimisations** at the top, which turns them all on or off at once.

Exact optimisations give the same result as the game (or fix a bug in it), only faster or leaner, and stay on. Only turn one off to narrow down a problem, and then name it in your bug report (see "Troubleshooting").

## Key bindings

Vintage Story › Controls shows all of the game's key bindings on one page. Changes here act at once, without applying.

- Click a row (or press **Enter**), then press the new key, key combination or mouse button.
- **Esc** keeps the old binding.
- **×** removes the binding. The row then shows **not bound**.
- A right click restores the game's default.
- Keys bound twice show in red, with the name of the other binding.
- **R** sets every binding on the page back to the game's default at once.
- At the very bottom, **Reset Controls** resets all bindings (click twice). When you come through Esc › Settings, **Open Macro Editor** is there too.

## Tools

Komet › Tools gathers switches and buttons for upkeep and troubleshooting.

| Row | What it does |
|---|---|
| **Show advanced options** | Shows the exact optimisations (see above) |
| **Update notices** | Looks for updates when you join a world (see "Komet") |
| **Checksum** | Shows your version and whether your file is the published one |
| **Rebuild the terrain** | Rebuilds all loaded chunks without leaving the world and frees their video memory |
| **Benchmark length**, **Benchmark** | Measures for 5 to 120 s and puts the averages on the clipboard and in the client log (see "HUD and measuring") |
| **Current values** | Records a debug protocol for 3 s and puts it on the clipboard |
| **Panel positions** | Puts the HUD window and the debug window back in their starting place |
| **Display, panels and features** | Resets the HUD display, the menu's look, all Komet options and the positions |

Both reset buttons ask first: the first click turns **Reset** into **Confirm**, and only the second one resets. Your answer about update notices is kept.

## Where your settings live

All paths are inside your VintagestoryData folder.

| Path | Content |
|---|---|
| `ModConfig/komet.json` | Preset, the Komet options you set yourself, HUD and menu |
| `ModConfig/komet/styles/` | Your own menu styles |
| `ModConfig/komet/logo.png` | Your logo in the header, when the style has none of its own |
| `ModConfig/komet/shaders/` | Shader packs to try out |
| `ModConfig/komet/shader-settings.json` | Which shaders are on or off, and the packs' values |
| `ModConfig/komet/mod-template/` | Mod templates from Modding › Templates |
| `Logs/komet-debug/` | Debug protocols |
| `Cache/komet-vulkan/` | Saved Vulkan shaders (with **Vulkan: save shaders**) |

## The menu's look

Komet › Style decides how this menu looks. Everything on this page acts at once.

- **Style**: Pragmatic (out of the box) with tabs at the top, page list, rows and details over a planet. Styles from other mods and your own come on top.
- **Panel opacity** and **Animations** (Off, Calm, Full) set the finer points.

How to build a style of your own is on "Menu styles" in the Modding section. Modding › Templates › **Create example style** writes a ready-made example.

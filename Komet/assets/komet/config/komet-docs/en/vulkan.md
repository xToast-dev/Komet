# Vulkan explained

Komet can draw the world with Vulkan instead of OpenGL. The first part of this page tells you what that gives you, how to try it and what you can set. The second part explains the technology behind it, for anyone who wants to know the details. Words like shader, pass or pool are explained on the page "Glossary".

## Why Vulkan

Vintage Story draws with OpenGL. OpenGL is old and proven, but the driver does a lot of work in the background, and the game often has to wait for it. With Vulkan, Komet decides more for itself: when to wait for the graphics card, what to keep in memory and which terrain the graphics card can leave out.

What this can mean for you:

- Less work for the CPU, because the graphics card itself picks the terrain it draws.
- Steadier frames with **Vulkan: even pacing** and **Bridge late frames**.
- Less video memory with **Save video memory**.

The other side, to be honest: the Vulkan renderer is **experimental**. What Vulkan cannot draw is missing from the picture. OpenGL stays the safe choice. If Vulkan brings you nothing or something looks wrong, switch back.

## Trying it

1. Open Komet › Overview.
2. Set **Renderer** to **Vulkan (experimental)** and apply the change (**Enter**). It takes effect at once, even in the middle of a world.
3. The terrain is rebuilt. After a few seconds it is back.
4. Above the row you can see which renderer is drawing now: **Renderer (active: Vulkan)**.

If it still says OpenGL there, Vulkan is waiting for something or could not start. A line `Komet: Vulkan …` in `client-main.log` then gives the reason. On Windows, and on Linux systems without shaderc (the tool that translates the shaders), Komet downloads it the first time (about 21 MB). Until then OpenGL keeps drawing. The chat tells you when to leave the world and join again.

**Switching back**: set **Renderer** to **OpenGL** again and apply. This also takes effect at once, and the terrain is rebuilt once more. Leaving the world ends Vulkan's work as well. The main menu and the loading screens are always drawn by OpenGL.

If Vulkan fails, OpenGL takes over from the next frame, and above the row it says **OpenGL (Vulkan failed, see the log)**. To try again, set **Renderer** to OpenGL and then back to Vulkan.

## The Vulkan options

All further options are on Komet › Vulkan. They only work while Vulkan is drawing. With OpenGL their rows are greyed out. The column "Default" shows what applies as long as you do not change an option yourself.

### Always shown

| Option | Default | What it does |
|---|---|---|
| Skip hidden terrain | 2 | Leaves out terrain you cannot see (0 to 2) |
| Save video memory | on | OpenGL gets no second copy of the large data |
| Bridge late frames (experimental) | on from 120 Hz | Shows a turned copy when a frame is late |
| Vulkan: measure GPU times | on | Measures the GPU's time per part of the frame |

### Advanced rows

These three rows only appear with Komet › Tools › **Show advanced options**. They are under **Exact optimisations**. The switch **All exact optimisations** turns them on or off together.

| Option | Default | What it does |
|---|---|---|
| Vulkan: even pacing | on | Waits for the GPU at the start of the frame |
| Spare idle OpenGL | on | Does not send shader values to the idle OpenGL |
| Vulkan: save shaders | on | Keeps translated shaders on disk |

### Details

- **Skip hidden terrain**: 0 as in the game, 1 also leaves out terrain behind other terrain, 2 lets the graphics card do all of the choosing. If your graphics card cannot do level 2, level 1 applies. More on this below under "Terrain and GPU culling".
- **Save video memory**: otherwise OpenGL keeps its own copy of everything, so it can take over at any time. With this option it keeps only small stand-ins for large textures and buffers. That saves uploads and usually several hundred MB. If Vulkan stops, Komet gives OpenGL the real contents back. The one exception is a failed graphics card: then you only see the GUI and have to join the world again. Switching works best before you join a world.
- **Bridge late frames**: pays off mostly on fast monitors and costs a little GPU time. How it works is explained below under "The frame bridge".
- **Vulkan: measure GPU times**: the times for terrain, sky, creatures, particles and effects appear in the HUD and in the log. With the HUD off, Komet measures only every 16th frame. Measuring costs a little performance.
- **Vulkan: even pacing**: when the graphics card is the bottleneck, the camera moves more evenly, with less microstutter.
- **Spare idle OpenGL**: saves about 0.15 ms per frame.
- **Vulkan: save shaders**: fewer stutters when things appear for the first time. More on this below under "Shader cache".

## Something looks wrong

1. Switch to OpenGL and look at the same spot. If it is wrong there too, it is not Vulkan's fault.
2. Open the HUD window (Ctrl+F7), tab **Render**. If it shows **… calls per frame missing**, click that line: it opens and names the kinds Vulkan cannot draw.
3. Look in `client-main.log` for lines starting with `Komet: Vulkan`, especially ones with `cannot draw`, `failed` or `stopped`.
4. If you use shader packs or shader mods, look at Shaders › Active shaders. A mod is marked there when its shaders cannot be translated for Vulkan.
5. Turn these options off one by one: **Save video memory**, **Bridge late frames**. After that, set **Skip hidden terrain** lower.
6. Try Komet › Tools › **Rebuild the terrain**.
7. If none of this helps, record a debug protocol while the problem is visible. How to do that and what belongs in a report is explained on the page "Troubleshooting".

---

## The big picture

From here on it is about the technology. You do not need any of it to play.

The game knows nothing of Vulkan. It still speaks OpenGL: it creates textures, uploads chunks, sets shaders and asks for draws, all through OpenGL calls.

Komet sits in between. With Vulkan chosen, Komet listens to every OpenGL call the game makes from the moment the mod starts. If you only switch once you are in the world, Komet fetches what already exists from OpenGL. In the code this listener is called the **tap**. It records what the game creates (textures, buffers, shaders, framebuffers) and what it asks for. Everything is drawn in Vulkan, with Komet's own copies.

OpenGL sits **idle** meanwhile. It still gets the uploads, the names and the settings, but it draws nothing of the world. That way it can take over at once if Vulkan stops.

```flow
The game calls OpenGL to draw
Komet's tap catches the call
Vulkan draws with Komet's copies
OpenGL only gets the data
Vulkan shows the finished picture
```

## The path of a frame

1. The frame begins before the game's first drawing step, so that all of the frame's work lands in Vulkan.
2. The terrain passes: water depth, shadow maps, the solid terrain, glass and water, water plants.
3. The rest of the scene: sky, creatures, particles, effects such as SSAO or god rays, and the passes of shader packs.
4. The GUI on top: hotbar, chat, menus, HUD.
5. Vulkan shows the finished picture in the game window (through the swapchain, see below).

Komet records the Vulkan commands on a thread of its own. That way the game's main thread waits less.

## Translating shaders

The game's shaders are written in GLSL for OpenGL. Vulkan needs them in another form: SPIR-V. Komet translates each shader itself, so neither you nor shader pack authors have to.

```flow
The game links a shader
Komet reads its source
Komet adapts it for Vulkan
shaderc translates it to SPIR-V
Komet builds a pipeline from it
```

The source already holds the game's settings and whatever mods have changed in it. Komet only changes the few parts that Vulkan needs differently, for example where values are stored. A **pipeline** is the shader together with all its drawing settings. Translating and building happen on worker threads, already while the world loads. A draw whose shader is not ready yet waits for it. While Komet prepares shaders, a card at the top centre of the screen shows the progress: **Komet · Vulkan prepares shaders: … of …**.

### shaderc

On Linux, Komet uses the system's shaderc first. On Windows, and on Linux systems without shaderc, Komet downloads it once from nuget.org (package Silk.NET.Shaderc.Native) and checks it against a fixed SHA-256 checksum. It is then kept in `VintagestoryData/Cache/komet-shaderc`. While it downloads, OpenGL keeps drawing. Once shaderc is there, you leave the world and join again, so that Vulkan starts. If the download fails, Komet tries again the next time you join the world.

### Shader cache

With **Vulkan: save shaders** on, translated shaders and pipelines are saved in `VintagestoryData/Cache/komet-vulkan`. From the next start, Komet reads them from disk instead of translating them again. This removes most stutters when the world, creatures or particles first appear. Even on the very first start, Komet builds common pipelines in advance, from a list that comes with Komet. After a Komet update, the shaders are translated once more.

## Terrain and GPU culling

Under Vulkan every resource is Vulkan's own: the chunk pools, the textures, the screen images. Komet adopts each chunk pool as soon as the game creates it, so new chunks do not cause an extra wait.

### Two passes

**Skip hidden terrain** lets the graphics card leave out terrain you cannot see:

- **0**: as in the game.
- **1**: also leaves out terrain hidden behind other terrain.
- **2**: the graphics card picks the terrain entirely by itself and also leaves out sides that face away from you. This takes load off the CPU.

For this the graphics card keeps a **depth pyramid**: a small picture of how far away things are. It tests in two passes, so that nothing shows up a frame late:

1. First pass: each chunk is tested against the last frame's pyramid (shifted along with the camera). What is clearly visible is drawn.
2. A new pyramid is built from the ground drawn so far.
3. Second pass: what the first pass hid is tested again against the new pyramid. Anything that turns out to be visible after all is drawn now, in the same frame.

Level 2 needs double-precision maths on the graphics card. If it cannot do that, level 1 applies. If the graphics card lacks an ability this culling needs in the first place (`drawIndirectCount`), Komet draws the solid terrain without any of this culling and writes that to the log.

## Showing the picture

Under Vulkan, Komet shows the picture in the game window itself, through a **swapchain**: a few screen images that take turns being drawn into and shown. It works on Linux (Wayland or X11) and on Windows. The swapchain follows the game's VSync setting. With VSync off, Komet uses the Mailbox mode when your driver offers it: each refresh of the monitor shows the newest finished frame, without tearing and without the game waiting for the monitor.

If the window changes size, the swapchain is created again. If Vulkan cannot get a swapchain at all, it cannot run, and OpenGL draws instead.

### The frame bridge

With **Bridge late frames** on, Komet watches for frames that come much later than usual, for example during a GC pause. When one is late, a separate thread shows the last frame again, turned to where the camera is turning. That way, looking around stays smooth.

- Only turning is bridged. Creatures and your own movement still run at the game's FPS.
- Your hand in first person, your body in third person and the GUI are shown as drawn, not turned.
- It pays off most on fast monitors. Komet turns it on by itself at 120 Hz and above.

## What can be missing

Vulkan can express almost everything the game does, but not every single OpenGL trick. When Komet meets a draw it cannot take over, this happens:

- That draw is **missing** from the picture. OpenGL does not fill the gap, because OpenGL's picture is never shown.
- Komet writes one line per kind to `client-main.log`: `Komet: Vulkan cannot draw …: it is missing from the picture`.
- The HUD window counts the missing calls per frame in the tab **Render** and names their kinds.

When Vulkan itself fails (through an error in Komet's Vulkan code or on the graphics card), Komet writes a warning such as `Komet: Vulkan failed: …` or `Komet: Vulkan stopped: …` to the log. Komet releases Vulkan and its memory, and OpenGL draws again from the next frame. The same happens when the system's main memory is nearly full (`Komet: Vulkan let go for this world: …`), so that the system does not kill the game.

## Known limits

- The renderer is experimental. What it cannot draw is missing from the picture.
- Other mods that call OpenGL directly mostly come along: Komet draws them in Vulkan too, as far as Vulkan can express it. What it cannot express is missing from the picture, but only under Vulkan.
- Shader packs work on both renderers.
- Do you write shaders yourself? What Vulkan expects from them is on the page "Shaders on Vulkan" under Modding.

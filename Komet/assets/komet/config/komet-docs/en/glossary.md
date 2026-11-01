# Glossary

This page explains the words you meet in Komet's menu, in the HUD and in the debug protocol. Each entry says what the word means and what it does in the game. You do not need to know any of this to play. It does help when you read a hint, a HUD line or a bug report.

## Picture and speed

- **Frame**: a single picture the game draws. Many frames a second make a moving picture.
- **FPS** (frames per second): how many frames the game draws in one second. More is smoother.
- **Frame time**: how long one frame took, in milliseconds (ms). 60 FPS means about 16.7 ms per frame. Lower is better. Even frame times feel smoother than a high but jumpy FPS.
- **1 % low**: the FPS of the slowest 1 % of frames. It tells you how bad the slow moments are. Komet's main goal is a better 1 % low, not only a higher average. The HUD also shows a 0.1 % low, the slowest 0.1 %.
- **Stutter, spike**: a single frame that takes much longer than the others. You see it as a short hitch. Komet's HUD counts a frame as a spike when it takes longer than 25 ms and at the same time longer than 2.5 times the average. It also names the likely cause.
- **Microstutter**: tiny, regular hitches, even when the FPS looks fine. They come from frames that are alternately short and long.
- **VSync**: the game waits for your monitor before it shows a new frame. This stops tearing (a visible cut line in the picture), but can add a little delay.
- **FPS cap**: an upper limit you set for the FPS. The game then rests between frames. It rounds the limit to whole milliseconds, though, which causes microstutter. Komet's **Even FPS cap** keeps it exact (only with VSync off). Without a cap, **Even frame times without a cap** evens out the frame times.
- **CPU** (processor) and **GPU** (graphics card): the CPU works out the game's logic and prepares the work, and the GPU draws the picture. Whichever of the two is slower limits your FPS.

## Memory

- **RAM** (main memory): the memory where the game keeps the world, creatures and everything else while it runs.
- **Video memory, VRAM**: the graphics card's own memory. It holds textures (the images on blocks and creatures), the terrain's shapes and the screen images. If it fills up, the game slows down a lot or crashes.
- **Allocation**: the game asks for a piece of memory for something new, for example for a list or a chunk's data. Many short-lived allocations become garbage soon after.
- **Garbage**: memory the game no longer needs but that has not been freed yet.
- **GC, garbage collection**: the cleanup that frees garbage. The game (built on .NET) does it on its own. While it cleans up, the game can pause for a moment, which you see as a stutter. Komet creates less garbage and, where it can, moves the cleanup into quiet moments. The HUD shows how much of the worst frame was a GC pause.

## World

- **Block**: one cube of the world, 1 × 1 × 1.
- **Chunk**: a piece of the world of 32 × 32 × 32 blocks. The game loads, builds and draws the world chunk by chunk.
- **Chunk column**: all chunks stacked on top of each other at one spot, from the bottom of the world to the sky.
- **View distance**: how far you can see, in blocks. More view distance means many more chunks to build, keep in memory and draw.
- **Mesh**: the shape of something in the form the graphics card needs, that is many small triangles with their corners, texture and colour. Each chunk has a mesh.
- **Tessellation (chunk building)**: turning a chunk's blocks into its mesh. This happens on worker threads, that is helpers that run beside the main game. Komet builds the nearest chunks first.
- **Upload**: sending a finished mesh or texture to the graphics card. Too many uploads in one frame cause stutter, so Komet spreads them over several frames (**Chunk upload time**).
- **Pool**: a large piece of video memory in which the meshes of many chunks sit together. Drawing from a few pools is faster than from thousands of small pieces.

```flow
You place or break a block
A worker thread rebuilds the mesh
The mesh is uploaded to the graphics card
It lands in a pool
The graphics card draws the pool
```

## Drawing

### Renderer and shaders

- **Renderer**: the part that draws the picture. In Komet you choose between OpenGL and Vulkan (Komet › Overview › **Renderer**).
- **OpenGL**: an older, widely used way for programs to talk to the graphics card. Vintage Story uses it. It is the safe choice.
- **Vulkan**: a newer way to talk to the graphics card. It gives a program more control and lets it do some work with less waiting. Komet's Vulkan renderer is experimental. More on the page "Vulkan explained".
- **Driver**: the software from your graphics card's maker that turns OpenGL or Vulkan calls into work for the card.
- **Shader**: a small program that runs on the graphics card. It decides where each corner lands on screen and what colour each pixel gets. Water, shadows and light are made by shaders.
- **GLSL**: the language the game's shaders are written in.
- **SPIR-V**: the form Vulkan needs its shaders in. Komet translates the game's GLSL into SPIR-V.
- **shaderc**: the tool Komet uses for that translation. If your system has none, Komet downloads it once.
- **Uniform**: a value the game hands to a shader while the game is running, for example the time of day or the camera's position. Changing it costs almost nothing.
- **Define**: a fixed setting written into a shader's text before it is translated. If a define changes, the shaders must be reloaded, which takes a moment.
- **Pipeline**: a shader together with all the drawing settings Vulkan needs for it. Building one takes a while the first time, so Komet can save them (see **Shader cache** below).
- **Swapchain**: a few screen images that take turns being drawn into and shown in the window. Under Vulkan, Komet shows the picture through a swapchain of its own.

### Steps of a frame

- **Pass**: one step in drawing a frame. For example: first the shadows, then the solid terrain, then water and glass, then effects, finally the GUI on top.
- **Draw call**: an order to the graphics card such as "Draw this mesh now". Fewer draw calls means less work for the CPU.
- **Shadow map**: a picture of the world as the sun sees it. It tells the game what lies in shadow. Komet's **Distant shadows** draws shadows out to your view distance.
- **SSAO** (ambient occlusion): darkens corners and gaps a little, so the world looks deeper.
- **God rays**: sunbeams that fall through gaps, for example between trees.
- **GUI**: everything drawn on top of the world: hotbar, menus, chat, HUD.

### Saving work

- **Culling**: leaving out what you cannot see, so the graphics card does not draw it for nothing.
- **Frustum culling**: leaves out what lies outside your field of view (behind you, or to the side).
- **Occlusion culling**: leaves out what lies inside your view but is hidden behind something else, for example a valley behind a mountain. Komet's **Skip hidden terrain** does this on the graphics card, under Vulkan only.
- **Depth pyramid**: a small, coarse picture of how far away things are on screen. Komet tests chunks against it to find out quickly whether they are hidden.
- **Shader cache**: translated shaders and pipelines saved on disk, so the next start does not have to build them again. That means fewer stutters when things appear for the first time. Komet saves them with **Vulkan: save shaders**.

## Komet itself

- **Feature**: one part of Komet that changes one thing in the game, for example how chunks are uploaded. Each feature has a state: active, off, held off, stood down and so on.
- **Switch, knob**: a setting that turns a feature on or off, or sets how much it does (a slider). In the code they are called knobs.
- **Preset**: a ready set of values for several of Komet's trade-offs at once. You pick it on Komet › Overview in the row **Settings**: **Automatic (recommended)**, **Quality**, **Balanced** or **Speed**. What you change yourself afterwards stays, and the row then shows **Adjusted**. More on the page "The options menu".
- **Exact optimisation**: a switch that gives the same result as the game, only faster or leaner. These switches are hidden until you turn on Komet › Tools › **Show advanced options**.
- **Harmony patch**: Komet and many other mods use a library called Harmony to change the game's code while it runs. A patch is one such change to one method (a piece of the game's code).
- **Stood down**: if another mod patches the same method a Komet feature relies on, Komet turns that feature off and lets the game do the work as usual. The HUD and the log tell you.
- **Held off**: another mod has asked Komet to leave a feature at the game's own behaviour. The feature's switch is then locked and names the mod.
- **Engine fingerprint**: a checksum of the game's code that a feature replaces. If a game update changed that code, the fingerprint no longer matches, and Komet does not install the feature, so nothing breaks.

## Measuring

- **HUD**: Komet's measuring display. It consists of the overlay and the HUD window. More on the page "HUD and measuring".
- **Overlay**: the small display in one corner of the screen. F7 turns it on and off. You choose what it shows (FPS, lows, frame time, graph, mods) on Komet › HUD.
- **HUD window**: a window with tabs such as Frames, Render and Log that shows all measurements in more detail. Ctrl+F7 opens it.
- **Debug window**: a window in which you record, read or copy a debug protocol. Ctrl+F8 opens it.
- **Debug protocol**: a plain-text report of everything Komet measures during a recording, with the log lines from that time. You create it in the debug window or with `.komet debug`. It goes to the clipboard and to the folder `Logs/komet-debug`. It helps a lot with bug reports. More on the page "Troubleshooting".
- **Benchmark**: a measurement over a fixed time, started on Komet › Tools › **Benchmark** (you set its length with **Benchmark length**). The averages then go to the clipboard and the client log.

## Modding

- **Shader pack**: a folder with a `komet-shader.json` and shader files that brings picture effects and sliders of its own. It lives in `ModConfig/komet/shaders` or in a mod. Each pack gets a page of its own under Shaders.
- **Shader mod**: a mod that replaces the game's shaders or brings its own. Komet shows it under Shaders › Active shaders.
- **Template**: a ready example folder that Komet creates for you on Modding › Templates: a shader pack, a style, a small mod or a shader package for Vulkan. You can read it, change it and learn from it.
- **Style**: how this menu looks, Pragmatic out of the box (Komet › Style). Your own styles live in `ModConfig/komet/styles`.

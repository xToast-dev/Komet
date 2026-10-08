# Changelog

## 3.0.0-pre1

### New

- **Slimmer settings**: only the options worth weighing are shown (budgets, threads, visuals, experimental features, Vulkan mode);
  every engine-exact optimisation stays on and appears with "Show advanced options" (Tools page).

- **Debug protocol** (`/komet debug`): records every collector for a while, a fixed time, or until a frame crosses a threshold,
  and writes one plain-text protocol (English or German) with findings, frame statistics, spikes, GC, threads, rendering, mods,
  Harmony patch conflicts with their risk and the log, compared with the previous protocol. Copied to the clipboard and written to
  `Logs/komet-debug/` with every frame as CSV and the full patch registry.
- **Mod profiler** (`/komet profile <modid>`): times one mod's entry points on demand (overrides, interface implementations,
  listeners and its Harmony patches) with total and self time per method, then removes its patches. Per method also the bytes it
  allocated itself (memory churn), its draw calls and its peak time inside one frame; for the mod its main-thread time per frame as
  a graph with p99, worst frame and frames over 2 ms, its share of everything allocated, the collections meanwhile, and the static
  collections its types hold. Compiler names read as the method they are in (`SmokeSystem.Register (lambda)`).
- **`KometDebug` API**: other mods add named timings (`Measure`) and sections of their own to the debug protocol.
- **New HUD**: the floating panels are gone. F7 shows a compact overlay (FPS, lows, frame time, graph, the mods' time with up to
  three pinned mods, a hint for each spike with its cause); Ctrl+F7 opens a window with tabs for everything the panels showed
  (overview with one-click hints, frames, system, render, threads, mods, log, settings); Ctrl+F8 opens the debug window, which
  starts captures and shows the protocol with a scrollbar. In the Mods tab a click pins a mod to the overlay, a right-click times
  its methods for 10 s.
- **Edition and version in the HUD**: overlay and window name the build as Release, Pre-Build or Development with its version.
  The build decides it from `modinfo.json` (a version with a suffix such as `3.0.0-pre1` is a Pre-Build, a Debug build is
  Development) and fails on a version that is not `major.minor.patch[-suffix]`. A click on the badge opens the Version page: commit,
  build time, the update check against GitHub and the installed file's SHA-256 beside the published one, digit by digit. It
  replaces the old checksum window. The overview warns when a newer build is out or the checksum differs.

- **Experimental: singleplayer server in its own process** (Settings → Other → Server, off; only for those who want it): a singleplayer world's server runs as
  a child process of the game's own server program on 127.0.0.1 and the game joins it, so the server's allocations no longer
  trigger the game's garbage collections. Nothing to set up: Komet finds a free port, sets a one-off password, gives the child
  server GC, keeps `serverconfig.json` as it was, pauses the child with the escape menu and stops it (saving) when the player
  leaves or the game ends. The first world after the game start is handed over before any chunk loads; a new world, less than
  16 GB of RAM or a child that does not come up start the usual way. Komet's server patches (LightRepair, ChunkThreadClosure,
  ColumnNoiseScratch, WorldGenScratch) run in the child. Flying (Vulkan, view distance 1536): collections 9.8 → 5.2/s, GC share
  6.7 → 3.8 %, frames over 25 ms 134 → 79–117/min, longest pause 37 → 20 ms, 1 % low 29.4 → 31–32 fps, the game's heap
  5.9 → 4.1 GB; loading the first world takes about 15 s longer and the child holds about 3 GB of RAM; the average frame time is unchanged. `modinfo.json` now says `Universal` (required on neither side)
  so the integrated server loads Komet for the handover.

### Faster

- **Occlusion culling rays** (CullerRays): the engine's culling thread walks its rays without vector objects, about 11 % of all CPU
  work while a world loads.
- **World map** (MapReads): a map piece locks each chunk once instead of once per block read.
- **Less lock contention while loading**: weather regions, climate maps and the mesh recycler are read without waiting.
- **Less garbage**: decor meshes are built per tessellation in a reused buffer (ClutterMeshes), chiseled blocks' texture names are
  made once (InsideTextures), world-gen structure data is encoded without temporary buffers (Ascii85Text).

- **Own tessellator** (OwnTessellation, on): cubes, soil, block layers, tall grass and simple models, leaves included, are drawn
  by Komet's own code that writes straight into the mesh arrays; the engine keeps every other block. Byte-identical with the
  engine (golden test). In the bench world it draws about 97 % of those blocks.
- **Smaller tessellation garbage** (PartRecycling): small chunk parts, decor lookups and upload lists reuse their buffers.
- **Block entities on chunk load** (BlockEntityBudget, BlockEntityCaches, ListenerSlots): far chunks initialise their block
  entities within the chunk load budget over a few frames; ground storage, firepits and pots on the fire work out their settings,
  meshes and shapes once; tick listener and renderer registration no longer search their lists.
- **Idle animated block entities skipped** in every render stage, not only the opaque one (IdleAnimators): about 12 000 calls a
  frame in the bench world.
- **Less garbage while flying**: the singleplayer server's chunk thread reuses its request list instead of making one every tick
  (about 11 MB/s, WorldGenScratch); particles keep their block walk callback and the room registry its set of chunks to drop
  for changed blocks (HandlerLists).
- **Creative inventory scrolls smoothly**: a row of creature items (deer, moose, traders) parsed shape files of up to 1.7 MB
  with every animation the first time it scrolled into view, up to 2 s and 800 MB of garbage in one frame. Creature items and
  clothing now build their inventory mesh from the shape without its animations, read from a pooled buffer (InventoryShapes),
  and the creative inventory draws items it never drew before within 6 ms a frame, the rest a few frames later (IconBudget).
  Scrolling through the whole "Everything" tab: worst frame 2139 → 60 ms, 1 % low 5.5 → 22.7 fps.
- **The GUI's item icons in Vulkan without a uniform block a draw**: a program whose blocks still pack anew in a quarter of its
  draws after Komet settled its hot uniforms is watched again (the GUI's first draws are the hotbar's, its item icons change
  other uniforms), and the push constants are budgeted as std430 lays them out.
- **Less garbage from world generation** (WorldGenScratch, singleplayer): sunlight spreading reuses its block positions, rock
  strata reuse their weight array and patch placement caches block code parts. Allocation while flying 89 → 82 MB/s, 9 % fewer
  gen0 collections.
- **Tessellation 0.25 ms faster per pass**: FaceSorting only sorts faces while the GPU culls rows of them, its only reader.
- **Less garbage from the map while exploring**: the pool of map piece arrays keeps up to 4096 instead of 512; pieces arrive in
  bursts of hundreds (900 a second flying at 20), and one in eight went to the collector.
- **Far shadows 1 ms cheaper** (ShadowCasters): the shadow passes leave out casters whose shadow cannot reach the camera's view
  (outside one of its sides, moved out by 8 blocks, with the light not crossing that side inward), the rule terrain culling on the
  GPU already used. Shadows are unchanged (screenshots compared pixel by pixel). Flight benchmark: 1.15 ms faster a frame (t −13.7),
  far shadow pass 1.4 → 0.4 ms GPU.
- **Slow frames leave their uploads to the next** (UploadYield): a frame that reaches its chunk uploads more than 6 ms later than
  usual (a collection, a burst of block entities) uploads only one chunk, never two frames in a row; edits and chunks next to the
  player still go at once. 1 % low +0.5 to +0.9 fps, pop-in unchanged.
- **No main thread waiting behind world generation**: chunk uploads' background packs ran on .NET's shared pool behind the
  integrated server's world generation while the main thread and Vulkan's recording thread spun for them; whoever waits now packs
  what nobody has started.
- **Vulkan: less work every frame**: pipeline statistics are queried only while the HUD shows them (every fourth frame around the
  opaque terrain otherwise), an unchanged glDrawBuffers no longer rebuilds the target's outputs, the frame's shared images are
  listed anew only when a texture copy is made or let go, and the scene's hooks are no longer wired anew every frame.
  Flight benchmark against before: 1 % low 35.6 → 38.8 fps, 0.1 % low 27.6 → 33.1 fps, GC 5.4 → 4.7 % of the time.
- **Less garbage from Vulkan**: dispatches and buffer copies reuse their recorded state instead of a closure and two descriptor
  arrays each, and every switch between compute and drawing its barrier and rendering state. Main thread 10.1 → 9.4 MB/s, with
  terrain culling fully on the GPU 15.1 → 10.8 MB/s.
- **No more multi-second freezes from full collections** (GcLatency): the garbage collector keeps full collections in the
  background instead of stopping the game to compact the whole heap (2.7 s at view distance 1536). Under real memory shortage
  the runtime still stops.
- **Chunk uploads off the main thread's critical path**: an unbind after every chunk part held the main thread in the driver for
  9 % of the frame while flying, and the colour and custom attribute copies went across the bus into VRAM; they now go to host
  staging with one GPU copy each, the colours copied by the background pack. Main thread share of uploads 10 % -> 2 %.
- **Less garbage per frame in the Vulkan renderer**: texture sets of up to eight samplers are cached (the chunk shaders bind
  more than four, so every pool draw built a dictionary and a descriptor array), and no closure per draw or tessellation pass.

### Fixed

- **Settings pages repeated group headings** (culling, drawing, culling, …); each group now appears once.

- **Trees without leaves with terrain culling fully on the GPU** (Vulkan): uploads that did not fit the frame's 32 MB staging were
  dropped, so whole pools drew stale rows. Every upload of Vulkan's compute work now takes a buffer of its own past the staging,
  kept until the GPU has done the frame.
- **Features stepping aside for Komet itself**: a Komet patch on another feature's engine method counted as another mod's
  (OccludedChunks never ran); Komet's own patches no longer count.
- **A crash while joining** ("Collection was modified" in WeatherSystemClient.OnAssetsPacket): the weather's region walk now
  holds the lock its writers take (WeatherLock). More tessellation threads made the engine's race likelier.

### Changed

- **Terrain culling fully on the GPU is on by default** and takes effect with occlusion culling (still experimental and off): with
  Vulkan 1.5 ms faster a frame on average in the flight benchmark, its 1 % lows from equal to 1.3 fps lower; in OpenGL slower, as
  occlusion culling itself is there.
- **Benchmark**: `"gui": "creative"` scrolls the creative inventory during still segments; `--build` no longer leaves build
  servers holding the GPU lock, and a crashed run's crash reporter is closed.

### Removed

- The old panels' drawing code, the "Paint the HUD off the main thread" switch and 111 unused texts. The report rows stay as data
  for the bench and the debug protocol; `/komet dump` is gone, the options' "copy the values" writes a debug protocol of right
  now instead (it was empty since the new HUD).

## 2.0.0 — 2026-09-27

Komet 2.0.0 is a complete rewrite for Vintage Story 1.22 (verified against 1.22.7). Every feature produces the same result as the
engine, checks the engine code it replaces by fingerprint and steps aside when the game changed or another mod patches the same
method. The focus is on stable frame times: the 1 % and 0.1 % lows.

### New

- **Options menu**: a full-screen menu in the style of Sodium replaces the in-game settings (Escape → Settings, or `.komet`):
  search over all options, sidebar with the game's pages, Komet's pages and other mods' pages, changes applied at once with a
  single shader reload, a controls page with rebinding and conflict highlighting. The original menu stays one click away.
- **Distant shadows**: terrain casts shadows up to the view distance. The engine's shadows end after about 200 blocks; a third,
  texel-stable shadow map covers the rest and is redrawn only when the sun has moved or you travelled far, one sixteenth per frame,
  so it costs no measurable frame time.
- **Light repair** (singleplayer): chunks saved without light - drawn pitch black, often whole areas or rivers - are relit
  automatically when they load and by a background sweep, and saved repaired. The engine's `FullRelight` (WorldEdit,
  `/debug relight`, the timeswitch, `/wgen`), which caused them, now runs without its two faults: it no longer wipes the light of
  partly loaded columns and no longer loses torch light.
- **Worker pool**: one pool of worker threads for frustum culling and chunk tessellation, frame work first.
- **Rendering**: vectorized and parallel frustum culling per render stage (FrustumSweep, FrustumStages), larger mesh pools with a
  quarter of the draw calls (PoolScale), culling of animated block entities outside the view and shadow volumes
  (AnimatableCulling), idle block entity animations skipped (IdleAnimators), GL error checks only every 16th frame (GlErrorPoll).
- **Less garbage**: entity partitioning (PartitionReuse), cooking recipe checks (CookingMatch), climate and wind handler lists
  (HandlerLists), plant sink offsets in the tessellator (TessBlockPos).
- **HUD** (F7): frame time graph, 1 % / 0.1 % lows, spikes with their cause, render passes, mod times, mods & patches, Komet
  counters, main and debug log.
- **Update check** (opt-in): looks for a newer build of the installed channel on GitHub and compares the checksum of the installed
  zip with the published one.
- **Mod API**: `KometOptions` lets other mods add their pages to the options menu; `KometFeatures` lets them query Komet's features,
  hold one on the engine's behaviour, or register features of their own. `Komet.Testing` provides test rigs for other mods.
- **Benchmark**: `scripts/bench.sh` flies a fixed route unattended and compares builds or switches in one process.

### Changed

- Rebuilt from the ground up: code organized by area (rendering, chunks, tessellation, shapes, world, HUD, diagnostics,
  benchmark), a feature registry that drives install order, settings, HUD, options menu and benchmark, and a Roslyn analyzer that
  enforces the project's rules. The code was trimmed by about 900 lines without any change in behaviour.
- Server-side patches (light repair, world generation, chunk thread) only install when the server runs in the same process, i.e.
  in singleplayer; on a dedicated server Komet only works on the client.
- All features are on by default and can be switched off one by one; off means the engine does the work.

### Fixed

- Black chunks and black rivers in worlds that were relit with a partly loaded area (see Light repair).
- Torches and other light sources losing their light after a relight.
- Minutes-long 8–13 ms frames near beehives (ParticleLight).

### Compatibility

- Vintage Story 1.22.x, client side; features whose engine code differs from 1.22.7 stay off and say so in the log and the HUD.
- If another mod patches a method a feature replaces, that feature steps aside; with another mod's shader pack, distant shadows stay
  off.
- Remove older Komet zips from the `Mods` folder: two mods with the id `komet` and the same version load only one of them.

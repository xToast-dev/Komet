# Changelog

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

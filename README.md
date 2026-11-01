# Komet

Komet is a performance mod for Vintage Story. It replaces expensive parts of the engine with code that gives the same
result, can draw the world with a Vulkan renderer of its own, and brings a HUD that measures frame times, lows and
spikes. Its options menu replaces the game's settings screen; every speed-up has a switch there, and off means the
game does the work. Shader packs and other mods can add their own settings, shader values and passes to that menu.
If a game update changes an engine method or another mod patches it, Komet leaves that method to the game and says
so in the log and the HUD.

## Requirements

- Vintage Story 1.22
- Client-side: servers do not need it. In singleplayer it also speeds up the built-in server.

## Install

Drop the release zip (`komet_<version>.zip`) into the game's `Mods` folder.

## Documentation

- **Players and mod authors**: in game, Escape › Settings, sections **Help** and **Modding**.
- **Code authors**: the developer site in `docs/site` (API reference and articles), built with `scripts/apidocs.sh`.
- **Maintenance**: [MAINTAINING.md](MAINTAINING.md).

## Building

Needs the .NET 10 SDK and Vintage Story in `/opt/vintagestory` (otherwise set `VsInstall=…`).

```bash
./build.sh                         # Releases/komet/ and Releases/komet_<version>.zip
scripts/test.sh                    # the quick tests; --all for every test
scripts/bench.sh --build --profile quick   # starts the game in a sandbox and flies one lap
```

## Menu style

The options menu's style is called Pragmatic.

**Pragmatic's look and name are a nod to the menus of Capcom's game Pragmata. Komet has no connection to Capcom and is not endorsed by it.**

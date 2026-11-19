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

## What it brings

- **Engine speed-ups**: chunk loading and building, culling, shadows, shapes and GC pressure, each with its own switch.
- **Vulkan renderer**: optional, switched in the options menu; off, the game draws with OpenGL.
- **HUD**: frame times, lows, spikes and per-mod costs.
- **Shader Studio**: a shader editor for shader authors (`Komet.Sdk`), Ctrl+F8 in game.
- **For other mods**: settings, shader values and passes through `komet.json` / `komet-shader.json`
  (schemas in `docs/schema`) and the API, KAPI 1.0 (`Komet.Api`), in `Komet.Core.dll`.

## Documentation

- **Players and mod authors**: in game, Escape › Settings, tab **Help** (players first, then Modding).
- **Code authors**: the developer site in `docs/site` (API reference and articles), built with `scripts/apidocs.sh`.
- **Maintainers**: [MAINTAINING.md](MAINTAINING.md), [docs/CODING-RULES.md](docs/CODING-RULES.md),
  [docs/WORKBENCH.md](docs/WORKBENCH.md).

## Building

Building with C# 14 requires the .NET 10 SDK specified in `global.json` and a Vintage Story installation.
Windows defaults to `%APPDATA%/Vintagestory`. Linux checks
`$XDG_DATA_HOME/vintagestory`, `~/.local/share/vintagestory`, then `/opt/vintagestory`.
Set `VINTAGE_STORY` or pass `-p:VsInstall=…` for another installation.

```bash
./build.sh                         # Releases/komet/ and Releases/komet_<version>.zip
scripts/test.sh                    # the quick tests; --all for every test
scripts/bench.sh --build --profile quick   # starts the game in a sandbox and flies one lap
```

On Windows, build and run the portable tests from PowerShell:

```powershell
dotnet build Komet/Komet.csproj -c Release -t:Package
dotnet test tests/Komet.Portable -c Release
```

## Menu style

The options menu's style is called Pragmatic.

**Pragmatic's look and name are a nod to the menus of Capcom's game Pragmata. Komet has no connection to Capcom and is not endorsed by it.**

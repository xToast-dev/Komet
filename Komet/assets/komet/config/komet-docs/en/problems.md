# Narrowing down problems

## What happens for you?

| Problem | Try first |
|---|---|
| World invisible or picture black | Set the renderer to [[OpenGL]] and join the world again |
| Single stutters | Take a [[spike capture]] with {key:komet-hud-window} |
| A setting is locked | With [[developer mode]] on, open **Developer › Compatibility** |
| Shaders missing or black | Search the [[log]] for the shader's name |

## Reporting a bug

Send us:

- the game version and Komet version
- your [[renderer]] and your mod list
- what you did until the bug came
- `client-main.log`, for stutter the spike capture too

Change only one setting at a time while testing.

[HUD and measuring](docs-hud)

[Compatibility guide](docs-compatibility)

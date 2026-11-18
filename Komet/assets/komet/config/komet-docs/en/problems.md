# Troubleshooting

## Find your symptom

| Symptom                            | First check                                                              |
|------------------------------------|--------------------------------------------------------------------------|
| World invisible or picture freezes | Restart the game and select OpenGL                                       |
| Intermittent stutter               | Open Ctrl+F7 and arm a spike capture                                     |
| Optimization locked                | Open Komet › Compatibility and copy details                              |
| Shader missing or black            | Open the shader overview and look up the affected pass in the client log |

## Report a problem

Include game and Komet versions, renderer, mods and reproduction steps. Attach `client-main.log` for graphical issues
and a debug capture for stutter. Change one setting at a time during comparisons.

[HUD and measurements](docs-hud)

[Compatibility guide](docs-compatibility)

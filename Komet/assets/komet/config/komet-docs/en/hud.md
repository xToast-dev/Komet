# HUD and measurements

## Keys

| Key     | Purpose                                                                  |
|---------|--------------------------------------------------------------------------|
| F7      | Toggle the overlay                                                       |
| Ctrl+F7 | Dockable HUD window: metrics, protocol, captures and every other tool    |
| Ctrl+F8 | Shader Studio (needs the Komet SDK; without it, a note where to get it)  |

## Measure a hitch

Go to the affected area, open the HUD window (Ctrl+F7) and record a protocol or arm a spike capture for the next hitch.
Copy the report afterwards.

Watch frame time, GC pause, JIT time and the longest frame sections: a high FPS figure can hide single slow frames.
Compare runs with the same world, view distance and mods.

[Troubleshooting](docs-problems)

# Start from a template

## Generate a template

1. Open Modding › Templates and create a **Basic shader mod project** or a **Shader mod project with optional C#
   integration**.
2. Open the generated folder under `ModConfig/komet/` and read its README.
3. Replace the sample ID in `modinfo.json`, the asset domain and the C# code.

## Test in game

1. Copy the mod folder into the Mods folder and join a world.
2. Check that settings and passes appear under Shaders.
3. For optional integration, test with Komet disabled as well.

[Shaders and passes](docs-shaders)

[Renderer integration for mods](docs-vulkan-shaders)

# Start modding

## Choose your goal

[Integrate an existing mod](docs-integration)

[Try a template](docs-tutorial)

[Check compatibility](docs-compatibility)

[Register shaders and passes](docs-shaders)

[Support OpenGL and Vulkan](docs-vulkan-shaders)

[Style the menu](docs-styles)

[Open the API reference](docs-api)

## Files and code

Put declarative integration in `assets/<modid>/config/komet.json`. For C#, reference `Komet.Core.dll` at build time
and connect through KAPI 1.0 (`Kapi.Connect(this, "1.0")`). For optional integration follow "Optional Komet
integration".

# Renderer and Vulkan

## Choose a renderer

Komet › Overview › Renderer offers OpenGL and experimental Vulkan. Shader mods may require OpenGL when they access
OpenGL resources directly.

## Safety mode

With low available RAM or a prior failed Vulkan run, Komet may stay on OpenGL before joining a world. See the HUD and
log for the reason. If rendering is incomplete, restart and test with OpenGL.

## Compare

Use the same scene, view distance and shaders. Check frame times, visual defects and memory alongside FPS.

[Porting shaders to Vulkan](docs-vulkan-shaders)

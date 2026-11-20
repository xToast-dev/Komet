# Renderer and Vulkan

## Choosing the renderer

Under **Komet › Overview › Renderer** you choose the [[renderer]]: [[OpenGL]] as in the game without mods, the faster,
still experimental [[Vulkan]] that also presents the window, or **Vulkan compute**. The last one starts the Vulkan
device and keeps GPU culling on OpenGL's depth pyramid while OpenGL draws and presents; use it on native Wayland with
NVIDIA, where Vulkan cannot take the window.

Some shader mods need OpenGL. Komet tells you which.

## When something is missing

- If graphics are missing or the picture stays black, switch to OpenGL and join the world again.
- Komet falls back to OpenGL by itself when Vulkan fails. The [[log]] says why.
- With little memory free Komet starts in [[safety mode]] on OpenGL.

## Comparing

Compare both renderers at the same place with the same view distance. Watch the [[frame time]] and look for glitches.

[Porting shaders to Vulkan](docs-vulkan-shaders)

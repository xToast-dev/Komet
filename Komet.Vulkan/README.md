# Komet.Vulkan

> Machine-translated from German; wording may be rough in places.

The Vulkan renderer: device and memory, the GL tap (reads along every OpenGL call of the game), terrain, scene,
swapchain and the shader ports to SPIR-V. Fills the hooks of `Komet.Rendering` (via `Komet/Modules.cs`). Uses
`Komet.Core`, `Komet.Diagnostics`, `Komet.Rendering`, `Komet.Chunks` and `Komet.Shapes`.

Features: `VulkanFeatures.cs`.

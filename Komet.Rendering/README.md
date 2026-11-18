# Komet.Rendering


The game's OpenGL drawing path, sped up: culling, mesh pools and uploads, shaders and shader packs, shadows,
frame pacing. Knows nothing of Vulkan; whatever it needs from there it asks through `Links.cs`. Uses `Komet.Core` and
`Komet.Diagnostics`.

Features: `RenderingFeatures.cs`.

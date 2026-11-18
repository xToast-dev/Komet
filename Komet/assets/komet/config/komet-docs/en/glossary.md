# Glossary

## Playing

- **Term**: A blue, underlined word. Click it and Komet explains it.
- **Preset**: A ready choice of all of Komet's settings: Quality, Balanced or Speed.
- **HUD**: The display over the game with frame time, FPS and memory.
- **Frame time**: How long one picture takes, in milliseconds. You feel its spikes as stutter.
- **Spike capture**: Komet waits for the next stutter and records what took the time in that moment.
- **Log**: The file `client-main.log` in the `Logs` folder, where the game writes down what happened.
- **Developer mode**: A game setting under Settings › Interface. It unlocks extra tools.

## Graphics

- **Renderer**: The part that draws the picture.
- **OpenGL**: The game's renderer. It runs everywhere.
- **Vulkan**: Komet's own renderer. Often faster, but still experimental.
- **Safety mode**: Komet stays on OpenGL because Vulkan failed last time or too little memory is free.
- **Shader**: A small program on the graphics card that works out colours and light.
- **Shader Studio**: Komet's editor for shader authors.
- **Pass**: One drawing step, for example the terrain or an effect over the whole picture.
- **Framebuffer**: A picture in memory that a pass draws into.
- **Tessellation**: Blocks and models become triangles the graphics card can draw.

## Technical

- **GC**: The .NET runtime cleans up memory. Sometimes it pauses the game briefly for that.
- **JIT**: The .NET runtime translates code when it first runs. That can stutter once.
- **KAPI**: Komet's interface through which other mods register settings, shaders and drawing steps.
- **Define**: A fixed value put into a shader when it is translated.
- **Uniform**: A value the game hands to a running shader.
- **SPIR-V**: The format in which Vulkan reads shaders.

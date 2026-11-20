# Renderer und Vulkan

## Renderer wählen

Unter **Komet › Übersicht › Renderer** wählst du den [[Renderer]]: [[OpenGL]] wie im Spiel ohne Mods, das schnellere,
noch experimentelle [[Vulkan]], das auch das Fenster zeigt, oder **Vulkan Compute**. Letzteres startet das Vulkan-Gerät
und lässt die GPU-Culling auf OpenGLs Tiefenpyramide laufen, während OpenGL zeichnet und das Bild zeigt. Nutze es auf
nativem Wayland mit NVIDIA, wo Vulkan das Fenster nicht übernehmen kann.

Manche Shader-Mods brauchen OpenGL. Komet sagt dir dann, welche.

## Wenn etwas fehlt

- Fehlt Grafik oder bleibt das Bild schwarz, stell auf OpenGL und tritt der Welt neu bei.
- Komet fällt selbst auf OpenGL zurück, wenn Vulkan scheitert. Das [[Log]] sagt, warum.
- Bei wenig Arbeitsspeicher startet Komet im [[Sicherheitsmodus]] mit OpenGL.

## Vergleichen

Vergleich beide Renderer an derselben Stelle mit derselben Sichtweite. Achte auf die [[Framezeit]] und auf Bildfehler.

[Shader auf Vulkan portieren](docs-vulkan-shaders)

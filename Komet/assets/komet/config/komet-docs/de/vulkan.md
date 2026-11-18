# Renderer und Vulkan

## Renderer wählen

Unter Komet › Übersicht › Renderer kannst du OpenGL oder das experimentelle Vulkan wählen. Shader-Mods können OpenGL
verlangen, wenn sie direkt auf OpenGL-Ressourcen zugreifen.

## Sicherheitsmodus

Bei knappem RAM oder einem früheren fehlgeschlagenen Vulkan-Lauf kann Komet schon vor dem Weltbeitritt auf OpenGL
bleiben. HUD und Log zeigen den Grund. Wenn Darstellung fehlt, starte neu und teste mit OpenGL.

## Vergleichen

Vergleiche dieselbe Szene mit gleicher Sichtweite und gleichen Shadern. Prüfe neben FPS auch Framezeit, Bildfehler und
RAM-Verbrauch.

[Shader auf Vulkan portieren](docs-vulkan-shaders)

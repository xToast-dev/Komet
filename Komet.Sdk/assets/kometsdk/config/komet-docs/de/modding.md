# Modding starten

## Wähle dein Ziel

[Bestehende Mod optional anbinden](docs-integration)

[Vorlage ausprobieren](docs-tutorial)

[Kompatibilität prüfen](docs-compatibility)

[Shader und Passes registrieren](docs-shaders)

[OpenGL und Vulkan unterstützen](docs-vulkan-shaders)

[Menü gestalten](docs-styles)

[API-Referenz öffnen](docs-api)

## Dateien und Code

Lege deklarative Integration unter `assets/<modid>/config/komet.json` ab. Für C# referenziere `Komet.Core.dll` zur
Build-Zeit und verbinde dich über KAPI 1.0 (`Kapi.Connect(this, "1.0")`). Für optionale Integration folge „Komet
optional integrieren“.

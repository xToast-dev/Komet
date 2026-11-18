# Mit einer Vorlage beginnen

## Vorlage erzeugen

1. Öffne Modding › Vorlagen und erstelle ein **Einfaches Shader-Mod-Projekt** oder ein **Shader-Mod-Projekt mit
   optionaler C#-Anbindung**.
2. Öffne den erzeugten Ordner unter `ModConfig/komet/` und lies seine README.
3. Ersetze die Beispiel-ID in `modinfo.json`, die Asset-Domain und den C#-Code.

## Im Spiel prüfen

1. Kopiere den Mod-Ordner in den Mods-Ordner und lade eine Welt.
2. Prüfe, ob Einstellungen und Passes unter Shader erscheinen.
3. Für optionale Integration teste zusätzlich mit deaktiviertem Komet.

[Shader und Passes](docs-shaders)

[Renderer-Integration für Mods](docs-vulkan-shaders)

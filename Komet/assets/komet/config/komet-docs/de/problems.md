# Probleme eingrenzen

## Symptom auswählen

| Symptom                                     | Erster Test                                                          |
|---------------------------------------------|----------------------------------------------------------------------|
| Welt bleibt unsichtbar oder Bild friert ein | Spiel neu starten und Renderer auf OpenGL stellen                    |
| Einzelne Ruckler                            | Strg+F7 öffnen und eine Spike-Aufnahme starten                       |
| Eine Optimierung ist gesperrt               | Komet › Kompatibilität öffnen und Details kopieren                   |
| Shader fehlen oder sind schwarz             | Shaderübersicht öffnen und den betroffenen Pass im Client-Log suchen |

## Einen Fehler melden

Nenne Spiel- und Komet-Version, Renderer, Modliste und Schritte zum Nachstellen. Bei Grafikfehlern hilft
`client-main.log`. Bei Rucklern füge die Debugaufnahme hinzu. Ändere beim Vergleich jeweils eine Einstellung.

[HUD und Messungen](docs-hud)

[Kompatibilitätsleitfaden](docs-compatibility)

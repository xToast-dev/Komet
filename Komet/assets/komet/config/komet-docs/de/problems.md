# Probleme eingrenzen

## Was passiert bei dir?

| Problem | Probier zuerst |
|---|---|
| Welt unsichtbar oder Bild schwarz | Renderer auf [[OpenGL]] stellen und der Welt neu beitreten |
| Einzelne Ruckler | Mit {key:komet-hud-window} eine [[Spike-Aufnahme]] machen |
| Eine Einstellung ist gesperrt | Mit [[Entwicklermodus]] **Entwickler › Kompatibilität** öffnen |
| Shader fehlen oder sind schwarz | Im [[Log]] nach dem Namen des Shaders suchen |

## Einen Fehler melden

Schick uns:

- Spielversion und Komet-Version
- deinen [[Renderer]] und deine Modliste
- was du getan hast, bis der Fehler kam
- `client-main.log`, bei Rucklern auch die Spike-Aufnahme

Ändere beim Testen immer nur eine Einstellung auf einmal.

[HUD und Messungen](docs-hud)

[Kompatibilitätsleitfaden](docs-compatibility)

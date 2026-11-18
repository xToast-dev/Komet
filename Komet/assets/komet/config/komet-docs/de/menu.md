# Menü und Einstellungen

## Bedienen

`.komet` öffnet die Übersicht. Links stehen die Seiten. In der Mitte bearbeitest du Optionen oder liest einen Artikel.
Rechts erscheinen Details oder das anklickbare Inhaltsverzeichnis.

- Suche nach Einstellungen, Hilfethemen und API-Namen.
- Enter übernimmt vorgemerkte Werte. R setzt die Seite auf Standardwerte. Esc schließt das Menü.
- Schaltflächen führen ihre beschriftete Aktion direkt aus.

## Renderdiagnose

Aktiviere den Entwicklermodus und öffne in Komets Einstellungen (`.komet`) **Render · Live (Dev)**. Schalte Terrain-Passes und registrierte
Komet-Passes einzeln. Drahtgitter, Punkte, Rückseiten und Tiefentest betreffen Terrain. Suche oder filtere nach Mod,
Stage und Fehlern. Kopieren exportiert den ganzen Renderbericht.

Speichere einen Zustand als A und schalte zum Vergleich gegen B um. Overrides gelten für die Sitzung;
Terrain-Overrides pausieren GPU-Occlusion-Culling.

## Verbundene Mods

**Übersicht › Verbundene Mods** listet die Mods, die über KAPI, Komets Schnittstelle für andere Mods, mit Komet reden.
Jede hat einen Schalter: Aus stoppt die Render-Schritte dieser Mod in Komets Frame, bis das Spiel neu startet; ihre
anderen Einstellungen bleiben. Ihre Karte zeigt, was die Mod angemeldet hat und was jeder Render-Schritt pro Frame
kostet, oder den Fehler, der ihn abgeschaltet hat. Mods, die Komet nicht verbinden konnte, etwa weil sie einen neueren
Komet brauchen, stehen darunter mit dem Grund; ihre Schaltfläche kopiert ihn.

## Dateien

`ModConfig/komet.json` speichert Einstellungen. Unter `ModConfig/komet/` liegen lokale Styles, Shader und Vorlagen.

[HUD und Messungen](docs-hud)

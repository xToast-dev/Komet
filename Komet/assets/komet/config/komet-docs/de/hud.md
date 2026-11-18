# HUD und Messungen

## Tasten

| Taste   | Zweck                                                                    |
|---------|--------------------------------------------------------------------------|
| F7      | HUD ein- oder ausblenden                                                 |
| Strg+F7 | Andockbares HUD-Fenster: Messwerte, Protokoll, Aufnahmen, alle Werkzeuge |
| Strg+F8 | Shader Studio (braucht das Komet SDK; ohne es ein Hinweis, wo es liegt)  |

## Einen Ruckler messen

Geh zur betroffenen Stelle, öffne das HUD-Fenster (Strg+F7) und nimm ein Protokoll auf oder warte mit der
Spike-Aufnahme auf den nächsten Hänger. Kopiere danach den Bericht.

Achte auf Framezeit, GC-Pause, JIT-Zeit und die teuersten Frameabschnitte: eine hohe FPS-Zahl kann einzelne langsame
Frames verdecken. Vergleiche bei gleicher Welt, Sichtweite und Modliste.

[Probleme eingrenzen](docs-problems)

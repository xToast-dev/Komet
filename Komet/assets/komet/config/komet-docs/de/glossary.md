# Begriffe

## Spielen

- **Begriff**: Ein blaues, unterstrichenes Wort. Klick darauf und Komet erklärt es dir.
- **Voreinstellung**: Eine fertige Auswahl aller Komet-Einstellungen: Qualität, Ausgewogen oder Tempo.
- **HUD**: Die Anzeige über dem Spiel mit Framezeit, FPS und Speicher.
- **Framezeit**: Wie lange ein Bild braucht, in Millisekunden. Spitzen spürst du als Ruckler.
- **Spike-Aufnahme**: Komet wartet auf den nächsten Ruckler und hält fest, was in diesem Moment Zeit gekostet hat.
- **Log**: Die Datei `client-main.log` im Ordner `Logs`. Dort schreibt das Spiel auf, was passiert ist.
- **Entwicklermodus**: Eine Spieleinstellung unter Einstellungen › Oberfläche. Sie schaltet zusätzliche Werkzeuge frei.

## Grafik

- **Renderer**: Der Teil, der das Bild zeichnet.
- **OpenGL**: Der Renderer des Spiels. Er läuft überall.
- **Vulkan**: Komets eigener Renderer. Oft schneller, aber noch experimentell.
- **Sicherheitsmodus**: Komet bleibt auf OpenGL, weil Vulkan zuletzt gescheitert ist oder zu wenig Speicher frei ist.
- **Shader**: Ein kleines Programm auf der Grafikkarte, das Farben und Licht berechnet.
- **Shader Studio**: Komets Editor für Shader-Autoren.
- **Pass**: Ein Zeichenschritt, zum Beispiel das Terrain oder ein Effekt über das ganze Bild.
- **Framebuffer**: Ein Bild im Speicher, in das ein Pass zeichnet.
- **Tessellierung**: Aus Blöcken und Modellen werden Dreiecke, die die Grafikkarte zeichnen kann.

## Technik

- **GC**: Die .NET-Laufzeit räumt Speicher auf. Manchmal hält sie dafür das Spiel kurz an.
- **JIT**: Die .NET-Laufzeit übersetzt Code beim ersten Aufruf. Das kann einmalig ruckeln.
- **KAPI**: Komets Schnittstelle, über die andere Mods Einstellungen, Shader und Zeichenschritte anmelden.
- **Define**: Ein fester Wert, der beim Übersetzen eines Shaders eingesetzt wird.
- **Uniform**: Ein Wert, den das Spiel einem laufenden Shader übergibt.
- **SPIR-V**: Das Format, in dem Vulkan Shader liest.

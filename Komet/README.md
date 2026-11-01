# Komet (die Mod)

Das Projekt, das das Spiel lädt: `Komet.dll` mit `modinfo.json`, `modicon.png` und `assets/`. Jede Klasse
`: ModSystem` gehört hierher; die anderen DLLs im Zip sind Bibliotheken.

- `KometModSystem.cs`: der Einstieg auf dem Client; registriert die Module, installiert die Funktionen, räumt auf.
- `Modules.cs`: die eine Stelle, die alle Module nennt (`All`, `InstallOrder`, `Wire`).
- `Host/`: der Serverteil (eigener Prozess für den eingebauten Server).
- `MapTracking.cs`: die Spielerpfeile auf der Karte.

# Komet (the mod)

> Machine-translated from German; wording may be rough in places.

The project the game loads: `Komet.dll` with `modinfo.json`, `modicon.png` and `assets/`. Every class
`: ModSystem` belongs here; the other DLLs in the zip are libraries.

- `KometModSystem.cs`: the client entry point; registers the modules, installs the features, cleans up.
- `Modules.cs`: the one place that names all modules (`All`, `InstallOrder`, `Wire`).
- `Host/`: the server part (own process for the built-in server).
- `MapTracking.cs`: the player arrows on the map.

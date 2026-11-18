# Komet (the mod)


The project the game loads: `Komet.dll` with `modinfo.json`, `modicon.png` and `assets/`. Every class
`: ModSystem` belongs here; the other DLLs in the zip are libraries.

- `KometModSystem.cs`: the client entry point; a thin lifecycle that forwards to `ClientRuntime`.
- `ClientRuntime.cs`: one world's run: composition and shader-mod hold-back, feature install, mod adapters, poll, teardown.
- `Modules.cs`: the composition root, the one place that names all modules (`All`) and registers the services the
  lower modules ask for (`Bind`); the install order comes from the features' own `Order`/`Requires`/`After`/`Before`.
- `Bridges.cs`: the implementations of those service interfaces, forwarding to the higher modules' statics.
- `Host/`: the server part (own process for the built-in server).
- `MapTracking.cs`: the player arrows on the map.

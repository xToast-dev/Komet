# KapiDebug · KapiTiming

## Members and usage

Adds timings of your own to the debug protocol.

```csharp
using (komet.Debug.Measure("pathfinding")) FindPath();
```

- `komet.Debug.Measure(what)` works on any thread and books the time as `modid:what`. It returns a `KapiTiming`; its
  `Dispose()` (or `using`) records the time. Outside a capture it costs only a field read and allocates nothing.
- You start a capture with `.komet debug` or in the protocol tab of the HUD window ({key:komet-hud-window}). The
  protocol lists each name with total time, number of calls and time per call, the most expensive first.
- `KapiDebug.Capturing` tells whether a capture is running. Check it before work that is worth doing only for the
  protocol.
- `modid:what` may have at most 128 characters (`KapiDebug.MaxName`); Komet does not measure longer ones. A capture
  keeps up to 1024 names.

[API index](docs-api)

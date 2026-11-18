# Komet.Core


The foundation: the public API for other mods, KAPI 1.0 (`Kapi/`, namespace `Komet.Api`, entry
`Kapi.Connect(this, "1.0")`; mod authors reference only `Komet.Core.dll`), the internal registries behind it
(`Registry/`: shaders, option pages, styles, debug timings, mod events; namespace `Komet.Core`), the feature registry
(`Features/`, with `FeatureHub` behind KAPI's features), the contracts (`Contracts.cs`) and small helpers for all
modules. Uses no other Komet module.

Features: `CoreFeatures.cs`.

The registry stays one type, with its responsibilities in three files:

| File                            | Responsibility                                             |
|---------------------------------|------------------------------------------------------------|
| `Features/Features.cs`          | Built-in registration, install order and world lifecycle   |
| `Features/Features.State.cs`    | State evaluation, holds and status reports                 |
| `Features/Features.External.cs` | Other mods' registration, patches, cleanup and option rows |

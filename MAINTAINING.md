# Maintaining Komet

> Machine-translated from German; wording may be rough in places.

How Komet is built and how you change it. Code, comments and these pages are English. Player docs live only in the game
(`komet-docs`), docs for mod authors only on the developer site (`docs/site`).

## The layout in one picture

Komet is one mod made of ten projects (the mod project and nine modules), each its own DLL in the same mod zip. A project may only use projects **below**
it; a reference upwards does not build.

```text
Komet            the mod: entry (KometModSystem, ClientRuntime), module list and services (Modules.cs, Bridges.cs), modinfo.json, assets/
  └─ Komet.Ui          HUD and options menu (Hud/, Options/)
       └─ Komet.Vulkan     the Vulkan renderer
            ├─ Komet.Shapes     shapes, animations, inventory
            ├─ Komet.Chunks     chunk arrival, budgets
            │    └─ Komet.Tessellation   chunk building and its scheduling
            └─ Komet.Rendering  OpenGL path: culling, pools, shaders, shadows
                 └─ Komet.Diagnostics   measuring: frame clock, costs, spikes, mod timings
                      └─ Komet.Core     public API, feature registry, contracts, helpers
  Komet.World     world: light, map, events (needs only Komet.Core)
```

Bottom to top: Core, Diagnostics, Rendering, Tessellation, Chunks, Shapes, World, Vulkan, Ui, Sdk, Komet. Who may use whom is
stated in the `ProjectReference` lines of each `<Module>.csproj` and in `tests/Komet.Test/Core/ModulesTests.cs`.

`Komet.Sdk` is the Shader Studio for shader authors: the top module, in Komet's zip, started by `KometModSystem`
(`KometSdk`); it joins the lower modules through `StudioHooks` and `SdkLink`. Its assets keep the domain `kometsdk`.

Alongside: `Komet.Bench` (the benchmark, a mod of its own, never in a release), `tests/`, `tools/Komet.Rules` (the
analyzer, runs on every build).

## Where things are

| You are looking for | There |
|---|---|
| Which features exist, in which order they start | the modules' `*Features.cs`: each entry's `Order`, `Requires`, `After`, `Before` (`Komet.Core/Features/FeatureOrder.cs` sorts them) |
| A feature and its switch | the module's `*Features.cs` (the entry `new("Name")`) and the class `Name.cs` in the module |
| The public API for other mods (KAPI) | `Komet.Core/Api/` (namespace `Komet.Api`, DLL `Komet.Core.dll`); it forwards to the internal registries in `Komet.Core/Registry/` and `Komet.Core/Features/` |
| The contracts `Assert`, `NotNull`, `Finite`, `Index`, `Bounded` | `Komet.Core/Contracts.cs` |
| Texts in the menu and HUD | `Komet/assets/komet/lang/de.json` and `en.json` (keys `settings-…`, `hud-…`) |
| The in-game help pages (players) | `Komet/assets/komet/config/komet-docs/<de|en>/*.md` |
| The Modding pages (mod authors) | `Komet.Sdk/assets/kometsdk/config/komet-docs/<de|en>/*.md` |
| The developer site (API reference, articles) | `docs/site`, built with `scripts/apidocs.sh` |
| Templates for mod authors | `Komet.Sdk/assets/kometsdk/config/komet-templates/` |
| What a module needs from a higher one | the interfaces in `<Module>/Links.cs`, answered in `Komet/Bridges.cs`, registered in `Komet/Modules.cs` (`Bind`) |
| Build settings shared by all modules | `Komet.Module.props` |

## Build and test

```bash
./build.sh
```

Builds everything in Release, checks the rules and puts the mod under `Releases/komet/` and as
`Releases/komet_<version>.zip`. Every warning is an error.

```bash
scripts/test.sh
```

The quick tests; `scripts/test.sh --all` runs all of them. Before a release run both, plus the Debug build once
(`dotnet build Komet/Komet.csproj -c Debug`), which checks a few more rules.

```bash
scripts/bench.sh --build --profile quick
```

Starts the game in a sandbox with the fresh build and flies one round: the only check that the game really loads the
mod. Never while your own game is running.

```bash
scripts/seed-pipelines.sh
```

Renews the pipeline recipes Komet ships (`Komet/assets/komet/config/pipelines.json`). They are tied to the shader
sources: run it once after changing a shader patch, otherwise Vulkan builds every pipeline only when it is first drawn
on entering a world (stutter).

## Changing a feature

1. Find it: its name in the menu is the lang key `settings-<name>` in `en.json`; the switch is named in code like the
   key without `settings-`. Search for `new("Name")` in the `*Features.cs` files.
2. The entry in `*Features.cs` says what happens at start (`Install`), which switch belongs to it (`Knobs`), what happens
   on stop (`Stop`) and how the feature reports its state (`Probe`).
3. The work itself is in the class of the same name in the same module.
4. Build (`./build.sh`). The analyzer reports missing texts (`KR0010`: key missing in a language).

## Adding a feature

1. Create the class in the fitting module (see the picture above: where would it belong? what does it need?).
2. Add it to the module's `*Features.cs`.
3. Give it an `Order` no other feature has: its place at install and in `komet.json` and the menu. Name what it must
   come after in `Requires` (must exist) or `After`. A duplicate `Order`, an unknown name or a cycle stops Komet at
   start, and the log says which.
4. Add the texts: `settings-<name>` and `settings-<name>-hint` in `de.json` **and** `en.json`.
5. Build and write a short test under `tests/Komet.Test/<Module>/`.

## Removing a feature

1. Delete the entry in `*Features.cs`, the class and the lang keys. Another feature that names it in `Requires`,
   `After` or `Before` loses that name too (`FeatureOrder` refuses an unknown id at start).
2. Build. The compiler names every place that still uses the feature (usually the HUD in `Komet.Ui`).
3. A player's old `komet.json` with the switch does no harm: unknown keys are ignored.

## Removing a whole module

1. Delete the project folder and its line in `Komet.slnx`.
2. Remove its `ProjectReference` from `Komet/Komet.csproj` and from the modules that used it.
3. Remove its list from `Modules.cs` (`All`), its registrations from `Bind` and its bridge from `Bridges.cs`.
4. Build. The compiler names every place that still uses the module. Whatever a lower module asked of it is an
   interface in that module's `Links.cs`; its null object keeps the "without the module" answer.

## Services instead of upward references (`Links.cs`)

If a low module needs something from a higher one (`Komet.Rendering` asks, for example, whether the Vulkan renderer is
drawing), it declares an **interface** in its `Links.cs`, with a null object for the answer when the upper module is
missing. `Komet/Bridges.cs` implements the interface by forwarding to the upper module, and `Modules.Bind` registers
it in `Services` before anything starts; the world's end clears the registrations.

New upward dependency? Never a `using` on the upper module, instead:

1. Add the question to an interface in the low module's `Links.cs` (or a new interface with its null object and a
   property on `Links`).
2. Answer it in the bridge in `Komet/Bridges.cs`; a new interface also gets its `Services.Register` line in `Bind`.

## The rules (Komet.Rules)

The analyzer `tools/Komet.Rules` checks rules after the "Power of Ten" pattern on every build. The most important:

| Rule | Means | What you do |
|---|---|---|
| KR0001 | no `while` loops | `for` with a fixed upper bound, for example `Math.Min(list.Count, MaxItems)` |
| KR0004 | `for` needs a constant bound | bound as `const` or `Math.Min(…, constant)` |
| KR0005 | `foreach` only over `Bounded(…)` | `foreach (var x in list.Bounded(MaxItems))` |
| KR0008 | a method at most 60 lines | split it |
| KR0010/11 | lang key missing or only in one language | add it to `de.json` and `en.json` |

Contracts (`Assert`, `NotNull`, `Index`, `Finite`, `Bounded`) belong where a wrong value would be a bug, not
everywhere.

## Typical compiler messages

| Message | Means | Fix |
|---|---|---|
| `CS0234: The type or namespace name 'X' does not exist in the namespace 'Komet'` | A module reaches upwards | Service in `Links.cs` (see above) or move the class into the lower module |
| `CS0122: '…' is inaccessible due to its protection level` | An `internal` from another module | All Komet projects see each other (`InternalsVisibleTo` in `Komet.Module.props`); if your project is missing there, add it |
| `IDE0005: Using directive is unnecessary` | A `using` without use | Delete the line |
| `Found multiple .dll files with ModSystems` (in the game log) | A module contains a class `: ModSystem` | It belongs in the project `Komet` (`ModulesTests` checks this) |

## For mod authors

Other mods reference **`Komet.Core.dll`**, not `Komet.dll`: that is where the public API, KAPI (`Komet.Api`), lives.
Mods connect with `Kapi.Connect(this, "1.0")`. Whatever is `public` in `Komet.Core/Api/` is part of KAPI; its XML
comments are the source of truth and become the API reference on `docs/site`. `Komet.Core/Registry/` is internal.

KAPI is versioned `major.minor` (`Kapi.Major`, `Minor` in `Kapi.cs`). Adding a type, member or overload raises
the minor version; changing or removing anything public raises the major version and needs a migration table in
`CHANGELOG.md`. Update the docs on every change: the Modding pages (`api*.md`, `integration.md`, both languages), the
templates' C# code and `docs/site/articles`.

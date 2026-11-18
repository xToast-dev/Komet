# KapiFeatures · KapiFeature

## Members and usage

A feature is a part of Komet the player can switch, for example `ChunkBudget`. Komet's features are named as
`KapiFeatures.Snapshot()` lists them, other mods' as `modid:name`.

- `KapiFeatures.Snapshot()` lists every feature in install order, Komet's first, as `KapiFeatureInfo` with `Id`, `Title`, `Owner`,
  `State`, `CanHoldOff`, `HeldBy` and `Reason`. The list is a snapshot and does not change afterwards.
- `KapiFeatures.StateOf(id)` returns a `KapiFeatureState`. Instead of the id, the name of one of its switches or sliders works too
  (`UploadCap` finds `ChunkBudget`).
- `komet.Features.StateChanged` reports every change with `Id`, `Previous` and `State`. `Snapshot` and
  `StateOf` are static: they need no connection.

| KapiFeatureState | Meaning                                                                                          |
|-----------------|--------------------------------------------------------------------------------------------------|
| `Unknown`       | No feature has this id                                                                           |
| `Pending`       | Not installed yet                                                                                |
| `Active`        | Installed and at work                                                                            |
| `Off`           | The player switched it off                                                                       |
| `HeldOff`       | A mod holds it at the game's behaviour                                                           |
| `StoodDown`     | Another mod patches what it relies on; it keeps out of the way                                   |
| `EngineChanged` | The game's code is not the code it was written for: not installed                                |
| `NotApplicable` | Server code, but the server does not run in your game; or the hardware or driver lacks something |
| `Failed`        | It threw an exception, or its own guard gave up                                                  |

### Holding a feature off

If your mod does not get along with a feature, hold it at the game's behaviour instead of changing the player's setting.
While you hold it, its switch in the menu is locked and shows your mod and your reason.

```csharp
KapiHold hold = komet.Features.HoldOff("ChunkBudget", "own upload control");
if (!hold.IsHolding) { /* refused: the log says why */ }
hold.Dispose(); // the player's value applies again
```

- Komet refuses if the feature does not exist, if it has no switch or slider (`KapiFeatureInfo.CanHoldOff`) or if there are
  already too many holds. Then `IsHolding` is false.
- A hold also works before Komet has started. `Dispose()` acts only once; later calls do nothing. `komet.Dispose()`
  and the end of the world release every hold of your connection.
- If several mods hold the same feature, `HeldBy` and `Reason` name the first.

### Registering your own

Komet installs your feature after its own, shows its switch or slider, lets other mods hold it off and stands it down
while another mod patches the watched methods.

```csharp
komet.Features.Register(new KapiFeature("water", Lang.Get("mymod:water"))
{
    Page = KapiFeatures.RenderPage,
    Group = Lang.Get("mymod:title"),
    Hint = Lang.Get("mymod:water-hint"),
    Knob = KapiKnob.Switch(() => config.Water, on => config.Water = on,
        on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Uninstall = () => WaterPatch.Enabled = false,
    Shaped = () => [WaterPatch.Target],
    Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

- The constructor takes name and title; the mod id comes from the connection. The feature's id and its Harmony id are
  `modid:name`. The title shows in
  the row exactly as you pass it: translate it yourself.
- `Page`: `RenderPage` (Komet › Rendering), `ChunksPage` (Komet › Chunks), `MiscPage` (Komet › Other), the id of one of
  your pages (`KapiPage.Id`), or `null` for no row.
- `Group`: the heading over the row. Without it, Komet's pages put your mod id above it; on your own page the row goes
  at the end.
- `Knob`: `KapiKnob.Switch(get, set, apply)` or `KapiKnob.Range(min, max, engine, get, set, apply, unit)`. `get`
  and `set` carry the player's value, which your mod saves. `apply` gets what your patches should read: normally the
  player's value; while the feature is held off or stood down, the game's value (`engine`, off for a switch). Without a
  `Knob`, the feature is always on while installed.
- `Install` gets a Harmony instance under the feature's id and a logger. `Uninstall` undoes whatever is not a patch.
  Komet removes the patches itself: when the world closes or when your code throws an exception (`Failed`).
- `Shaped` lists the game's methods you replace; `Fingerprint` is the checksum of the code you wrote the feature for. If
  the code differs, the feature stays out (`EngineChanged`). `KapiFeatures.FingerprintOf(methods)` gives the value; pin
  it in a test. 0 turns the check off.
- `Watched`: methods no other mod should patch. `StandDownFor` says which patch kinds count (default
  `KapiPatchKinds.Replacing`). While the feature stands down, your patches stay in, but `apply` gets the game's value until
  Komet finds the methods free again. That is why `Watched` needs a `Knob`.
- `ServerOnly`: the feature patches server code. It needs the built-in server (singleplayer); otherwise it is
  `NotApplicable`.
- Your feature lasts until the world closes, also after `komet.Dispose()`: Harmony patches cannot all be taken back at
  once.

`Register` returns false and logs why when it refuses:

- The name is not letters, digits, `_` and `-`, the mod id is not lower-case letters and digits, or the title is empty.
- The connection is gone, or the world has already loaded. Register your feature in `StartClientSide`.
- The id is taken, or there are already 32 features from other mods.
- A slider's range is empty or does not hold its game value.
- `Fingerprint` without `Shaped`, `Watched` without `Knob`, or `Watched` with `StandDownFor` set to `None`.

### Patch kinds

`KapiPatchKinds` are flags; you can combine them with `|`.

| KapiPatchKinds                | Meaning                                                                |
|-------------------------------|------------------------------------------------------------------------|
| `Prefix`                      | Runs before the original and can skip it                               |
| `Postfix`                     | Runs after the original and can change the result                      |
| `Transpiler`                  | Rewrites the original's IL code                                        |
| `Finalizer`                   | Runs after the original and sees its exception                         |
| `InnerPrefix`, `InnerPostfix` | On a call inside the original                                          |
| `Body`                        | `Transpiler`, `InnerPrefix` and `InnerPostfix`                         |
| `Replacing`                   | `Prefix` or `Body`: everything that can replace the original (default) |
| `All`                         | Every kind                                                             |
| `None`                        | No kind                                                                |

[API index](docs-api)

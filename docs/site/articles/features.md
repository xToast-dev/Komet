# Features

Everything Komet changes in the game is a **feature** with a name (the README lists them), a state
(<xref:Komet.Api.FeatureState>) and usually a switch. Mods can read the states, hold a feature at the game's own
behaviour, and register features of their own.

## Reading states

```csharp
if (KometFeatures.StateOf("FrustumSweep") == FeatureState.Active) { ... }
KometFeatures.StateChanged += (_, e) => Log($"{e.Id}: {e.Previous} -> {e.State}");
foreach (var f in KometFeatures.Snapshot()) { ... }
```

## Holding a feature off

When a Komet feature breaks something in your mod, hold it instead of asking players to turn it off:

```csharp
hold = KometFeatures.HoldOff("FrustumSweep", "mymod", "Our shadow pass needs the game's culling");
...
hold.Dispose();   // or keep it until the world closes
```

The player sees the hold and its reason in Komet's options. Holds work before Komet started too. A file-only mod
does the same with `holds` in `komet.json`.

## Registering a feature of your own

A <xref:Komet.Api.FeatureDefinition> gives Komet what it needs to treat your patches like its own: a row in the
options, install order, holds, and standing down when another mod patches the same methods.

```csharp
KometFeatures.Register(new FeatureDefinition("mymod", "FastLeaves", "Fast leaves")
{
    Hint = "Draws leaves with fewer faces.",
    Page = KometFeatures.RenderPage,
    Knob = FeatureKnob.Switch(() => Config.FastLeaves, v => Config.FastLeaves = v, v => LeafPatches.On = v),
    Install = (harmony, log) => harmony.PatchAll(typeof(LeafPatches)),
    Watched = () => [AccessTools.Method(typeof(ChunkTesselator), "Tesselate")],
    Shaped = () => [AccessTools.Method(typeof(ChunkTesselator), "Tesselate")],
    Fingerprint = 0x1234_5678_9ABC_DEF0
});
```

| Member | Purpose |
|---|---|
| `Knob` | the switch or slider; `apply` gets what patches should read (the engine value while held or stood down) |
| `Install` / `Uninstall` | patch with the given Harmony (id `modid:name`); Komet unpatches at world close or when the code throws |
| `Watched` + `StandDownFor` | another mod's patch of these kinds on these methods stands the feature down |
| `Shaped` + `Fingerprint` | pin the engine bodies; a game update that changes them keeps the feature out (`EngineChanged`) |
| `ServerOnly` | patches server code; not applicable on a remote server |

Pin the fingerprint in your own test with <xref:Komet.Api.KometFeatures.Fingerprint*>, so a game update fails
there first.

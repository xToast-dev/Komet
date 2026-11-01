# API for mods

Komet has a small public API in the namespace `Komet.Api`. With it, your mod can:

- add pages of its own to Komet's options menu (`KometOptions`),
- ask about Komet's features, hold one off or register its own (`KometFeatures`),
- change the game's shaders and draw picture effects of its own (`KometShaders`),
- register menu styles from code (`KometStyles`),
- add timings of its own to the debug protocol (`KometDebug`).

Words like define, uniform or pass are explained on the page "Glossary" under Help.

## Optional Komet support

Your mod should still load without Komet. Three rules make sure of that:

1. Reference `Komet.Core.dll` (it holds Komet's public API; `Komet.dll` only starts the mod) without shipping it: in the `.csproj` with `Private="false"`.
2. Keep every Komet call in a class of its own. Only that class names Komet's types.
3. Call that class only when `api.ModLoader.IsModEnabled("komet")` is true.

```xml
<Reference Include="Komet.Core" HintPath="$(Komet)" Private="false" />
```

```csharp
public override void StartClientSide(ICoreClientAPI api)
{
    if (api.ModLoader.IsModEnabled("komet")) KometBridge.Hook(api, config);
}

// in its own file: the only class that names Komet's types
internal static class KometBridge
{
    public static void Hook(ICoreClientAPI api, MyConfig config)
    {
        // KometOptions.Page(...), KometFeatures.Register(...), ...
    }
}
```

The bridge (`KometBridge`) gets what it needs, here `api` and your settings. `$(Komet)` is the path to `Komet.Core.dll`, set when building with `-p:Komet=<path>`. The Vulkan template (Modding › Templates › **Create Vulkan template**) shows the pattern in a whole project, in its `code/` folder.

## Rules

- Call the API from the main thread, ideally in `StartClientSide`. Only `KometDebug.Measure` may be called from any thread.
- Everything ends when the world closes: pages, features, holds, shader registrations, styles from code and event subscriptions. In the next world, register everything again; `StartClientSide` runs every time you join a world anyway.
- If an event handler throws an exception, Komet logs it and unsubscribes the handler.

## KometOptions

`KometOptions.Page(id, title, section)` creates an empty page and returns it. You add the rows in order. Every method returns the page, so you can chain the calls.

- `id`: unique across all mods. Reusing an id starts the page over, so its rows do not double when you join a world again.
- `title`: the page title, already translated.
- `section`: the section the page goes under (a tab at the top of the menu). Without it, the page gets a section of its own, named after its title.
- To put your page into Komet's Shaders section in every language, pass `Lang.Get("komet:settings-section-shaders")` as `section`.

```csharp
KometOptions.Page("mymod", Lang.Get("mymod:title"))
    .Group(Lang.Get("mymod:rendering"))
    .Switch(Lang.Get("mymod:water"), () => config.Water,
        on => config.Water = on, Lang.Get("mymod:water-hint")).Default(1)
    .Slider(Lang.Get("mymod:detail"), 1, 8, 1, () => config.Detail,
        v => config.Detail = (int)v, hint: Lang.Get("mymod:detail-hint")).Default(4)
    .Format(v => v >= 8 ? "max" : v.ToString())
    .EnabledWhen(() => config.Water)
    .Choice(Lang.Get("mymod:mode"), ["A", "B", "C"], () => config.Mode,
        i => config.Mode = i)
    .Button(Lang.Get("mymod:reset"), Lang.Get("mymod:do-reset"), config.Reset);

KometOptions.Applied += (_, _) => config.Save();
```

| Member | What it does |
|---|---|
| `Group(title)` | A heading over the rows that follow |
| `Switch(label, get, set, hint)` | On or off: `Func<bool>`, `Action<bool>` |
| `Slider(label, min, max, step, get, set, unit, hint)` | A number: `Func<double>`, `Action<double>`; `max` greater than `min`, `step` greater than 0 |
| `Choice(label, names, get, set, hint)` | One of 1 to 40 names; the value is the index (`Func<int>`, `Action<int>`) |
| `Button(label, text, click, hint)` | A row with a button; acts at once, without **Apply** |
| `Default(value)` | The last row's default value; for a switch 0 or 1, for a choice the index |
| `Format(format)` | How the last row shows its value |
| `EnabledWhen(enabled)` | The last row takes input only while `enabled` returns true; otherwise it is dimmed |

`hint` and `unit` are optional. Pass them by name, for example `unit: " ms"`. Komet puts `unit` right after the number, unless the row has a `Format`: then it shows only that text.

### Values and saving

- Getters, setters, saving and translation are up to your mod.
- Komet keeps changes to switches, sliders and choices until the player applies them (**Enter** or **Apply** in the footer). Then it calls your setters.
- Afterwards `KometOptions.Applied` fires if anything changed: the right moment to save.
- The R key (**Default**) resets the whole page to the values from `Default`. A right-click on a slider's track resets just that slider. A slider marks its default value on its track.
- Limits: `KometOptions.MaxPages` (32) pages for all mods together, Komet's own shader pages included; `OptionPage.MaxOptions` (40) rows per page. Komet refuses more.

## KometFeatures

A feature is a part of Komet the player can switch, for example `ChunkBudget`. Komet's features are named as in the README, other mods' as `modid:name`.

- `Snapshot()` lists every feature in install order, Komet's first, as `FeatureInfo` with `Id`, `Title`, `Owner`, `State`, `CanHoldOff`, `HeldBy` and `Reason`. The list is a snapshot and does not change afterwards.
- `StateOf(id)` returns a `FeatureState`. Instead of the id, the name of one of its switches or sliders works too (`UploadCap` finds `ChunkBudget`).
- `StateChanged` reports every change with `Id`, `Previous` and `State`.

| FeatureState | Meaning |
|---|---|
| `Unknown` | No feature has this id |
| `Pending` | Not installed yet |
| `Active` | Installed and at work |
| `Off` | The player switched it off |
| `HeldOff` | A mod holds it at the game's behaviour |
| `StoodDown` | Another mod patches what it relies on; it keeps out of the way |
| `EngineChanged` | The game's code is not the code it was written for: not installed |
| `NotApplicable` | Server code, but the server does not run in your game; or the hardware or driver lacks something |
| `Failed` | It threw an exception, or its own guard gave up |

### Holding a feature off

If your mod does not get along with a feature, hold it at the game's behaviour instead of changing the player's setting. While you hold it, its switch in the menu is locked and shows your mod and your reason.

```csharp
FeatureHold hold = KometFeatures.HoldOff("ChunkBudget", "mymod", "own upload control");
if (!hold.IsHolding) { /* refused: the log says why */ }
hold.Release(); // or Dispose(): the player's value applies again
```

- Komet refuses if the feature does not exist, if it has no switch or slider (`FeatureInfo.CanHoldOff`) or if there are already too many holds. Then `IsHolding` is false.
- A hold also works before Komet has started. `Release()` and `Dispose()` act only once; later calls do nothing.
- If several mods hold the same feature, `HeldBy` and `Reason` name the first.

### Registering your own

Komet installs your feature after its own, shows its switch or slider, lets other mods hold it off and stands it down while another mod patches the watched methods.

```csharp
KometFeatures.Register(new FeatureDefinition("mymod", "water", Lang.Get("mymod:water"))
{
    Page = KometFeatures.RenderPage,
    Group = Lang.Get("mymod:title"),
    Hint = Lang.Get("mymod:water-hint"),
    Knob = FeatureKnob.Switch(() => config.Water, on => config.Water = on,
        on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Uninstall = () => WaterPatch.Enabled = false,
    Shaped = () => [WaterPatch.Target],
    Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

- The constructor takes mod id, name and title. The feature's id and its Harmony id are `modid:name`. The title shows in the row exactly as you pass it: translate it yourself.
- `Page`: `RenderPage` (Komet › Rendering), `ChunksPage` (Komet › Chunks), `MiscPage` (Komet › Other), the id of one of your `KometOptions` pages, or `null` for no row.
- `Group`: the heading over the row. Without it, Komet's pages put your mod id above it; on your own page the row goes at the end.
- `Knob`: `FeatureKnob.Switch(get, set, apply)` or `FeatureKnob.Range(min, max, engine, get, set, apply, unit)`. `get` and `set` carry the player's value, which your mod saves. `apply` gets what your patches should read: normally the player's value; while the feature is held off or stood down, the game's value (`engine`, off for a switch). Without a `Knob`, the feature is always on while installed.
- `Install` gets a Harmony instance under the feature's id and a logger. `Uninstall` undoes whatever is not a patch. Komet removes the patches itself: when the world closes or when your code throws an exception (`Failed`).
- `Shaped` lists the game's methods you replace; `Fingerprint` is the checksum of the code you wrote the feature for. If the code differs, the feature stays out (`EngineChanged`). `KometFeatures.Fingerprint(methods)` gives the value; pin it in a test. 0 turns the check off.
- `Watched`: methods no other mod should patch. `StandDownFor` says which patch kinds count (default `PatchKinds.Replacing`). While the feature stands down, your patches stay in, but `apply` gets the game's value until Komet finds the methods free again. That is why `Watched` needs a `Knob`.
- `ServerOnly`: the feature patches server code. It needs the built-in server (singleplayer); otherwise it is `NotApplicable`.

`Register` returns false and logs why when it refuses:

- The id is not `modid:name` (a mod id of lower-case letters and digits; letters, digits, `_` and `-` in the name), or the title is empty.
- The world has already loaded. Register your feature in `StartClientSide`.
- The id is taken, or there are already 32 features from other mods (`KometFeatures.MaxExternal`).
- A slider's range is empty or does not hold its game value.
- `Fingerprint` without `Shaped`, `Watched` without `Knob`, or `Watched` with `StandDownFor` set to `None`.

### Patch kinds

`PatchKinds` are flags; you can combine them with `|`.

| PatchKinds | Meaning |
|---|---|
| `Prefix` | Runs before the original and can skip it |
| `Postfix` | Runs after the original and can change the result |
| `Transpiler` | Rewrites the original's IL code |
| `Finalizer` | Runs after the original and sees its exception |
| `InnerPrefix`, `InnerPostfix` | On a call inside the original |
| `Body` | `Transpiler`, `InnerPrefix` and `InnerPostfix` |
| `Replacing` | `Prefix` or `Body`: everything that can replace the original (default) |
| `All` | Every kind |
| `None` | No kind |

## KometShaders

Changes the game's shaders and draws fullscreen effects of your own, on OpenGL and on Komet's Vulkan renderer alike.

| Member | What it does |
|---|---|
| `Define(modId, name, value, programs)` | Puts `#define name value` into programs; `value` is read as they load, so call `Reload()` after a change |
| `Uniform(modId, name, value)` | A uniform Komet sets every frame (1 to 4 numbers) |
| `Edit(modId, program, vertex, fragment)` | Rewrites a program's source as it loads |
| `Reload()` | Rebuilds the game's shaders with every define and edit |
| `AddPass(pass)` | A fullscreen pass of your own (`KometPass`) |
| `Renderer`, `RendererChanged` | Which renderer draws now, and when it switches |

```csharp
IDisposable hour = KometShaders.Uniform("mymod", "mymodHour", () => api.World.Calendar.HourOfDay);
hour.Dispose(); // takes it back before the world closes
```

- Every registration returns an `IDisposable`. `Dispose()` takes it back early.
- Every program has `#define KOMET 1`, and `#define KOMET_VULKAN 1` as well under Vulkan.
- For all mods together, Komet takes up to 128 defines, 256 uniforms, 64 edits and 32 passes.

Everything else, with examples, is on the "Shader reference" page; what applies on Vulkan is on "Shaders on Vulkan".

## KometStyles

Registers a menu style from code. Reusing an id replaces the style; afterwards `KometStyles.Changed` fires. You can register up to 32 styles this way (`KometStyles.MaxStyles`).

```csharp
KometStyles.Register(new KometStyle
{
    Id = "mymod-night",
    Name = "Night",
    Base = "pragmatic",
    Colors = { ["accent"] = "#7A5CFF" }
});
```

`KometStyle` has `Id`, `Name`, `Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo`, `Background`, `BackgroundSource`, `Folder` and `Domain`. What they mean and where relative files live is on the "Menu styles" page.

## KometDebug

Adds timings of your own to the debug protocol.

```csharp
using (KometDebug.Measure("mymod:pathfinding")) FindPath();
```

- `Measure(name)` works on any thread. It returns a `KometTiming`; its `Dispose()` (or `using`) records the time. Outside a capture it costs only a field read.
- You start a capture with `.komet debug` or in the debug window (Komet › HUD › **Debug window (Ctrl+F8)**). The protocol lists each name with total time, number of calls and time per call, the most expensive first.
- `KometDebug.Capturing` tells whether a capture is running. Check it before work that is worth doing only for the protocol.
- Use names like `modid:what`, at most 128 characters; Komet does not measure longer ones. A capture keeps up to 1024 names (`KometDebug.MaxTimings`).

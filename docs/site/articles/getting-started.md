# Getting started

## Without code

Put `assets/<modid>/config/komet.json` in your mod. Komet reads it from every enabled mod at world join, lists the
mod under *Shaders › Active shaders* as made for Komet, and builds a page from its settings. See the
[komet.json reference](komet-json.md) and the [Vulkan shader template](vulkan-template.md).

Without Komet the file is just an asset nobody reads: the mod still loads.

## With code: Komet as an optional dependency

1. Reference the player's `Komet.Core.dll` (the public API; `Komet.dll` only starts the mod) and do not ship it:

   ```xml
   <Reference Include="Komet.Core" HintPath="$(Komet)" Private="false" />
   ```

2. Keep every call into Komet in a class of its own. .NET loads `Komet.Core.dll` only when that class runs, so the mod
   still loads where Komet is missing.

3. Call that class from `StartClientSide`, and only when Komet runs:

   ```csharp
   public override void StartClientSide(ICoreClientAPI api)
   {
       if (api.ModLoader.IsModEnabled("komet")) KometBridge.Start(api);
   }
   ```

   ```csharp
   internal static class KometBridge
   {
       public static void Start(ICoreClientAPI api)
       {
           KometShaders.Uniform("mymod", "mymodHour", () => api.World.Calendar.HourOfDay);
           KometOptions.Page("mymod", "My mod")
               .Switch("Warm picture", () => Config.Warm, v => Config.Warm = v);
       }
   }
   ```

Do not add Komet to `dependencies` in `modinfo.json` unless your mod cannot work without it: a dependency makes
the game refuse your mod where Komet is missing.

## Lifetime rules

- Main thread only, unless a member says otherwise.
- Register in `StartClientSide`. Komet drops pages, registrations, holds and event handlers when the world
  closes; register again on the next join.
- `KometShaders` returns `IDisposable` registrations: dispose one to take it away earlier.
- An event handler that throws is logged once and unsubscribed.
- Use your mod's domain as the mod id. Komet then counts your `komet.json` and your code as one mod: the code's
  defines and uniforms reach the file's passes.

## Where to look when something is wrong

| Where | What |
|---|---|
| `Logs/client-main.log`, lines `Komet shaders:` | passes that did not compile, refused registrations |
| `Logs/client-main.log`, lines `Komet: Vulkan cannot draw` | programs the Vulkan port could not take |
| `Logs/komet-shader-<file>.txt` | the full source of a shader stage that failed to compile |
| *Shaders › Active shaders*, the mod's row | binding, renderer, shader count, problems |
| `.komet debug` | a protocol, with your [timings](debug.md) |

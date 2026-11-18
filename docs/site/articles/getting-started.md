# Getting started

## Without code

Put `assets/<modid>/config/komet.json` in your mod. Komet reads it from every enabled mod at world join, lists the
mod under *Shaders › Active shaders* as made for Komet, and builds a page from its settings. See the
[komet.json reference](komet-json.md) and the [Vulkan shader template](vulkan-template.md).

Without Komet the file is just an asset nobody reads: the mod still loads.

## With code: KAPI, with Komet optional

Komet's API for other mods is **KAPI**, namespace `Komet.Api`, version 1.0.

1. Reference the player's `Komet.Core.dll` (KAPI lives there; `Komet.dll` only starts the mod) and do not ship it:

   ```xml
   <Reference Include="Komet.Core" HintPath="$(Komet)" Private="false" />
   ```

2. Name KAPI's types only in a bridge class of their own. .NET loads `Komet.Core.dll` only when that class runs, so the
   mod still loads where Komet is missing.

3. Call the bridge from `StartClientSide`, and only when Komet runs:

   ```csharp
   public override void StartClientSide(ICoreClientAPI api)
   {
       if (api.ModLoader.IsModEnabled("komet")) KometBridge.Start(this, api, () => StartOwn(api));
       else StartOwn(api);
   }
   ```

4. The bridge connects with the KAPI version it was written against and falls back when it gets no connection or
   Komet refuses the effect:

   ```csharp
   using Komet.Api;

   internal static class KometBridge
   {
       public static void Start(ModSystem system, ICoreClientAPI api, Action startOwn)
       {
           var komet = Kapi.Connect(system, "1.0");
           if (komet is null) { startOwn(); return; } // a Komet without KAPI 1.0: the log says why

           komet.Shaders.Uniform("mymodHour", () => api.World.Calendar.HourOfDay);
           komet.Options.Page("My mod").Switch("Warm picture", () => Config.Warm, v => Config.Warm = v);

           var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", Config.GradeSource));
           if (!fx.Active) startOwn(); // fx.Error and the log say why
       }
   }
   ```

To draw in Komet's frame, add a [render step](render-steps.md) with `komet.Rendering.AddStep(...)`. Every connected
mod, with its steps and their cost, shows on Komet's *Connected mods* page; a mod Komet refused shows there with the
reason.

Do not add Komet to `dependencies` in `modinfo.json` unless your mod cannot work without it: a dependency makes
the game refuse your mod where Komet is missing.

## Versioning

KAPI is versioned `major.minor`; this Komet offers 1.0 (<xref:Komet.Api.Kapi.Version>). A mod names the version it
was written against in `Kapi.Connect` and nowhere else.

| Komet offers | Mod asks for | Result |
|---|---|---|
| 1.0 | 1.0 | connected |
| 1.3 | 1.0 | connected: a minor version only adds |
| 1.0 | 1.2 | `null`, logged: this Komet lacks what 1.2 added |
| 2.0 | 1.x | `null`, logged: a major version may change or remove |

- On `null`, start your own path, exactly as without Komet. The log line is
  `Komet: KAPI did not connect <modid>: it was written for KAPI x.y, this Komet offers KAPI 1.0`.
- <xref:Komet.Api.Kapi.Supports*> asks the same question without connecting or logging.
- The promise: within a major version KAPI only gains types, members and overloads, so code for 1.0 keeps compiling
  and running. A new major version may break; its changelog entry carries a migration table.
- Ask for the lowest version that has what you use.

## Lifetime rules

- Main thread only, unless a member says otherwise (<xref:Komet.Api.KapiDebug.Measure*> runs on any thread).
- Connect in `StartClientSide`. A connection lasts one world: Komet takes back pages, passes, render steps, defines,
  uniforms, edits, holds, styles and event handlers when the world closes; connect again on the next join.
- `komet.Dispose()` takes everything back earlier, at once. Features stay until the world closes.
- Every shader registration returns a <xref:Komet.Api.KapiRegistration>: `Active`, `Error`, and `Dispose()` to take
  it away earlier.
- An event handler that throws is logged once and unsubscribed.
- Your mod id comes from your `modinfo.json`. Keep it equal to your asset domain: Komet then counts your `komet.json`
  and your code as one mod, and the code's defines and uniforms reach the file's passes.

## Where to look when something is wrong

| Where | What |
|---|---|
| `Logs/client-main.log`, lines `Komet shaders:` | passes that did not compile, refused registrations |
| `Logs/client-main.log`, lines `Komet: Vulkan cannot draw` | programs the Vulkan port could not take |
| `Logs/komet-shader-<file>.txt` | the full source of a shader stage that failed to compile |
| *Shaders › Active shaders*, the mod's row | binding, renderer, shader count, problems |
| `.komet debug` | a protocol, with your [timings](debug.md) and render steps |
| *Overview › Connected mods* | whether your mod connected or was refused, what it registered, each step's cost or error |

# Optional Komet integration

## Do I need an integration?

Mods using standard game paths can already benefit from the Komet optimizations active there. Add integration code when
your mod needs to plug its own renderer or shader effect into Komet, or wants a page in Komet's options. Start with one
effect.

## Set up the project

Create a `Komet/` folder in your C# project. Compile the bridge into your normal mod DLL. Reference `Komet.Core.dll`
with `Private="false"` and keep it out of your package: the player's Komet brings it. Leave Komet out of required
dependencies in `modinfo.json`.

## Start exactly one path

Replace `config` and `StartNormalRenderer` with your own code. The normal start must return a handle that removes its
renderers and hooks. Only the bridge names KAPI types (`Komet.Api`), and the client system calls it only when Komet is
enabled, so your mod loads without Komet too. The bridge connects with the KAPI version it was written against; your mod
id comes from `modinfo.json`.

```csharp
// Client system. Keep every KAPI type out of this file.
private IDisposable? _own;

public override void StartClientSide(ICoreClientAPI api)
{
    if (config.UseKomet && api.ModLoader.IsModEnabled("komet"))
    {
        try { TryKomet(api); return; }
        catch (Exception e) when (e is System.IO.FileNotFoundException or
            System.IO.FileLoadException or TypeLoadException or MissingMethodException)
        {
            api.Logger.Warning("mymod: optional Komet API unavailable: {0}", e.Message);
        }
    }

    StartOwn(api);
}

[System.Runtime.CompilerServices.MethodImpl(
    System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
private void TryKomet(ICoreClientAPI api) =>
    KometBridge.Start(this, config.FragmentSource, () => StartOwn(api));

private void StartOwn(ICoreClientAPI api) => _own = StartNormalRenderer(api);

public override void Dispose()
{
    _own?.Dispose();
    _own = null;
    base.Dispose();
}
```

```csharp
// Komet/KometBridge.cs
using System;
using Komet.Api;
using Vintagestory.API.Common;

internal static class KometBridge
{
    internal static void Start(ModSystem system, string source, Action startOwn)
    {
        var komet = Kapi.Connect(system, "1.0");
        if (komet is null) { startOwn(); return; } // no KAPI 1.0: the log says why

        var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", source));
        if (!fx.Active) startOwn(); // fx.Error and the log say why
    }
}
```

## Draw in Komet's frame

A renderer of your own can sit in Komet's frame as a render step instead of registering with the game itself:
`komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, Draw))`. Komet times it,
books it in `.komet debug`, switches it off for the world when it throws, and the player can switch your steps off.
Every connected mod, with its steps and their cost, shows on the Connected mods page (*Overview › Connected mods*);
a mod Komet refused shows there too, with the reason.

## Verify fallback

- Without Komet or with the setting off, the normal path starts.
- With a Komet that does not offer KAPI 1.0, `Connect` returns null: the normal path starts and the log says
  `KAPI did not connect` with both versions.
- If `TryAddPasses` refuses the effect, `fx.Active` is false. The normal path starts and `fx.Error` and the log give
  the reason.
- If it succeeds, Komet owns the effect until the world closes; the next `StartClientSide` connects again. Test
  rendering and world exit on both renderers you intend to support.

[API reference and versioning](docs-api)

[Shader API and render paths](docs-api-shaders)

[Render steps](docs-api-rendering)

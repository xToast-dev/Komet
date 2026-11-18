# Shaders from code

<xref:Komet.Api.KapiShaders> (`komet.Shaders`) does from C# what `komet.json` does from a file, and more: live values
from the game, source edits, and passes built at run time.

```csharp
internal static class KometBridge
{
    public static void Start(ModSystem system)
    {
        var komet = Kapi.Connect(system, "1.0");
        if (komet is null) return; // a Komet without KAPI 1.0: the log says why

        // a define in two game programs; read again at each reload
        komet.Shaders.Define("MYMOD_SOFT_SHADOWS", () => Config.Soft ? "1" : "0", "chunkopaque", "chunktopsoil");

        // a live uniform: every program and pass that declares it gets it
        komet.Shaders.Uniform("mymodWetness", () => Weather.Wetness);

        // a source edit: Komet's own edits came first, so match loosely
        komet.Shaders.Edit("final", null, fragment => fragment
            .Replace("void main(void)", "uniform float mymodTint;\nvoid main(void)")
            .Replace("outColor = mix(", "outColor = mymodTint * mix("));
        komet.Shaders.Uniform("mymodTint", () => Config.Tint);

        // a pass of your own
        komet.Shaders.AddPass(new KapiPass("outline", Source("outline.fsh"))
        {
            Order = 50,
            Enabled = () => Config.Outline,
            Uniforms = { ["mymodWidth"] = () => [Config.Width] }
        });
    }
}
```

The mod id comes from the connection; every registration returns a <xref:Komet.Api.KapiRegistration> whose
`Dispose()` takes it back early. After changing a define's value, call <xref:Komet.Api.KapiShaders.Reload>; several
calls in one frame reload once. Uniforms need no reload.

## A pass or your own renderer

<xref:Komet.Api.KapiShaders.TryAddPasses*> compiles the passes at once and adds all or none. A mod with a renderer of
its own starts that one when Komet refuses:

```csharp
var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", gradeSource), new KapiPass("bloom", bloomSource));
if (!fx.Active) StartOwn(); // fx.Error says why
```

<xref:Komet.Api.KapiShaders.EditPass*> rewrites another mod's pass before it compiles.

## Defines for passes

A define reaches a pass of the same mod when its program list is empty or names `pass:<id>`:

```csharp
komet.Shaders.Define("MYMOD_SAMPLES", () => "8", "pass:outline");
```

Packs from `komet.json` count as the same mod as code whose mod id (from `modinfo.json`) is the mod's domain.

## Which renderer draws

```csharp
if (KapiShaders.Renderer == KapiRenderer.Vulkan) { ... }
komet.Shaders.RendererChanged += (_, _) => { ... };
```

Most mods need neither: Komet rebuilds programs and passes on a switch, and `KOMET_VULKAN` tells the shader.

## Limits

All mods together: 128 defines, 256 uniforms, 64 edits, 32 passes. Names must be GLSL names (letters, digits,
`_`, at most 64 characters).

A refused name logs `Komet shaders: ... registered "...", which is no GLSL name`. A callback that throws is
logged once (`Komet shaders: ...'s callback for ... threw`) and skipped from then on.

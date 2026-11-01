# Shaders from code

<xref:Komet.Api.KometShaders> does from C# what `komet.json` does from a file, and more: live values from the
game, source edits, and passes built at run time.

```csharp
internal static class KometBridge
{
    private const string Mod = "mymod";   // the mod's domain

    public static void Start(ICoreClientAPI api)
    {
        // a define in two game programs; read again at each reload
        KometShaders.Define(Mod, "MYMOD_SOFT_SHADOWS", () => Config.Soft ? "1" : "0", "chunkopaque", "chunktopsoil");

        // a live uniform: every program and pass that declares it gets it
        KometShaders.Uniform(Mod, "mymodWetness", () => Weather.Wetness);

        // a source edit: Komet's own edits came first, so match loosely
        KometShaders.Edit(Mod, "final", null, fragment => fragment
            .Replace("void main(void)", "uniform float mymodTint;\nvoid main(void)")
            .Replace("outColor = mix(", "outColor = mymodTint * mix("));
        KometShaders.Uniform(Mod, "mymodTint", () => Config.Tint);

        // a pass of your own
        KometShaders.AddPass(new KometPass(Mod, "outline", KometPassStage.Final, Source("outline.fsh"))
        {
            Order = 50,
            Enabled = () => Config.Outline,
            Uniforms = { ["mymodWidth"] = () => [Config.Width] }
        });
    }
}
```

After changing a define's value, call <xref:Komet.Api.KometShaders.Reload>; several calls in one frame reload
once. Uniforms need no reload.

## Defines for passes

A define reaches a pass of the same mod when its program list is empty or names `pass:<id>`:

```csharp
KometShaders.Define("mymod", "MYMOD_SAMPLES", () => "8", "pass:outline");
```

Packs from `komet.json` count as the same mod as code that uses the mod's domain.

## Which renderer draws

```csharp
if (KometShaders.Renderer == KometRenderer.Vulkan) { ... }
KometShaders.RendererChanged += (_, _) => { ... };
```

Most mods need neither: Komet rebuilds programs and passes on a switch, and `KOMET_VULKAN` tells the shader.

## Limits

All mods together: 128 defines, 256 uniforms, 64 edits, 32 passes. Names must be GLSL names (letters, digits,
`_`, at most 64 characters).

A refused name logs `Komet shaders: ... registered "...", which is no GLSL name`. A callback that throws is
logged once (`Komet shaders: ...'s callback for ... threw`) and skipped from then on.

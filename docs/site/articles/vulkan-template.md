# The Vulkan shader template

A complete shader mod that runs on OpenGL and on Komet's Vulkan renderer: two passes, settings with uniforms and a
define, texts in two languages, and an optional C# part.

Get it in game with *Modding › Templates › Create Vulkan template* (it lands in
`ModConfig/komet/mod-template/myshaders-dev` and the folder opens), or download
[komet-vulkan-template.zip](../downloads/komet-vulkan-template.zip).

## Files

```text
myshaders-dev/
  README.md
  myshaders/                               the mod: copy this folder into Mods
    modinfo.json
    assets/myshaders/config/komet.json     settings, passes, renderer
    assets/myshaders/shaders/haze.fsh      pass: distance haze from the depth
    assets/myshaders/shaders/grade.fsh     pass: colour grading
    assets/myshaders/lang/en.json, de.json texts, used as myshaders:key
  code/                                    optional C#, kept out of the mod folder
    MyShaders.csproj
    MyShadersSystem.cs
    KometBridge.cs
```

The C# sources stay beside the mod on purpose: the game refuses a zipped mod that holds `.cs` files outside
`src/`.

## komet.json, piece by piece

```json
{
  "programs": ["pass:grade", "pass:haze"],
  "settings": [
    { "type": "group", "label": "myshaders:group-grade" },
    { "key": "grade", "label": "myshaders:grade", "type": "switch", "default": 1 },
    { "key": "exposure", "label": "myshaders:exposure", "type": "slider", "min": -1, "max": 1, "step": 0.05,
      "default": 0, "unit": "EV", "uniform": "myshadersExposure" },
    { "key": "quality", "label": "myshaders:quality", "type": "choice",
      "options": ["myshaders:quality-fast", "myshaders:quality-fine"], "default": 1, "define": "MYSHADERS_QUALITY" }
  ],
  "passes": [
    { "id": "haze", "fragment": "shaders/haze.fsh", "order": 10, "when": "haze" },
    { "id": "grade", "fragment": "shaders/grade.fsh", "order": 20, "when": "grade" }
  ],
  "renderer": "both"
}
```

| Part | What it does |
|---|---|
| `programs` | Which programs get the defines. `pass:grade` and `pass:haze` are the mod's own passes; without the list the defines would also go into every game program and each change would rebuild all of them |
| `group` | A heading on the mod's page |
| `switch` `grade` | The player's switch; `when: "grade"` on the pass follows it at once |
| `slider` with `uniform` | Set as `uniform float myshadersExposure` before each draw: changes at once |
| `choice` with `define` | `#define MYSHADERS_QUALITY 0` or `1` before the pass; a change rebuilds the passes |
| `myshaders:...` labels | Lang keys from `assets/myshaders/lang/`; plain text works too |
| `order` | Lower draws first: the haze is drawn, then graded |
| `renderer: both` | The mod works on both renderers. `opengl` makes Komet hold its Vulkan renderer off while the mod runs |

## haze.fsh: reading the depth

```glsl
uniform float myshadersHaze;

void main()
{
    vec3 c = texture(scene, uv).rgb;
    float d = texture(depth, uv).r;
    float far = smoothstep(0.990, 0.9995, d) * (1.0 - step(0.99999, d));
    color = vec4(mix(c, vec3(0.72, 0.80, 0.90), far * myshadersHaze), 1.0);
}
```

- `scene`, `depth`, `uv` and `color` come from the [pass prelude](passes.md); do not declare them.
- `depth` is the window depth, 0..1, and not linear: nearly the whole landscape lies above 0.99. 1.0 is the sky.
- It is the same on OpenGL and Vulkan: Komet's Vulkan renderer keeps OpenGL's depth range and row order.

## grade.fsh: defines and a Vulkan branch

```glsl
#ifndef MYSHADERS_QUALITY
#define MYSHADERS_QUALITY 1
#endif

void main()
{
    vec3 c = texture(scene, uv).rgb;
#if MYSHADERS_QUALITY >= 1
    c = sharpened(c);
#endif
    c *= exp2(myshadersExposure);
    ...
#ifdef KOMET_VULKAN
    // only under Komet's Vulkan renderer
#endif
    color = vec4(max(c, 0.0), 1.0);
}
```

- Give every define of yours a default: it is missing when the setting is removed or an older Komet builds the pass.
- `KOMET` is always defined in a pass, `KOMET_VULKAN` only while Vulkan draws. Komet rebuilds the passes when
  the renderer switches.

## The code part

`KometBridge.cs` shows what a file cannot do: a value from the game, live, and a define for one pass.

```csharp
KometShaders.Define("myshaders", "MYSHADERS_CODE", () => "1", "pass:haze");
KometShaders.Uniform("myshaders", "myshadersHour", () => api.World.Calendar.HourOfDay);
```

The mod id `myshaders` is the mod's domain, the same id the `komet.json` pack has, so both reach the file's
passes. Build it with
`dotnet build -c Release -p:VintageStory=<game folder> -p:Komet=<path to Komet.Core.dll>`; `MyShaders.dll` lands
in `myshaders/` next to `modinfo.json`. Then set `"type": "code"` there.

## Checklist before you publish

1. Both renderers: *Komet › Overview › Renderer*, OpenGL then Vulkan. Same picture?
2. The mod's row in *Active shaders*: renderer badge red (Vulkan, all ported), no warning sign.
3. `client-main.log` has no `Komet shaders: pass` or `Komet: Vulkan cannot draw` lines for your mod.
4. Without Komet: the mod loads and does nothing harmful.

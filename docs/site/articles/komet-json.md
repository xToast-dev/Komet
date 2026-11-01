# komet.json reference

`assets/<modid>/config/komet.json` binds a mod into Komet. Komet reads it from every enabled mod when a world
loads. The pack id is the mod's domain; name and author come from `modinfo.json` unless the file sets them.
Schema for editors: `docs/schema/komet.schema.json` (add `"$schema"` with its URL and editors complete the keys).
A file that does not parse is skipped with one line in `client-main.log`:
`Komet shaders: ... is not a valid komet-shader.json: ...`, naming the file and the JSON line.

A shader pack without a mod uses the same fields in `komet-shader.json`, either in a mod at
`assets/<domain>/shaders/komet-shader.json` or in the player's folder `ModConfig/komet/shaders/<id>/`.

## Top level

| Key | Type | Meaning |
|---|---|---|
| `id` | string | Pack id; default: the mod's domain (or the folder's name) |
| `name` | string | Page title; may be a lang key |
| `author` | string | Shown in the detail panel |
| `programs` | string[] | Who gets the defines: game program names (`final`, `chunkopaque` ...) and `pass:<id>` for the pack's passes. Missing: every game program and every pass of the pack |
| `settings` | object[] | Rows of the pack's page, at most 40 |
| `passes` | object[] | Fullscreen passes, at most 32 |
| `renderer` | `both` or `opengl` | `opengl`: Komet holds its Vulkan renderer off while the mod runs |
| `config` | object | The mod's own settings file, steered by Komet's preset (below) |
| `holds` | object[] | Komet features to hold at the game's behaviour while the mod runs |
| `_comment` | string | Ignored |

## settings[]

| Key | Meaning |
|---|---|
| `key` | Unique in the pack; the player's value is stored under it |
| `label`, `hint` | Row title and detail text; plain or a lang key (`domain:key`) |
| `type` | `switch`, `slider`, `choice` or `group` (a heading) |
| `min`, `max`, `step`, `unit` | A slider's range, step and the text after its value |
| `options` | A choice's names; its value is the index |
| `default` | The value at first start and after Standard |
| `define` | `#define NAME value` in the programs of `programs`; a change rebuilds the shaders |
| `uniform` | `uniform float NAME`, set before each draw; live |

Values: a switch is 0 or 1, a choice its index, a slider its number. A define's value is written as an integer
for switches and choices, as a decimal number for sliders.

## passes[]

| Key | Meaning |
|---|---|
| `id` | Unique in the pack; `pass:<id>` names it in `programs` |
| `fragment` | The fragment shader, a path inside the mod's assets (`shaders/grade.fsh`) |
| `order` | Lower draws first |
| `when` | Key of a switch that turns the pass on and off |

What a pass sees is in [Writing passes](passes.md).

## config: steering the mod's own settings

For mods with a settings file of their own. Komet's preset level (quality, balanced, speed) picks a profile, and
Komet writes the file and runs the mod's reload command. The player can choose *own* to keep their hands on it.

```json
"config": {
  "file": "mymod.json",
  "reload": ".mymod reload",
  "levels": {
    "quality":  { "from": "profiles/quality.json" },
    "balanced": { "from": "profiles/quality.json", "set": { "shadows.distance": 128 } },
    "speed":    { "from": "profiles/speed.json" }
  },
  "switches": [ { "key": "bloom.enabled", "label": "Bloom" } ]
}
```

| Key | Meaning |
|---|---|
| `file` | The settings file in `ModConfig` |
| `reload` | A client command that reloads it; Komet runs it with full privileges |
| `levels.<level>.from` | A profile file in `assets/<modid>/config/` |
| `levels.<level>.set` | Dotted keys set on top of the profile |
| `switches` | Boolean keys shown as switches on the mod's page |

Before the first write Komet keeps a copy of the player's file with the ending `.before-komet`.

## holds[]

```json
"holds": [ { "feature": "FrustumSweep", "reason": "Our shadow pass needs the game's culling" } ]
```

Holds the named Komet feature at the game's own behaviour while the mod runs, like
<xref:Komet.Api.KometFeatures.HoldOff*> from code.

## The Komet path for files

A mod can ship a second version of a shader file for Komet: `assets/<domain>/config/komet/shaders/<file>` (or
`shaderincludes/`) stands in for `assets/<domain>/shaders/<file>` while Komet runs. Without Komet the game never
sees it. For small differences an `#ifdef KOMET` in the normal file is simpler.

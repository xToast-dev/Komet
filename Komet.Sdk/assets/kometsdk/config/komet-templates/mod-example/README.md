# My Komet Mod

A small shader mod made for Komet, to copy and change.

1. Copy this folder into your Mods folder (`VintagestoryData/Mods`). In Komet's options, Modding › Templates ›
   Mods folder opens it.
2. Enter a world (if you are already in one, leave it and enter it again). Shaders › Active shaders now lists
   "My Komet Mod" under "Made for Komet", and Shaders has a page of its own for it with a switch and a slider.
3. Change `assets/mymod/config/komet.json` or `assets/mymod/shaders/warm.fsh`, then press Shaders › Active shaders ›
   Reload packs. You do not need to leave the world.
4. Rename `mymod` to your own id everywhere: `modid` in `modinfo.json`, the folder `assets/mymod` (it must match the
   modid) and the uniform `mymodWarmth` in `komet.json` and `warm.fsh`.

In game, Komet's options › Modding › "Your mod for Komet" walks through every step.

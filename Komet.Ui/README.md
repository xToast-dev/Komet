# Komet.Ui


Everything visible: the HUD (`Hud/`) and the options menu with styles and help (`Options/`). The top module: it may
use every other one, and none knows it.

Features: `Hud/HudFeatures.cs` and `Options/OptionsFeatures.cs`.

`Options/ShaderOverview.cs` owns shader page updates and switches; `Options/ShaderOverview.Details.cs` owns its badges,
cards and status text.

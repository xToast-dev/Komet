# Komet.Sdk

The Shader Studio (Shader Workbench) for shader authors: Komet's top module, shipped in Komet's zip and started by
`KometModSystem`. Its assets keep the domain `kometsdk`.

- `Workbench/`: the full-screen studio (ShaderIde), its explorer, panels, passes tab, game values, engine view.
- `Editor/`: the code editor, completion, diagnostics, GLSL docs.
- `Backend/`: projects, project copies in the game's assets, builds and history, per-pass attribution and timing,
  the decompiler index (Engine view).
- `KometSdk.cs`: hands Komet the workbench (`SdkLink.Workbench`) and the shader-loading hooks
  (`StudioHooks`) in `Start`, patches in `StartClientSide`, puts everything back at the world's end.

Komet's side: `Komet.Rendering/Shaders/StudioHooks.cs` and `Komet.Ui/Options/IDevTool.cs` (`SdkLink`). Without the kit
the developer screen (Ctrl+F8) says where the workbench lives. Lang keys stay in Komet's lang files.

Build: `dotnet build Komet.Sdk/Komet.Sdk.csproj`; release zip: `-c Release -t:Package` (build.sh does both).
The kit needs the Komet of the same version (modinfo.json).

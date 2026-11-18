using HarmonyLib;
using Komet.Options;
using Komet.Rendering;

namespace Komet.Sdk;

// The Komet SDK's entry: in Start (before any StartClientSide, Komet's included) it hands Komet the workbench and the
// shader-loading hooks (StudioHooks); in StartClientSide the per-pass attribution patches in; at the world's end
// everything it put into the game's assets and renderers goes back. Without this mod Komet runs no workbench code.
// ReSharper disable once UnusedType.Global the game's mod loader makes it
public sealed class KometSdkSystem : ModSystem
{
    private const string HarmonyId = "komet.sdk";

    private Harmony? _harmony;
    private WeakReference<ShaderIde>? _ide; // the HUD owns and disposes it
    private ICoreClientAPI? _api;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void Start(ICoreAPI api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _harmony = new Harmony(HarmonyId);
        ProjectAssets.InstallInterfaceCheck(_harmony, api);
        SdkLink.Workbench = (capi, preview) =>
        {
            var ide = new ShaderIde(capi, preview);
            _ide = new WeakReference<ShaderIde>(ide);
            return ide;
        };
        StudioHooks.Loading = ProjectAssets.Apply;
        StudioHooks.Validate = ProjectAssets.Validate;
        StudioHooks.Used = DrawCounter.Used;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        ShaderModules.Install(_harmony ??= new Harmony(HarmonyId), api);
        LiveForce.Install(_harmony);
        _ = api.ChatCommands.GetOrCreate("komet").BeginSubCommand("shadertest")
            .WithDescription("Shows every vanilla shader in the workbench's 3D view and saves the pictures and a report")
            .HandleWith(_ => ShaderTest(api, Path.Join(GamePaths.Logs, "komet-shadertest",
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)), false))
            .EndSubCommand();
        if (Environment.GetEnvironmentVariable("KOMET_SHADER_TEST") is { Length: > 0 } folder && api.Event is { } world)
        {
            world.LevelFinalize += () => _ = ShaderTest(api, folder, true); // scripts/shadertest.sh: test, then exit
        }

        var mode = Environment.GetEnvironmentVariable("KOMET_BENCH_STUDIO");
        if (mode is "open" or "folded" && api.Event is { } events)
        {
            events.LevelFinalize += () => BenchStudio(api, mode == "open");
        }
    }

    // The developer screen opens and its workbench runs the shader test into the folder
    private TextCommandResult ShaderTest(ICoreClientAPI api, string folder, bool exit)
    {
        Hud.HudOverlay.DeveloperForBench?.Invoke();
        if (_ide?.TryGetTarget(out var ide) != true || ide is null) return TextCommandResult.Error("workbench not open");

        ide.StartTest(folder, exit);
        api.Logger.Notification("Komet SDK: shader test into {0}", folder);
        return TextCommandResult.Success("shader test → " + folder);
    }

    // Measuring the studio's cost (scripts/bench.sh --env KOMET_BENCH_STUDIO=open|folded): the developer screen stays
    // open for the run, its PREVIEW section open or folded
    private void BenchStudio(ICoreClientAPI api, bool preview)
    {
        Hud.HudOverlay.DeveloperForBench?.Invoke();
        if (_ide?.TryGetTarget(out var ide) == true)
        {
            _ = ide.Prefs.Sections.Remove("view");
            if (preview) ide.Prefs.Sections.Add("view");

            ide.Redraw();
        }

        api.Logger.Notification("Komet SDK bench: studio open, preview {0}", preview ? "open" : "folded");
    }

    public override void Dispose()
    {
        LiveForce.Clear(); // the player's graphics settings back
        ShaderModules.Clear();
        ProjectAssets.Clear(_api);
        ProjectAssets.ClearInterfaceCheck();
        ShaderProjects.Forget();
        _harmony?.UnpatchAll(HarmonyId);
        (SdkLink.Workbench, StudioHooks.Loading, StudioHooks.Validate, StudioHooks.Used) = (null, null, null, null);
        (_harmony, _api) = (null, null);
        base.Dispose();
    }
}

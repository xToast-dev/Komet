using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Komet.Features;
using Komet.Runtime.UI.Overlay;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    private Harmony? _harmony;
    private HudOverlay? _overlay;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Contracts.Attach(Mod.Logger);
        if (!Assert(_harmony is null && _overlay is null) || !Assert(Mod.Info.ModID == "komet")) return;   // the id is hardcoded in the stats   // the game starts a mod system once
        _harmony = new Harmony(Mod.Info.ModID);
        _overlay = new HudOverlay(api);
        ShaderUseCache.Install(_harmony);
        ModProfiler.Install(_harmony, api);
        foreach (var stage in (ReadOnlySpan<EnumRenderStage>)[EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done])
            api.Event.RegisterRenderer(_overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet opens the settings");
    }

    [SuppressMessage("Design", "CA1063", Justification = "ModSystem.Dispose is virtual and the class is sealed, so the override cannot be overridden")]
    public override void Dispose()
    {
        _overlay?.Dispose();
        _harmony?.UnpatchAll(_harmony.Id);
        (_overlay, _harmony) = (null, null);
    }
}

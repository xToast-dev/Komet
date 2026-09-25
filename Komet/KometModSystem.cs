using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Komet.Features;
using Komet.Interop;
using Komet.Runtime.UI.Overlay;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    private Harmony? _harmony;
    private HudOverlay? _overlay;
    private string[] _optimumYields = [];

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Contracts.Attach(Mod.Logger);
        if (!Assert(_harmony is null && _overlay is null) || !Assert(Mod.Info.ModID == "komet")) return;   // started once; the id is hardcoded in the stats
        string[] activeFeatures = ModInterop.TryGetFeatureState(VsModInterop.ProviderId,
            VsModInterop.FeatureAdaptiveChunkInflow, out bool inflowActive) && inflowActive
            ? [VsModInterop.FeatureAdaptiveChunkInflow]
            : [];
        if (!Assert(activeFeatures.Length <= 1) || !Assert(VsModInterop.ProtocolVersion == 1)) return;
        if (!ModInterop.TryNegotiate(ModInterop.OptimumProviderId, VsModInterop.ProviderId, activeFeatures, out _optimumYields))
            Mod.Logger.Debug("Komet interop: Optimum did not answer feature negotiation.");
        try
        {
            _harmony = new Harmony(Mod.Info.ModID);
            _overlay = new HudOverlay(api);
            ShaderUseCache.Install(_harmony);
            ModProfiler.Install(_harmony, api);
        }
        catch
        {
            _overlay?.Dispose();
            _harmony?.UnpatchAll(_harmony.Id);
            _overlay = null;
            _harmony = null;
            ModInterop.Release(ModInterop.OptimumProviderId, VsModInterop.ProviderId, _optimumYields);
            _optimumYields = [];
            throw;
        }
        if (ModInterop.TryGetFeatureState(ModInterop.OptimumProviderId, ModInterop.OptimumAdaptiveRadius, out bool adaptiveRadius))
            Mod.Logger.Notification("Komet interop: Optimum adaptive radius is {0}", adaptiveRadius ? "active" : "inactive");
        if (_optimumYields.Length > 0)
            Mod.Logger.Notification("Komet interop: Optimum yielded {0}", string.Join(", ", _optimumYields));
        foreach (var stage in (ReadOnlySpan<EnumRenderStage>)[EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done])
            api.Event.RegisterRenderer(_overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet opens the settings");
    }

    [SuppressMessage("Design", "CA1063", Justification = "ModSystem.Dispose is virtual and the class is sealed, so the override cannot be overridden")]
    public override void Dispose()
    {
        _overlay?.Dispose();
        _harmony?.UnpatchAll(_harmony.Id);
        ModInterop.Release(ModInterop.OptimumProviderId, VsModInterop.ProviderId, _optimumYields);
        _optimumYields = [];
        (_overlay, _harmony) = (null, null);
    }
}

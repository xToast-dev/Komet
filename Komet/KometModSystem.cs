using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Rendering;
using Vintagestory.Client.NoObf;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    internal const string ModId = KometMod.Id;
    private const int HudStageCount = 3, PoolMs = 250;

    private static readonly EnumRenderStage[] HudStages =
        [EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done];

    private ICoreClientAPI? _api;
    private FeatureContext? _context;
    private long _pool;

    internal static bool LocalServer => KometMod.LocalServer;

    public override void Dispose()
    {
        Komet.Core.Listeners.Unregister(_api?.Event, _pool);
        Features.Stop(_context);
        _context?.Harmony.UnpatchAll(_context.Harmony.Id);
        Features.Unpatched();
        ShaderMods.Release();
        Komet.Options.ModAdapters.Stop();
        (_context, _api, _pool) = (null, null, 0);
    }

    // Before any mod's Start: the public API logs what it refuses from then on
    public override void StartPre(ICoreAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        KometMod.Attach(typeof(KometModSystem).Assembly);
        Modules.Register();
        ApiEvents.Logger = Mod.Logger;
        if (api is ICoreClientAPI) ShaderMods.HoldBack(api, Mod.Logger); // before the other mods' Start
    }

    // The mods' assets are there, no other mod has started: held-back mods' files give way, Komet versions stand in
    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI capi && NotNull(Mod.Logger)) ShaderMods.Swap(capi);
    }

    // Client only, though modinfo says "Universal" for MapTracking: a singleplayer server instance's Dispose would clear the
    // client's statics
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Attach(Mod.Logger);
        if (!Assert(_context is null) || !Assert(Mod.Info.ModID == ModId)) return;
        try
        {
            Install(api);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // All or nothing. RegisterRenderer throws after earlier stages when another mod took the HUD's order.
            Mod.Logger.Error("Komet: install failed, Komet stands down: {0}", e);
            api.Event.LevelFinalize -= Features.Recheck;
            if (_context?.Overlay is { } overlay)
                foreach (var stage in HudStages.Bounded(HudStageCount))
                    api.Event.UnregisterRenderer(overlay, stage);
            Dispose();
        }
    }

    private void Install(ICoreClientAPI api)
    {
        KometMod.LocalServer = api.IsSinglePlayer;
        (_context, _api) = (new FeatureContext(new Harmony(Mod.Info.ModID), api, Mod.Logger, LocalServer), api);
        Mod.Logger.Notification(LocalServer
            ? "Komet: the server runs in this process, its chunk and worldgen patches are installed"
            : "Komet: remote server, Komet patches the client only");
        Features.Install(_context, FeatureStage.Main);
        Features.InstallQueued(_context);
        Komet.Options.ModAdapters.Start(api);
        api.Event.LevelFinalize += Features.Recheck;
        _pool = api.Event.RegisterGameTickListener(dt =>
        {
            if (!NotNull(_api) || !Finite(dt)) return;
            WorkerPool.Steer(_api.World as ClientMain);
            Features.Poll();
        }, PoolMs);
        if (NotNull(_context.Overlay))
            foreach (var stage in HudStages.Bounded(HudStageCount))
                api.Event.RegisterRenderer(_context.Overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet or Escape → Settings opens the options");
        Features.Install(_context, FeatureStage.Last);
    }
}

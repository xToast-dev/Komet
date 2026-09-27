using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    internal const string ModId = "komet";
    private const int HudStageCount = 3;
    private const int PoolMs = 250; // how often the worker pool follows its knob and the world

    private static readonly EnumRenderStage[] HudStages =
        [EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done];

    private ICoreClientAPI? _api;
    private FeatureContext? _context;
    private long _pool;

    // Whether the game's server runs in this process: a singleplayer world, also one opened to LAN (ClientMain.IsSingleplayer, set
    // before the mods start). Only then does Komet patch server code; on a remote server, vanilla or a fork such as Stratum, it patches
    // the client alone and leaves everything the server decides (what it sends, how fast, in which order) to the server.
    internal static bool LocalServer { get; private set; }

    public override void Dispose()
    {
        if (_api is not null && _pool != 0) _api.Event.UnregisterGameTickListener(_pool);
        Features.Stop(_context);
        _context?.Harmony.UnpatchAll(_context.Harmony.Id);
        Features.Unpatched();
        (_context, _api, _pool) = (null, null, 0);
    }

    // Before any mod's Start: the public API logs what it refuses from then on
    public override void StartPre(ICoreAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        ApiEvents.Logger = Mod.Logger;
    }

    // Besides modinfo "side": "Client": a singleplayer server instance's Dispose would clear the client's statics
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Attach(Mod.Logger);
        // started once, and as the id hardcoded in the stats and the bench
        if (!Assert(_context is null) || !Assert(Mod.Info.ModID == ModId)) return;
        try
        {
            Install(api);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // All or nothing: a feature that threw halfway through its own patches must not stay half installed.
            // RegisterRenderer throws when another mod reserved the HUD's render order, after earlier stages went in.
            Mod.Logger.Error("Komet: install failed, Komet stands down: {0}", e);
            api.Event.LevelFinalize -= Features.Recheck;
            if (_context?.Overlay is { } overlay)
                foreach (var stage in HudStages.Bounded(HudStageCount))
                    api.Event.UnregisterRenderer(overlay, stage);
            Dispose();
        }
    }

    // The features in Features' order: Main, other mods' registered before Komet started, then the bench, the HUD's renderers and
    // last PreJit
    private void Install(ICoreClientAPI api)
    {
        LocalServer = api.IsSinglePlayer;
        (_context, _api) = (new FeatureContext(new Harmony(Mod.Info.ModID), api, Mod.Logger, LocalServer), api);
        Mod.Logger.Notification(LocalServer
            ? "Komet: the server runs in this process, its chunk and worldgen patches are installed"
            : "Komet: remote server, Komet patches the client only");
        Features.Install(_context, FeatureStage.Main);
        Features.InstallQueued(_context);
        Features.Install(_context, FeatureStage.Tail);
        api.Event.LevelFinalize += Features.Recheck;
        _pool = api.Event.RegisterGameTickListener(SteerPool, PoolMs);
        if (NotNull(_context.Overlay))
            foreach (var stage in HudStages.Bounded(HudStageCount))
                api.Event.RegisterRenderer(_context.Overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet or Escape → Settings opens the options");
        Features.Install(_context, FeatureStage.Last);
    }

    // The worker pool follows its knob and the world being played, on the main thread; the features' states follow too
    private void SteerPool(float dt)
    {
        if (!NotNull(_api) || !Finite(dt)) return;
        WorkerPool.Steer(_api.World as ClientMain);
        Features.Poll();
    }
}

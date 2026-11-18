using HarmonyLib;
using Komet.Core;
using Komet.Options;
using Komet.Rendering;

namespace Komet;

/// <summary>
/// One client world's run of Komet, which KometModSystem only forwards its lifecycle to: the composition (Modules) and the
/// shader mods' hold-back before any other mod starts, the features' install, the mod adapters, the recheck at
/// LevelFinalize and the state poll, and the teardown in reverse. Each module's own start lives in its features.
/// </summary>
internal sealed class ClientRuntime
{
    private const int PollMs = 250;
    private const string Owner = KometMod.Id;

    private ICoreClientAPI? _api;
    private FeatureContext? _context;
    private long _poll;

    /// <summary>
    /// Before any mod's Start: the public API logs what it refuses from then on
    /// </summary>
    /// <param name="api"></param>
    /// <param name="logger"></param>
    /// <param name="mod"></param>
    public static void Prepare(ICoreAPI api, ILogger logger, System.Reflection.Assembly mod)
    {
        KometMod.Attach(mod);
        Modules.Register();
        ModEvents.Logger = logger;
        if (api is ICoreClientAPI)
        {
            ShaderMods.HoldBack(api, logger); // before the other mods' Start
        }
    }

    /// <summary>
    /// The mods assets are there, no other mod has started: held-back mods' files give way, Komet versions stand in
    /// </summary>
    /// <param name="api"></param>
    public static void AssetsLoaded(ICoreClientAPI api) => ShaderMods.Swap(api);

    /// <summary>
    /// A throw propagates: the mod system stands Komet down as a whole
    /// </summary>
    /// <param name="api"></param>
    /// <param name="logger"></param>
    /// <param name="harmonyId"></param>
    public void Start(ICoreClientAPI api, ILogger logger, string harmonyId)
    {
        KometMod.LocalServer = api.IsSinglePlayer;
        (_context, _api) = (new FeatureContext(new Harmony(harmonyId), api, logger, KometMod.LocalServer), api);
        logger.Notification(KometMod.LocalServer
            ? "Komet: the server runs in this process, its chunk and worldgen patches are installed"
            : "Komet: remote server, Komet patches the client only");
        Features.Start(_context);
        ModAdapters.Start(api);
        Options.ConfigLibPages.Start(api);
        api.Event.LevelFinalize += Features.Recheck;
        _poll = api.Event.RegisterGameTickListener(Poll, PollMs);
        logger.Notification("Komet HUD ready – F7 toggles it, .komet or Escape → Settings opens the options");
    }

    private static void Poll(float dt)
    {
        if (Finite(dt)) Features.Poll();
    }

    /// <summary>
    /// Also after a failed Start: what was attached goes, every step on its own
    /// </summary>
    public void Stop()
    {
        var (api, context, poll) = (_api, _context, _poll);
        (_context, _api, _poll) = (null, null, 0);
        if (api is not null)
        {
            Features.Cleanup(Owner, "detach world events", () => api.Event.LevelFinalize -= Features.Recheck);
            Features.Cleanup(Owner, "detach tick listener", () => Listeners.Unregister(api.Event, poll));
        }

        if (context is not null)
        {
            Features.Stop(context);
            Features.Cleanup(Owner, "unpatch", () => context.Harmony.UnpatchAll(context.Harmony.Id));
            Features.Cleanup(Owner, "release feature state", Features.Unpatched);
        }

        Features.Cleanup(Owner, "release shader mods", ShaderMods.Release);
        Features.Cleanup(Owner, "release mod adapters", ModAdapters.Stop);
        Features.Cleanup(Owner, "release ConfigLib pages", Options.ConfigLibPages.Stop);
        Features.Cleanup(Owner, "release services", Modules.Release);
    }
}

using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Komet;

// SystemRemotePlayerTracking (1.22.7) sends the players' positions for the map only when the world configuration says
// allowMap, and reads it as GetBool("allowMap") whose default is false, while every other map check in the game takes
// true. A world saved without the key (some are) gets a map but never a player arrow on it. Here, after every StartPre,
// such a world gets the server side the system skipped: its join and leave handlers and its 100 ms tick. Nothing is
// written to the world; a world that says allowMap false stays without.
// A ModSystem lives in this project: the game accepts one assembly with mod systems per mod (ModulesTests guards it).
public sealed class MapTracking : ModSystem
{
    private const string Key = "allowMap";
    private const int TickMs = 100;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        if (!NotNull(api) || api.World?.Config is not { } config || config.HasAttribute(Key)) return;
        if (api.ModLoader.GetModSystem<SystemRemotePlayerTracking>() is not { } tracking) return;
        var (join, leave, tick) = (Handler<IServerPlayer>(tracking, "ServerEvent_PlayerJoin"), Handler<IServerPlayer>(tracking, "ServerEvent_PlayerDisconnect"),
            Handler<float>(tracking, "OnTick"));
        if (join is null || leave is null || tick is null)
        {
            Mod.Logger.Warning("Komet: the map's player arrows stay off in this world, the tracking system has another shape");
            return;
        }

        api.Event.PlayerJoin += join.Invoke;
        api.Event.PlayerDisconnect += leave.Invoke;
        _ = api.Event.RegisterGameTickListener(tick, TickMs);
        Mod.Logger.Notification("Komet: this world has no allowMap entry; the map's player arrows are sent as the map is allowed");
    }

    // A private handler of the tracking system as a delegate on it; null when the engine has none of that name
    private static Action<T>? Handler<T>(SystemRemotePlayerTracking tracking, string name)
    {
        var method = AccessTools.DeclaredMethod(typeof(SystemRemotePlayerTracking), name, [typeof(T)]);
        return method is MethodInfo { IsStatic: false } ? method.CreateDelegate<Action<T>>(tracking) : null;
    }
}

namespace Komet.Core;

// Whether the game's server runs in this process: a singleplayer world, also one opened to LAN (ClientMain.IsSingleplayer, set before
// the mods start). Only then does Komet patch server code; on a remote server, vanilla or a fork such as Stratum, it patches the client
// alone and leaves everything the server decides (what it sends, how fast, in which order) to the server.
internal static class LocalServer
{
    public static bool Present { get; private set; }

    public static void Detect(ICoreClientAPI api, ILogger logger)
    {
        Present = NotNull(api) && api.IsSinglePlayer;
        if (!NotNull(logger)) return;
        logger.Notification(Present
            ? "Komet: the server runs in this process, its chunk and worldgen patches are installed"
            : "Komet: remote server, Komet patches the client only");
    }
}

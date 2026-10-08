namespace Komet.Test;

// Before any fixture, as the game before it starts: its own dependencies resolve from the installation. A fixture that reaches
// protobuf-net (shapes, landforms) or cairo then passes alone as it does in the whole run. Windows open as the game opens its
// own: on a Wayland desktop RuntimeEnv sets OPENTK_4_USE_WAYLAND=0 unless the player set it, and OpenTK then asks GLFW for an
// X11 window (XWayland).
[SetUpFixture]
public sealed class GameAssemblies
{
    [OneTimeSetUp]
    public void ResolveLikeTheGame()
    {
        GameInstall.ResolveAssemblies();
        // the essentials mod, loaded before Komet as in the game: features find its types by name
        _ = typeof(Vintagestory.GameContent.MultiChunkMapComponent).Assembly;
        if (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" &&
            Environment.GetEnvironmentVariable("OPENTK_4_USE_WAYLAND") is null)
            Environment.SetEnvironmentVariable("OPENTK_4_USE_WAYLAND", "0");
    }
}

namespace Komet.Test;

// Before any fixture, as the game before it starts: its own dependencies resolve from the installation. A fixture that reaches
// protobuf-net (shapes, landforms) or cairo then passes alone as it does in the whole run.
[SetUpFixture]
public sealed class GameAssemblies
{
    [OneTimeSetUp]
    public void ResolveLikeTheGame()
    {
        GameInstall.ResolveAssemblies();
    }
}

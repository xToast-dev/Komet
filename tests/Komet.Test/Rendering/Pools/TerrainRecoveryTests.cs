using Komet.Rendering;

namespace Komet.Test.Rendering;

[NonParallelizable]
public sealed class TerrainRecoveryTests
{
    private static bool Pending => (bool)AccessTools.Field(typeof(TerrainRebuild), "_asked").GetValue(null)!;

    [SetUp]
    public void Start()
    {
        TerrainRebuild.Stop();
        var events = Answers.Of<IClientEventAPI>([]);
        TerrainRebuild.Install(Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["get_Event"] = _ => events }));
    }

    [TearDown]
    public void Stop() => TerrainRebuild.Stop();

    [Test]
    public void FallbackRecoverySurvivesTheWorldsFirstFrame()
    {
        TerrainRebuild.Request();
        Assert.That(Pending, Is.False, "initial settings do not rebuild terrain");
        TerrainRebuild.Recover();
        TerrainRebuild.Run();
        Assert.That(Pending, Is.True, "an early fallback must survive the first-frame readiness gate");
        TerrainRebuild.Recover();
        Assert.That(Pending, Is.True, "repeated requests coalesce");
    }

}

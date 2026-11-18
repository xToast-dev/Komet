using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class EngineSourceTests
{
    // The game's own chunk shader: its program's sites and a fog uniform's setter come out of the installation
    [Test]
    [Explicit("decompiles the installed game, about half a minute")]
    public async Task ChunkOpaqueFindsWhereTheGameSetsItsFog()
    {
        EngineSource.Start();
        var text = (string?)null;
        for (var i = 0; i < 600 && text is null; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            text = EngineSource.Ready || EngineSource.Failed ? EngineSource.Query("chunkopaque", ["rgbaFogIn"]) : null;
        }

        await TestContext.Out.WriteLineAsync(EngineSource.Error).ConfigureAwait(false);
        await TestContext.Out.WriteLineAsync(text?[..Math.Min(text.Length, 4000)]).ConfigureAwait(false);
        Assert.That(text, Is.Not.Null.And.Not.Empty);
        Assert.That(EngineSource.LineOf(text!, "rgbaFogIn"), Is.GreaterThanOrEqualTo(0));
    }
}

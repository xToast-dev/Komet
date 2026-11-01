using Komet.World;

namespace Komet.Test.World;

// The scan thread's one queued commit is rewritten, so the main thread only swaps the built tickers in
[NonParallelizable]
public sealed class TickerCommitTests
{
    [Test]
    public void TheCommitIsQueuedThroughKomet()
    {
        using var harmony = new TestHarmony("komet-test-tickercommit");
        TickerCommit.Install(harmony);
        Assert.That(TickerCommit.Rewritten, Is.True);
        TickerCommit.Forget();
    }
}

using Komet.Chunks;

namespace Komet.Test.Chunks;

public sealed class ChunkFactoriesTests
{
    [Test]
    public void BothDecompressionPathsReuseTheFactoryForTheirOwnPool()
    {
        foreach (var name in (ReadOnlySpan<string>)["Decompress", "DecompressSeparate"])
        {
            var method = AccessTools.DeclaredMethod(typeof(ChunkDataLayer), name);
            var factory = AccessTools.DeclaredMethod(typeof(ChunkFactories), nameof(ChunkFactories.ForPool));
            var code = ChunkFactories.Rewrite(PatchProcessor.GetOriginalInstructions(method));
            Assert.That(code.Count(c => c.Calls(factory)), Is.EqualTo(1), name);
        }

        var pool = (ChunkDataPool)RuntimeHelpers.GetUninitializedObject(typeof(ChunkDataPool));
        var factoryForPool = ChunkFactories.ForPool(pool);
        Assert.That(factoryForPool.Target, Is.SameAs(pool));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = ChunkFactories.ForPool(pool);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}

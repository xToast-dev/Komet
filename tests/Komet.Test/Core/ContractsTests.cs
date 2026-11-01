namespace Komet.Test.Core;

[NonParallelizable]
public sealed class ContractsTests
{
    [TearDown]
    public void Detach()
    {
        Contracts.Attach(null);
    }

    // A failure is reported once per call site, and a call site is its file too: line 25 of two files are two sites
    [Test]
    public void AFailureIsReportedOncePerFileAndLine()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        foreach (var file in (string[])["/src/GlQueryCache.cs", "/src/WindowSize.cs", "/src/GlQueryCache.cs"])
            _ = Contracts.Assert(false, 25, file);
        Assert.That(logger.Lines, Has.Count.EqualTo(2).And.Some.Contains("/src/WindowSize.cs:25"));
    }
}

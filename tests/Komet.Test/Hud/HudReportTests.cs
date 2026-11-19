using Komet.Core;

namespace Komet.Test.Hud;

// The HUD's report rows: the bench's means per row, the debug protocol's HUD section, the rows' growth over their own reads
[NonParallelizable]
public sealed class HudReportTests
{
    [TearDown]
    public void Detach() => Contracts.Attach(null);

    [TestCase(double.PositiveInfinity, "F1", "")]
    public void NumbersPrintCultureInvariantAndNothingWhenNotFinite(double value, string format, string expected) =>
        Assert.That(KometText.Format(value, format), Is.EqualTo(expected));

}

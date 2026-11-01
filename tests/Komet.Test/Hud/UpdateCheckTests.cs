using Komet.Hud;

namespace Komet.Test.Hud;

// GitHub's answer about the newest release and its checksum, as the update notice and the checksum window read it
public sealed class UpdateCheckTests
{
    private const string Build = "v1.2.3", Newer = "v1.3.0";
    private static readonly string Installed = new('a', 64), Other = new('b', 64);

    [TestCase(true, Build, "a", "a", "Verified", "aaaaaaa")]
    [TestCase(true, Build, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(true, Build, "a", "", "Unverified", Build)]
    [TestCase(true, Newer, "a", "a", "Outdated", Newer)]
    [TestCase(true, Newer, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(false, Newer, "a", "", "Outdated", Newer)]
    [TestCase(false, "", "", "", "NoRelease", Build)]
    [TestCase(false, "v1.2.2", "a", "", "NoRelease", Build)]
    [TestCase(true, "v1.2.2", "a", "a", "Verified", "aaaaaaa")]
    public void GitHubsAnswerMapsToOneState(bool listed, string newest, string installed, string published,
        string state, string detail)
    {
        var report = UpdateCheck.Judge(Build, listed, newest, Hash(installed), listed ? Hash(published) : "", "");
        Assert.Multiple(() =>
        {
            Assert.That(report.State, Is.EqualTo(Enum.Parse<UpdateState>(state)));
            Assert.That(report.Detail, Is.EqualTo(detail));
            Assert.That(report.Tag, Is.EqualTo(listed ? Build : ""));
        });
    }

    // Only a later build is an update: a pre-release is ahead of the release before it and behind its own; previews have no order
    [TestCase("v2.0.1-pre", "v2.0.0", false)]
    [TestCase("v2.0.1-pre", "v2.0.1", true)]
    [TestCase("v2.0.1", "v2.0.1-pre", false)]
    [TestCase("v2.0.9", "v2.0.10", true)]
    [TestCase("v2.0.0", "v2.0.0", false)]
    [TestCase("v2.0.0", "", false)]
    [TestCase("preview-abc1234", "preview-def5678", true)]
    public void TheNewestTagIsAnUpdateOnlyWhenItIsLater(string tag, string newest, bool behind)
    {
        Assert.That(UpdateCheck.Behind(tag, newest), Is.EqualTo(behind));
    }

    [TestCase("Checking", "v1", "a", "a", "verify-checking")]
    [TestCase("Failed", "v1", "a", "a", "verify-failed")]
    [TestCase("Verified", "v1", "a", "a", "verify-match")]
    [TestCase("Outdated", "v1", "a", "b", "verify-mismatch")]
    [TestCase("Unverified", "", "a", "", "verify-norelease")]
    [TestCase("Unverified", "v1", "", "b", "verify-nofile")]
    [TestCase("Unverified", "v1", "a", "", "verify-nochecksum")]
    public void TheChecksumWindowNamesWhatTheComparisonFound(string state, string tag, string installed,
        string published, string key)
    {
        var report = new UpdateReport(Enum.Parse<UpdateState>(state), "", tag, "", Hash(installed), Hash(published));
        Assert.That(report.Verdict(), Is.EqualTo(key));
    }

    private static string Hash(string digit) => digit switch { "" => "", "a" => Installed, _ => Other };
}

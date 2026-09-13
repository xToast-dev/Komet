using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Runtime.Diagnostics;

internal enum UpdateState { Checking, Verified, Mismatch, Outdated, Unverified, Failed }

// Asks GitHub for the newest build of this channel and compares the version (release) or the commit (preview).
// When they match, the sha256 of the installed zip is compared with the one CI published next to it.
// Runs once in the background; the HUD polls State and Detail.
internal sealed class UpdateCheck : IDisposable
{
    private const string Repo = "xtoast-dev/Komet";
    private const int MaxReleases = 30, MaxAssets = 16, TimeoutSeconds = 15, ShortHash = 7;
    [SuppressMessage("Minor Code Smell", "S1075", Justification = "the one place the release feed is addressed")]
    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repo}/releases?per_page={MaxReleases}");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    private volatile UpdateState _state = UpdateState.Checking;
    private volatile string _detail = "";

    public UpdateState State => _state;
    public string Detail => _detail;

    public UpdateCheck(ILogger logger, string version, bool preview, string commit, string sourcePath)
    {
        if (!NotNull(logger) || !Assert(version.Length > 0) || !Assert(!preview || commit.Length > 0)) { Set(UpdateState.Failed, "bad arguments"); return; }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Komet/" + version);
        _ = Task.Run(() => Run(logger, version, preview, commit, sourcePath));
    }

    private async Task Run(ILogger logger, string version, bool preview, string commit, string sourcePath)
    {
        try
        {
            var releases = JArray.Parse(await _http.GetStringAsync(Feed).ConfigureAwait(false));
            var newest = releases.Take(MaxReleases).OfType<JObject>().FirstOrDefault(r => preview ? Tag(r).StartsWith("preview-", StringComparison.Ordinal) : (bool?)r["prerelease"] == false && Tag(r).StartsWith('v'));
            if (!NotNull(newest)) { Set(UpdateState.Failed, "no release on GitHub"); return; }
            var tag = Tag(newest);
            if (tag != (preview ? "preview-" + commit : "v" + version)) { Set(UpdateState.Outdated, tag); return; }
            var checksum = (string?)newest["assets"]?.Take(MaxAssets).FirstOrDefault(a => (string?)a["name"] == "komet.sha256")?["browser_download_url"];
            if (checksum is null || !File.Exists(sourcePath)) { Set(UpdateState.Unverified, ""); return; }
            var published = (await _http.GetStringAsync(new Uri(checksum)).ConfigureAwait(false)).Split(' ', 2)[0];
            var stream = File.OpenRead(sourcePath);
            await using (stream.ConfigureAwait(false))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                Set(Assert(published.Length == actual.Length) && actual == published ? UpdateState.Verified : UpdateState.Mismatch, actual[..ShortHash]);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or ObjectDisposedException)
        {
            Set(UpdateState.Failed, e.Message);
        }
        logger.Notification("Komet update check: {0} {1}", _state, _detail);
    }

    private static string Tag(JObject release) => NotNull(release) ? (string?)release["tag_name"] ?? "" : "";

    private void Set(UpdateState state, string detail)
    {
        if (!NotNull(detail) || !Assert(state != UpdateState.Checking)) return;
        (_state, _detail) = (state, detail);
    }

    public void Dispose() => _http.Dispose();
}

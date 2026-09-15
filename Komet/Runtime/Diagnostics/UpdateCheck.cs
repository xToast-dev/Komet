using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Komet.Runtime.UI.Overlay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Runtime.Diagnostics;

internal enum UpdateState { Checking, Verified, Mismatch, Outdated, Unverified, Failed }

// Everything one check found out, replaced as a whole so readers on the render thread never see a half-written result.
// Tag: the release this build belongs to ("" when GitHub has none). Newest: the newest build of the channel.
// Installed / Published: full sha256 hex of the installed zip and of the file CI published with the release ("" when unknown).
// Released: when GitHub published the release of this build, as local time text ("" when unknown).
internal sealed record UpdateReport(UpdateState State, string Detail, string Tag, string Newest, string Installed, string Published, string Released)
{
    public static readonly UpdateReport Pending = new(UpdateState.Checking, "", "", "", "", "", "");
    public bool Match => Installed.Length > 0 && Installed == Published;
}

// Asks GitHub for the releases and compares this build (release: version, preview: commit) with the one of the same tag
// and with the newest of its channel. Runs in the background; the HUD and the checksum window poll Report.
internal sealed class UpdateCheck : IDisposable
{
    private const string Repo = "xtoast-dev/Komet";
    private const int MaxReleases = 30, MaxAssets = 16, TimeoutSeconds = 15, ShortHash = 7;
    [SuppressMessage("Minor Code Smell", "S1075", Justification = "the one place the release feed is addressed")]
    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repo}/releases?per_page={MaxReleases}");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    private readonly ILogger _logger;
    private readonly string _tag, _sourcePath;
    private readonly bool _preview;
    private volatile UpdateReport _report = UpdateReport.Pending;
    private int _running;

    public UpdateReport Report => _report;
    public string FileName => Path.GetFileName(_sourcePath);

    public UpdateCheck(ILogger logger, string version, bool preview, string commit, string sourcePath)
    {
        _logger = logger;
        (_preview, _sourcePath) = (preview, sourcePath);
        _tag = preview ? "preview-" + commit : "v" + version;
        if (!NotNull(logger) || !Assert(version.Length > 0) || !Assert(!preview || commit.Length > 0)) { _report = UpdateReport.Pending with { State = UpdateState.Failed, Detail = "bad arguments" }; return; }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Komet/" + version);
        Start();
    }

    // One run at a time; a click on "check again" while one is running is ignored.
    public void Start()
    {
        if (!Assert(_tag.Length > 1) || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _report = UpdateReport.Pending;
        _ = Task.Run(Run);
    }

    private async Task Run()
    {
        try
        {
            // GitHub orders the feed by the tagged commit's date, which rebases and cherry-picks scramble; the newest build is the one published last.
            var releases = JArray.Parse(await _http.GetStringAsync(Feed).ConfigureAwait(false)).Take(MaxReleases).OfType<JObject>()
                .OrderByDescending(r => (string?)r["published_at"] ?? "", StringComparer.Ordinal).ToList();
            if (!Assert(releases.Count <= MaxReleases) || !Assert(_tag.Length > 1)) { _report = UpdateReport.Pending with { State = UpdateState.Failed, Detail = "bad feed" }; return; }
            var same = releases.Find(r => Tag(r) == _tag);
            var newest = releases.Find(r => _preview ? Tag(r).StartsWith("preview-", StringComparison.Ordinal) : (bool?)r["prerelease"] == false && Tag(r).StartsWith('v'));
            var installed = File.Exists(_sourcePath) ? await Hash(_sourcePath).ConfigureAwait(false) : "";
            var published = same is null ? "" : await Published(same).ConfigureAwait(false);
            var released = same is null ? "" : LocalTime((string?)same["published_at"] ?? "");
            var report = new UpdateReport(UpdateState.Unverified, _tag, same is null ? "" : _tag, newest is null ? "" : Tag(newest), installed, published, released);
            if (same is null && newest is null) report = report with { State = UpdateState.Failed, Detail = "no release on GitHub" };
            else if (installed.Length > 0 && published.Length > 0) report = report with { State = report.Match ? UpdateState.Verified : UpdateState.Mismatch, Detail = installed[..ShortHash] };
            if (report.State != UpdateState.Mismatch && newest is not null && Tag(newest) != _tag) report = report with { State = UpdateState.Outdated, Detail = Tag(newest) };
            _report = report;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or ObjectDisposedException or InvalidCastException)
        {
            _report = UpdateReport.Pending with { State = UpdateState.Failed, Detail = e.Message };
        }
        finally
        {
            _logger.Notification("Komet update check: {0} {1}", _report.State, _report.Detail);
            _running = 0;
        }
    }

    private async Task<string> Published(JObject release)
    {
        var url = (string?)release["assets"]?.Take(MaxAssets).FirstOrDefault(a => (string?)a["name"] == "komet.sha256")?["browser_download_url"];
        if (url is null || !Assert(url.StartsWith("https://", StringComparison.Ordinal))) return "";
        var line = (await _http.GetStringAsync(new Uri(url)).ConfigureAwait(false)).Split(' ', 2)[0].Trim();
        return Assert(line.Length == SHA256.HashSizeInBytes * 2) ? line : "";
    }

    private static async Task<string> Hash(string path)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            return Assert(hash.Length == SHA256.HashSizeInBytes * 2) && Assert(path.Length > 0) ? hash : "";
        }
    }

    // ISO 8601 UTC (what CI stamps and what GitHub reports) as local time in the language's format, minute precision; "" when absent or unreadable.
    public static string LocalTime(string iso)
    {
        if (iso.Length == 0 || !Assert(iso.Length <= 40)) return "";
        var format = HudSettings.Translate("time-format");
        var ok = DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time);
        return ok && Assert(time.Year > 2000) && Assert(format.Length > 0) ? time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture) : "";
    }

    private static string Tag(JObject release) => NotNull(release) ? (string?)release["tag_name"] ?? "" : "";

    public void Dispose() => _http.Dispose();
}

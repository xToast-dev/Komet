using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Hud;

internal enum UpdateState
{
    Checking,
    Verified,
    Mismatch,
    Outdated,
    Unverified,
    Failed,
    // GitHub answered, with no release of this build and none of its channel: nothing to compare with, and nothing failed
    NoRelease
}

// Everything one check found out, replaced as a whole so readers on the render thread never see a half-written result.
// Tag: the release this build belongs to ("" when GitHub has none). Newest: the newest build of the channel.
// Installed / Published: full sha256 hex of the installed zip and of the file CI published with the release ("" when unknown).
// Released: when GitHub published the release of this build, as local time text ("" when unknown).
internal sealed record UpdateReport(UpdateState State, string Detail = "", string Tag = "", string Newest = "",
    string Installed = "", string Published = "", string Released = "")
{
    public const int HashLength = SHA256.HashSizeInBytes * 2; // hex digits
    public static readonly UpdateReport Pending = new(UpdateState.Checking);
    public bool Match => Installed.Length > 0 && Installed == Published;

    // The HUD's update line under the title: a lang key and its colour
    public (string Key, Rgba? Color) Notice() => State switch
    {
        UpdateState.Checking => ("hud-update-checking", null),
        UpdateState.Verified => ("hud-update-verified", HudCanvas.Good),
        UpdateState.Mismatch => ("hud-update-mismatch", HudCanvas.Error),
        UpdateState.Outdated => ("hud-update-outdated", HudCanvas.Warning),
        UpdateState.Unverified => ("hud-update-unverified", null),
        UpdateState.Failed => ("hud-update-failed", null),
        _ => ("hud-update-norelease", null)
    };

    // What the checksum window's comparison says: a lang key and a colour
    public (string Key, Rgba Color) Verdict() => State switch
    {
        UpdateState.Checking => ("verify-checking", HudCanvas.Neutral),
        UpdateState.Failed => ("verify-failed", HudCanvas.Neutral),
        UpdateState.NoRelease => ("verify-norelease", HudCanvas.Warning),
        _ when Match => ("verify-match", HudCanvas.Good),
        _ when Installed.Length > 0 && Published.Length > 0 => ("verify-mismatch", HudCanvas.Error),
        _ when Tag.Length == 0 => ("verify-norelease", HudCanvas.Warning),
        _ when Installed.Length == 0 => ("verify-nofile", HudCanvas.Neutral),
        _ => ("verify-nochecksum", HudCanvas.Warning)
    };
}

// Asks GitHub for the releases and compares this build (release: version, preview: commit) with the one of the same tag
// and with the newest of its channel. Runs in the background; the HUD and the checksum window poll Report.
internal sealed class UpdateCheck : IDisposable
{
    private const string Repo = "xtoast-dev/Komet";
    private const int MaxReleases = 30, MaxAssets = 16, TimeoutSeconds = 15, ShortHash = 7;

    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repo}/releases?per_page={MaxReleases}");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    private readonly ILogger _logger;
    private readonly bool _preview;
    private readonly string _sourcePath;
    private volatile UpdateReport _report = UpdateReport.Pending;
    private int _running;

    public UpdateCheck(ILogger logger, string version, bool preview, string commit, string sourcePath)
    {
        _logger = logger;
        (_preview, _sourcePath) = (preview, sourcePath);
        BuildTag = preview ? "preview-" + commit : "v" + version;
        if (!NotNull(logger) || !Assert(version.Length > 0) || !Assert(!preview || commit.Length > 0))
        {
            _report = new UpdateReport(UpdateState.Failed, "bad arguments");
            return;
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Komet/" + version);
        Start();
    }

    public UpdateReport Report => _report;
    public string FileName => Path.GetFileName(_sourcePath);
    public string BuildTag { get; }

    public void Dispose() => _http.Dispose();

    // One run at a time; a click on "check again" while one is running is ignored.
    public void Start()
    {
        if (!Assert(BuildTag.Length > 1) || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _report = UpdateReport.Pending;
        _ = Task.Run(Run).ContinueWith(
            failed => _logger.Warning("Komet update check: {0}", failed.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task Run()
    {
        try
        {
            // GitHub orders the feed by the tagged commit's date, which rebases and cherry-picks scramble; the newest build is the one
            // published last.
            var releases = JArray.Parse(await _http.GetStringAsync(Feed).ConfigureAwait(false)).Take(MaxReleases)
                .OfType<JObject>()
                .OrderByDescending(r => (string?)r["published_at"] ?? "", StringComparer.Ordinal).ToList();
            if (!Assert(releases.Count <= MaxReleases) || !Assert(BuildTag.Length > 1))
            {
                _report = new UpdateReport(UpdateState.Failed, "bad feed");
                return;
            }

            var same = releases.Find(r => Tag(r) == BuildTag);
            var newest = releases.Find(r =>
                _preview
                    ? Tag(r).StartsWith("preview-", StringComparison.Ordinal)
                    : (bool?)r["prerelease"] == false && Tag(r).StartsWith('v'));
            var installed = File.Exists(_sourcePath) ? await Hash(_sourcePath).ConfigureAwait(false) : "";
            var published = same is null ? "" : await Published(same).ConfigureAwait(false);
            var released = same is null ? "" : HudText.LocalTime((string?)same["published_at"] ?? "");
            _report = Judge(BuildTag, same is not null, newest is null ? "" : Tag(newest), installed, published,
                released);
        }
        // Newtonsoft's explicit casts throw ArgumentException on an unexpected token, new Uri a FormatException
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or JsonException or InvalidOperationException or InvalidCastException
                                      or ArgumentException or FormatException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            _report = new UpdateReport(UpdateState.Failed, e.Message);
        }
        finally
        {
            if (_report.State == UpdateState.Checking)
                _report = new UpdateReport(UpdateState.Failed, "unexpected error"); // logged by Start
            _logger.Notification("Komet update check: {0} {1}", _report.State, _report.Detail);
            Volatile.Write(ref _running, 0);
        }
    }

    // What GitHub's answer means for this build. listed: GitHub has the release tagged like this build; newest: the newest tag of the
    // build's channel, "" when it has none. A feed without either is still an answer: a build not published (a local or CI build
    // ahead of every release, or a fork) is NoRelease, not a failed check.
    internal static UpdateReport Judge(string tag, bool listed, string newest, string installed, string published,
        string released)
    {
        if (!Assert(tag.Length > 1) || !Assert(listed || published.Length == 0))
            return new UpdateReport(UpdateState.Failed, "bad arguments");
        var report = new UpdateReport(UpdateState.Unverified, tag, listed ? tag : "", newest, installed, published,
            released);
        // Detail is the build's tag, which the notice names
        if (!listed && newest.Length == 0) report = report with { State = UpdateState.NoRelease };
        else if (installed.Length > 0 && published.Length > 0)
            report = report with
            {
                State = report.Match ? UpdateState.Verified : UpdateState.Mismatch,
                Detail = installed[..Math.Min(ShortHash, installed.Length)]
            };
        if (report.State != UpdateState.Mismatch && newest.Length > 0 && newest != tag)
            report = report with { State = UpdateState.Outdated, Detail = newest };
        return report;
    }

    private async Task<string> Published(JObject release)
    {
        var asset = release["assets"]?.Take(MaxAssets).FirstOrDefault(a => (string?)a["name"] == "komet.sha256");
        var url = (string?)asset?["browser_download_url"];
        if (url is null || !Assert(url.StartsWith("https://", StringComparison.Ordinal))) return "";
        var line = (await _http.GetStringAsync(new Uri(url)).ConfigureAwait(false)).Split(' ', 2)[0].Trim();
        return Assert(line.Length == UpdateReport.HashLength) ? line : "";
    }

    private static async Task<string> Hash(string path)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            return Assert(hash.Length == UpdateReport.HashLength) && Assert(path.Length > 0) ? hash : "";
        }
    }

    private static string Tag(JObject release) => NotNull(release) ? (string?)release["tag_name"] ?? "" : "";
}

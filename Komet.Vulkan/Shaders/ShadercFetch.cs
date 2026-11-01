using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Komet.Vulkan;

// Vulkan-only compiles the game's shaders with shaderc. Where the system has none (Windows always, many Linux systems) it is
// fetched once from nuget.org (Silk.NET.Shaderc.Native, Apache-2.0), checked against the hash pinned here, and only this
// system's library is kept in the game's cache. Until it loads, Vulkan-only waits and OpenGL draws; the player is told.
internal static class ShadercFetch
{
    private const string Package = "Silk.NET.Shaderc.Native 2.23.0";
    private const string Url =
        "https://api.nuget.org/v3-flatcontainer/silk.net.shaderc.native/2.23.0/silk.net.shaderc.native.2.23.0.nupkg";

    // nuget.org's own hash of the package (catalog packageHash, SHA-512) matched this file's on 2026-10-15
    private const string Sha256 = "50f67d5ebaefe97bd55f9b0eed4044f57c925675d55100d35a51718ccab948be";
    private const int MaxBytes = 64 << 20, TimeoutSeconds = 300, PackageMb = 21;

    private enum Phase { Unknown, Ready, Fetching, Fetched, Failed }

    private static volatile Phase _phase;
    private static volatile int _percent;
    private static volatile string _failure = "";
    private static volatile Phase _told; // the phase the player heard of: a phase published while it is told is told next

    public static string Folder => Path.Join(GamePaths.Cache, "komet-shaderc", "2.23.0");

    public static ILogger? Logger { get; set; }

    // What Vulkan-only waits for, for the HUD; empty once shaderc loads
    public static string Status => _phase switch
    {
        Phase.Fetching => Text("vk-shaderc-status-fetching", _percent),
        Phase.Fetched => Text("vk-shaderc-status-fetched"),
        Phase.Failed => Text("vk-shaderc-status-failed", _failure),
        _ => ""
    };

    // A world joined: a fetched library loads now, a failed fetch is tried again. The shaders of a world are ported at its
    // start, so a library fetched during play waits for the next join.
    public static void Joined(ILogger? logger)
    {
        Logger = logger;
        if (_phase is Phase.Fetched or Phase.Failed) (_phase, _told) = (Phase.Unknown, Phase.Unknown);
    }

    // Render thread, each frame Vulkan-only waits: true once shaderc loads; the first miss starts the fetch, and every
    // change is told the player once in the chat
    public static bool Ready(ICoreClientAPI? api)
    {
        var phase = _phase; // read once: Fetch may publish meanwhile
        if (phase == Phase.Ready) return true;
        if (phase == Phase.Unknown)
        {
            Shaderc.Folder = Folder;
            if (Shaderc.Load())
            {
                _phase = Phase.Ready;
                return true;
            }

            phase = Phase.Fetching;
            _phase = phase;
            _ = Task.Run(Fetch);
        }

        if (_told == phase) return false;
        var key = phase switch { Phase.Fetching => "vk-shaderc-fetching", Phase.Fetched => "vk-shaderc-fetched", _ => "vk-shaderc-failed" };
        Tell(api, phase, Text(key, phase == Phase.Fetching ? PackageMb : _failure));
        return false;
    }

    private static void Tell(ICoreClientAPI? api, Phase phase, string text)
    {
        _told = phase;
        if (!Assert(text.Length > 0)) return;
        Logger?.Warning("{0}", text); // the text names Komet itself (it is shown in chat too)
        api?.ShowChatMessage(text);
    }

    private static async Task Fetch()
    {
        try
        {
            if (Library() is not var (rid, file)) throw new PlatformNotSupportedException(RuntimeInformation.RuntimeIdentifier);
            // HttpClient.Timeout ends with the headers (ResponseHeadersRead): a stalled body would leave Fetching for good
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
            using var response = await http.GetAsync(new Uri(Url), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > MaxBytes) throw new InvalidDataException($"{length} bytes, more than expected");
            var bytes = await Read(response, length, timeout.Token).ConfigureAwait(false);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("the package's hash is not the one pinned in Komet");
            Unpack(bytes, $"runtimes/{rid}/native/{file}", Path.Join(Folder, file));
            Logger?.Notification("Komet: shaderc from {0} (Apache-2.0, https://github.com/google/shaderc) is in {1}", Package, Folder);
            _phase = Phase.Fetched;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or
                                       UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Failed(e.Message);
        }
    }

    private static void Failed(string why)
    {
        (_failure, _phase) = (why.Length > 0 ? why : "?", Phase.Failed);
        Logger?.Warning("Komet: shaderc could not be fetched: {0}", _failure);
    }

    private static async Task<byte[]> Read(HttpResponseMessage response, long length, CancellationToken cancel)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var closing = stream.ConfigureAwait(false);
        using var into = new MemoryStream(length is > 0 and <= MaxBytes ? (int)length : 1 << 20);
        var buffer = new byte[1 << 16];
        for (var steps = 0; steps < MaxBytes / 1024; steps++)
        {
            var read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;
            await into.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            if (into.Length > MaxBytes) throw new InvalidDataException("the package is larger than expected");
            if (length > 0) _percent = (int)(100 * into.Length / length);
        }

        return into.ToArray();
    }

    // Only this system's library, written whole before it takes its name
    internal static void Unpack(byte[] package, string entry, string path)
    {
        if (!NotNull(package) || !Assert(entry.Length > 0)) return;
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var found = zip.GetEntry(entry) ?? throw new InvalidDataException($"the package has no {entry}");
        if (!Assert(found.Length <= MaxBytes)) throw new InvalidDataException($"{entry} is larger than expected");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        found.ExtractToFile(part, overwrite: true);
        File.Move(part, path, overwrite: true);
    }

    // The package's runtime folder and file for this process
    internal static (string Rid, string File)? Library()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (!arm && !x64) return null;
        var cpu = arm ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return ($"win-{cpu}", "shaderc_shared.dll");
        if (OperatingSystem.IsLinux()) return ($"linux-{cpu}", "libshaderc_shared.so");
        return OperatingSystem.IsMacOS() ? ($"osx-{cpu}", "libshaderc_shared.dylib") : null;
    }

    private static string Text(string key, params object[] args)
    {
        var full = "komet:" + key;
        var text = Lang.Get(full, args);
        return text != full ? text : key;
    }
}

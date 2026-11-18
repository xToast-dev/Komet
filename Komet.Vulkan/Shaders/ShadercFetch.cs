using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Komet.Vulkan;

// Vulkan-only compiles the game's shaders with shaderc. The mod ships it for Windows x64 (native/win-x64). Where neither the
// mod nor the system has one (other Windows CPUs, many Linux systems) it is fetched once from nuget.org (Silk.NET.Shaderc.Native, Apache-2.0), checked against the hash pinned here, and only this
// system's library is kept in the game's cache. Until it loads, Vulkan-only waits and OpenGL draws; the player is told.
internal static class ShadercFetch
{
    private const string Package = "Silk.NET.Shaderc.Native 2.23.0";

    private const string Url =
        "https://api.nuget.org/v3-flatcontainer/silk.net.shaderc.native/2.23.0/silk.net.shaderc.native.2.23.0.nupkg";

    // nuget.org's own hash of the package (catalog packageHash, SHA-512) matched this file's on 2026-10-15
    private const string Sha256 = "50f67d5ebaefe97bd55f9b0eed4044f57c925675d55100d35a51718ccab948be";
    private const int MaxBytes = 64 << 20, TimeoutSeconds = 300, PackageMb = 21;

    private static volatile Phase _phase;
    private static volatile int _percent;
    private static volatile string _failure = "";

    private static volatile Phase
        _told; // the phase the player heard of: a phase published while it is told is told next

    private static bool _missed; // Loadable found none this join: the early ports leave it to Ready's fetch
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

    private static string Folder =>
        Path.Join(GamePaths.Cache, "komet-shaderc", "2.23.0", Library()?.Rid ?? "unsupported");

    private static ILogger? Logger { get; set; }

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
    public static void Joined(ILogger? logger, string source = "")
    {
        Logger = logger;
        Shaderc.SourceFolder = "";
        Shaderc.Folder = Folder; // ports begin as the world loads, before Ready: they must find the fetched library too
        _missed = false;
        if (_phase is Phase.Fetched or Phase.Failed) (_phase, _told) = (Phase.Unknown, Phase.Unknown);

        if (!Shaderc.Load() && source.Length > 0)
        {
            try
            {
                _ = Bundled(source, Folder);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger?.Warning("Komet: bundled shaderc could not be prepared from {0}: {1}", source, e);
            }
        }
    }

    // The downloader and native compiler are process-owned; neither keeps a closed world's API or logger.
    public static void Leave() => Logger = null;

    // Main thread, as the world loads: whether a port may begin now. A port begun without shaderc fails for the whole join,
    // so the early ones wait for Ready instead; one miss per join, the fetch is Ready's
    public static bool Loadable()
    {
        if (_phase == Phase.Ready) return true;

        if (_phase != Phase.Unknown || _missed) return false;

        Shaderc.Folder = Folder;
        if (Shaderc.Load())
        {
            _phase = Phase.Ready;
        }
        else
        {
            _missed = true;
        }

        return !_missed;
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
            Logger?.Warning("Komet: no usable shaderc for {0}: {1}", RuntimeInformation.RuntimeIdentifier,
                Shaderc.Failure);
            _ = Task.Run(Fetch);
        }

        if (_told == phase) return false;

        var key = phase switch
        {
            Phase.Fetching => "vk-shaderc-fetching", Phase.Fetched => "vk-shaderc-fetched", _ => "vk-shaderc-failed"
        };
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
            if (Library() is not var (rid, file))
            {
                throw new PlatformNotSupportedException(RuntimeInformation.RuntimeIdentifier);
            }
            // HttpClient.Timeout ends with the headers (ResponseHeadersRead): a stalled body would leave Fetching for good
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var response = await Http
                .GetAsync(new Uri(Url), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > MaxBytes) throw new InvalidDataException($"{length} bytes, more than expected");

            var bytes = await Read(response, length, timeout.Token).ConfigureAwait(false);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("the package's hash is not the one pinned in Komet");
            }

            Unpack(bytes, $"runtimes/{rid}/native/{file}", Path.Join(Folder, file));
            Logger?.Notification("Komet: shaderc from {0} (Apache-2.0, https://github.com/google/shaderc) is in {1}",
                Package, Folder);
            _phase = Phase.Fetched;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or InvalidDataException or
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
        Extract(zip, entry, path);
    }

    internal static bool Bundled(string source, string folder)
    {
        if (Library() is not { } library) return false;

        var entry = $"native/{library.Rid}/{library.File}";
        if (Directory.Exists(source))
        {
            Shaderc.SourceFolder = Path.Join(source, "native", library.Rid);
            return File.Exists(Path.Join(Shaderc.SourceFolder, library.File));
        }

        if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;

        using var zip = ZipFile.OpenRead(source);
        if (zip.GetEntry(entry) is null) return false;

        Extract(zip, entry, Path.Join(folder, library.File));
        return true;
    }

    private static void Extract(ZipArchive zip, string entry, string path)
    {
        var found = zip.GetEntry(entry) ?? throw new InvalidDataException($"the package has no {entry}");
        if (!Assert(found.Length <= MaxBytes)) throw new InvalidDataException($"{entry} is larger than expected");

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        try
        {
            found.ExtractToFile(part, true);
            File.Move(part, path, true);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
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

    private enum Phase
    {
        Unknown,
        Ready,
        Fetching,
        Fetched,
        Failed
    }
}

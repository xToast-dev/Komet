using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Komet.Vulkan;

// So a program the engine built before costs a file read instead of shaderc and the driver's compiler on the render thread. Ports
// go in a folder of this Komet build, as another build ports anew. The hot uniforms the scene settled on are kept per program, so
// its first port already pushes them and neither a second port nor a second set of pipelines follows. Ports and hot uniforms are
// written off the render thread, one after the other. Ports are made on the builders' threads (Builds), so the state they share
// (the settled hot uniforms, the counts, the chain of writes) is guarded by State.
internal static class ShaderCache
{
    private const int MaxPorts = 1024, MaxSettled = 1024, MaxFile = 64 << 20, KeyBytes = 16, MaxParts = 4;
    private const string Format = "1", HotFile = "hot.json", PortsFolder = "ports";

    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals // NaN or infinite uniform defaults
    };

    private static readonly Lock Writing = new(), State = new();
    private static readonly string Build =
        typeof(ShaderCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private static Dictionary<string, string[]>? _settled;
    private static Task _writes = Task.CompletedTask;
    private static int _kept;

    public static bool Enabled { get; set; } = true;

    // Where the files go; "" until Install, so tests never touch the game's folders
    public static string Folder { get; private set; } = "";
    public static bool Active => Enabled && Folder.Length > 0;

    public static int Loaded { get; private set; }
    public static long PipelineBytes { get; set; }

    public static void Install(ILogger logger)
    {
        if (!NotNull(logger)) return;
        Open(Path.Join(Vintagestory.API.Config.GamePaths.Cache, "komet-vulkan"));
        var folder = Folder;
        lock (State) _writes = _writes.ContinueWith(_ => Dropped(folder, logger), TaskScheduler.Default);
        _ = Assert(Active || !Enabled);
    }

    // The other builds' ports: never read again
    private static void Dropped(string folder, ILogger logger)
    {
        var ports = Path.Join(folder, PortsFolder);
        try
        {
            if (!Assert(folder.Length > 0) || !NotNull(logger) || !Directory.Exists(ports)) return;
            foreach (var old in Directory.GetDirectories(ports).Bounded(MaxPorts))
                if (Path.GetFileName(old) != Build)
                    Directory.Delete(old, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Warning("Komet: the old Vulkan shader cache stays: {0}", e.Message);
        }
    }

    public static void Open(string folder)
    {
        if (!NotNull(folder)) return;
        lock (State) (Folder, _settled, Loaded, _kept, PipelineBytes) = (folder, null, 0, 0, 0);
        _ = Assert(Folder == folder);
    }

    public static string Sources(string vertex, string fragment, IReadOnlyDictionary<string, int> attributes)
    {
        if (!NotNull(vertex) || !NotNull(fragment) || !NotNull(attributes)) return "";
        var bound = string.Join(";", attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => $"{a.Key}={a.Value}"));
        return Hash(vertex, fragment, bound);
    }

    public static GlslPort.Ported? Port(string sources, IReadOnlySet<string>? hot, string name)
    {
        if (!Active || !Assert(sources.Length > 0) || !Assert(name.Length > 0)) return null;
        var path = PortPath(sources, hot);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFile) return null;
            var ported = JsonSerializer.Deserialize<GlslPort.Ported>(File.ReadAllBytes(path), Json);
            if (ported is not // every part there and SPIR-V in both stages
                {
                    Vertex.Length: > 5, Fragment.Length: > 5, VertexUniforms: not null, FragmentUniforms: not null,
                    HotUniforms: not null, Samplers: not null, Blocks: not null, Attributes: not null, Outputs: not null
                } || ported.Vertex[0] != Spirv.Magic || ported.Fragment[0] != Spirv.Magic) return null;
            _ = Assert(ported.Vertex.Length < MaxFile / 4 && ported.Fragment.Length < MaxFile / 4);
            lock (State) Loaded++;
            return ported with { Name = name };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            return null; // ported anew, and written over
        }
    }

    public static void Keep(string sources, IReadOnlySet<string>? hot, GlslPort.Ported ported)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(ported)) return;
        var path = PortPath(sources, hot);
        lock (State)
        {
            if (_kept >= MaxPorts) return;
            _kept++;
            _writes = _writes.ContinueWith(_ => Write(path, JsonSerializer.SerializeToUtf8Bytes(ported, Json)),
                TaskScheduler.Default);
        }
    }

    public static IReadOnlySet<string>? Settled(string sources)
    {
        if (!Active || !NotNull(sources) || sources.Length == 0) return null;
        lock (State)
        {
            var settled = _settled ??= ReadSettled();
            _ = Assert(settled.Count <= MaxSettled);
            return settled.TryGetValue(sources, out var hot) && hot.Length > 0
                ? new HashSet<string>(hot, StringComparer.Ordinal)
                : null;
        }
    }

    public static void Settle(string sources, IReadOnlySet<string> hot)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(hot) || hot.Count == 0) return;
        string[] names = [.. hot.Order(StringComparer.Ordinal)];
        lock (State)
        {
            var settled = _settled ??= ReadSettled();
            if (settled.TryGetValue(sources, out var known) && known.SequenceEqual(names)) return;
            if (settled.Count >= MaxSettled && !settled.ContainsKey(sources)) return;
            settled[sources] = names;
            var (path, bytes) = (Path.Join(Folder, HotFile), JsonSerializer.SerializeToUtf8Bytes(settled, Json));
            _writes = _writes.ContinueWith(_ => Write(path, bytes), TaskScheduler.Default);
        }
    }

    // The device checks the header against itself before it hands it to the driver
    public static byte[]? Pipelines(uint vendor, uint model)
    {
        if (!Active) return null;
        var path = PipelinePath(vendor, model);
        try
        {
            var data = File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxFile
                ? File.ReadAllBytes(path)
                : null;
            return Assert(data is null || data.Length <= MaxFile) ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The device's pipeline cache as it goes; written at once, as the process may end right after
    public static void KeepPipelines(uint vendor, uint model, byte[] data)
    {
        if (!Active || !NotNull(data) || data.Length == 0 || !Assert(data.Length <= MaxFile)) return;
        Write(PipelinePath(vendor, model), data);
    }

    public static string Report()
    {
        if (!Active) return "";
        int read, kept;
        lock (State) (read, kept) = (Loaded, _kept);
        _ = Assert(read >= 0) && Assert(kept <= MaxPorts);
        return $"; shader cache: {read} ports read, {kept} written, pipeline cache {PipelineBytes >> 10} KB at the start";
    }

    private static Dictionary<string, string[]> ReadSettled()
    {
        var path = Path.Join(Folder, HotFile);
        var read = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            if (Assert(Folder.Length > 0) && File.Exists(path) && new FileInfo(path).Length <= MaxFile &&
                JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllBytes(path), Json) is { } file)
                read = new Dictionary<string, string[]>(file.Where(e => e.Value is not null).Take(MaxSettled),
                    StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            // started anew
        }

        _ = Assert(read.Count <= MaxSettled);
        return read;
    }

    // Written to a file beside it and moved over it: a reader never sees half a file
    private static void Write(string path, byte[] bytes)
    {
        if (!Assert(path.Length > 0) || !NotNull(bytes)) return;
        lock (Writing)
        {
            var part = path + ".part";
            try
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Folder);
                File.WriteAllBytes(part, bytes);
                File.Move(part, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // kept next time
            }
        }
    }

    private static string PortPath(string sources, IReadOnlySet<string>? hot)
    {
        _ = Assert(Folder.Length > 0) && Assert(sources.Length == 2 * KeyBytes);
        var hotNames = hot is null ? "" : string.Join(";", hot.Order(StringComparer.Ordinal));
        return Path.Join(Folder, PortsFolder, Build, Hash(Format, sources, hotNames) + ".json");
    }

    private static string PipelinePath(uint vendor, uint model)
    {
        _ = Assert(Folder.Length > 0) && Assert(Build.Length > 0);
        return Path.Join(Folder, $"pipelines-{vendor:x4}-{model:x4}.bin");
    }

    private static string Hash(params ReadOnlySpan<string> parts)
    {
        if (!Assert(parts.Length <= MaxParts)) return "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < Math.Min(parts.Length, MaxParts); i++)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(parts[i]));
            hash.AppendData([0]);
        }

        var key = Convert.ToHexStringLower(hash.GetHashAndReset(), 0, KeyBytes);
        return Assert(key.Length == 2 * KeyBytes) ? key : "";
    }
}

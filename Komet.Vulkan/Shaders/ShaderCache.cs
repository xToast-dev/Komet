using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Komet.Vulkan;

// Built programs cost a file read instead of shaderc plus driver compile. A folder per Komet build. Settled hot
// uniforms are kept per program, so the first port already pushes them. Writes run off the render thread, one at a
// time; ports build on builder threads, so shared state is guarded by State.
internal static class ShaderCache
{
    private const int MaxPorts = 1024, MaxSettled = 1024, MaxFile = 64 << 20, KeyBytes = 16, MaxParts = 4;
    // Part of every port's key: a new format ports every program anew once (2p: SPIR-V as base64, point sizes written)
    private const string Format = "2p", HotFile = "hot.json", PortsFolder = "ports", RecipesFile = "pipelines.json";
    private const int MaxRecipes = 256, MaxRecipePrograms = 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals // NaN or infinite uniform defaults
    };

    private static readonly Lock Writing = new(), State = new();
    private static readonly string Build =
        typeof(ShaderCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private static Dictionary<string, string[]>? _settled;
    private static Dictionary<string, List<Recipe>>? _recipes;
    // Pipeline recipes the mod ships (assets/komet/config/pipelines.json, renewed by scripts/seed-pipelines.sh): a first start
    // warms those pipelines off the engine's thread instead of making each as a draw first needs it
    private static Dictionary<string, List<Recipe>>? _seed;

    private static bool _recipesQueued;
    private static Task _writes = Task.CompletedTask;
    private static int _kept;

    // A pipeline a port drew with, keyed by the program's sources, so a later start builds it before the first draw.
    // Fixed: see PipelineRecipes.
    public sealed record Recipe(string Inputs, string Target, uint Topology, uint[] Colors, uint Depth, uint[] Fixed,
        long[] Attributes);

    public static bool Enabled { get; set; } = true;

    // "" until Install, so tests never touch the game's folders
    public static string Folder { get; private set; } = "";
    public static bool Active => Enabled && Folder.Length > 0;

    public static int Loaded { get; private set; }
    public static long PipelineBytes { get; set; }

    public static void Install(ILogger logger, ICoreClientAPI? api = null)
    {
        if (!NotNull(logger)) return;
        Open(Path.Join(Vintagestory.API.Config.GamePaths.Cache, "komet-vulkan"));
        Seeded(api);
        var folder = Folder;
        lock (State) _writes = _writes.ContinueWith(_ => Dropped(folder, logger), TaskScheduler.Default);
    }

    private static void Seeded(ICoreClientAPI? api)
    {
        _seed = null;
        if (api?.Assets is not { } assets) return;
        try
        {
            var text = assets.TryGet(new AssetLocation("komet", "config/pipelines.json"))?.ToText();
            if (text is null || text.Length > MaxFile) return;
            if (JsonSerializer.Deserialize<Dictionary<string, List<Recipe>>>(text, Json) is { } read)
                lock (State) _seed = read;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            // no seed: recipes come as they are made
        }
    }

    private static void Dropped(string folder, ILogger logger)
    {
        var ports = Path.Join(folder, PortsFolder);
        try
        {
            if (!Directory.Exists(ports)) return;
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
        lock (State)
        {
            (Folder, _settled, _recipes, Loaded, _kept, PipelineBytes) = (folder, null, null, 0, 0, 0);
            _recipesQueued = false; // a write still queued keeps its own folder's recipes; the new ones queue their own
        }
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
        var ported = ReadJson<GlslPort.Ported>(PortPath(sources, hot)); // unreadable: ported anew, and written over
        if (ported is not // every part there and SPIR-V in both stages
            {
                Vertex.Length: > 5, Fragment.Length: > 5, VertexUniforms: not null, FragmentUniforms: not null,
                HotUniforms: not null, Samplers: not null, Blocks: not null, Attributes: not null, Outputs: not null
            } || ported.Vertex[0] != Spirv.Magic || ported.Fragment[0] != Spirv.Magic) return null;
        lock (State) Loaded++;
        return ported with { Name = name };
    }

    // A cache file read whole; null when missing, too large or unreadable (the cache starts that part anew)
    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxFile
                ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            return null;
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

    public static Recipe[] Recipes(string sources)
    {
        if (!Active || !NotNull(sources) || sources.Length == 0) return [];
        lock (State)
        {
            var recipes = _recipes ??= ReadRecipes();
            return recipes.TryGetValue(sources, out var known) ? [.. known] : [];
        }
    }

    // Written off-thread, one at a time: a burst of new pipelines writes the file once or twice
    public static void Remember(string sources, Recipe recipe)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(recipe)) return;
        lock (State)
        {
            var recipes = _recipes ??= ReadRecipes();
            if (!recipes.TryGetValue(sources, out var known))
            {
                if (recipes.Count >= MaxRecipePrograms) return;
                recipes[sources] = known = [];
            }

            if (known.Count >= MaxRecipes || known.Exists(r => Same(r, recipe))) return;
            known.Add(recipe);
            if (_recipesQueued) return;
            _recipesQueued = true;
            var path = Path.Join(Folder, RecipesFile);
            _writes = _writes.ContinueWith(_ => Write(path, RecipeBytes(recipes)), TaskScheduler.Default);
        }
    }

    private static bool Same(Recipe a, Recipe b) =>
        a.Inputs == b.Inputs && a.Target == b.Target && a.Fixed.AsSpan().SequenceEqual(b.Fixed);

    // The dictionary the write was queued for: after an Open (next world) _recipes is null, and serializing that
    // wrote an empty file over the recipes
    private static byte[] RecipeBytes(Dictionary<string, List<Recipe>> recipes)
    {
        lock (State)
        {
            if (ReferenceEquals(recipes, _recipes)) _recipesQueued = false;
            return JsonSerializer.SerializeToUtf8Bytes(recipes, Json);
        }
    }

    private static Dictionary<string, List<Recipe>> ReadRecipes()
    {
        var read = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        if (ReadJson<Dictionary<string, List<Recipe>>>(Path.Join(Folder, RecipesFile)) is { } file)
            foreach (var (key, recipes) in file.Take(MaxRecipePrograms).ToArray().Bounded(MaxRecipePrograms))
                if (recipes is not null)
                    read[key] = [.. recipes.Where(Whole).Take(MaxRecipes)];

        if (_seed is { } seed) // what the file lacks, the mod's seed gives
            foreach (var (key, recipes) in seed.Take(MaxRecipePrograms).ToArray().Bounded(MaxRecipePrograms))
                if (recipes is not null && !read.ContainsKey(key) && read.Count < MaxRecipePrograms)
                    read[key] = [.. recipes.Where(Whole).Take(MaxRecipes)];
        return read;
    }

    private static bool Whole(Recipe r) =>
        r is { Inputs: not null, Target: not null, Colors: not null, Fixed.Length: PipelineRecipes.StateWords, Attributes: not null } &&
        r.Attributes.Length % PipelineRecipes.AttributeWords == 0 && Assert(r.Colors.Length < 1 << 16);

    // The device checks the header before handing it to the driver
    public static byte[]? Pipelines(uint vendor, uint model)
    {
        if (!Active) return null;
        var path = PipelinePath(vendor, model);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxFile
                ? File.ReadAllBytes(path)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written at once: the process may end right after
    public static void KeepPipelines(uint vendor, uint model, byte[] data)
    {
        if (!Active || !NotNull(data) || data.Length is 0 or > MaxFile) return; // a driver may keep more: not kept then, not wrong
        Write(PipelinePath(vendor, model), data);
    }

    public static string Report()
    {
        if (!Active) return "";
        int read, kept;
        lock (State) (read, kept) = (Loaded, _kept);
        return $"; shader cache: {read} ports read, {kept} written, pipeline cache {PipelineBytes >> 10} KB at the start";
    }

    private static Dictionary<string, string[]> ReadSettled()
    {
        return ReadJson<Dictionary<string, string[]>>(Path.Join(Folder, HotFile)) is { } file
            ? new Dictionary<string, string[]>(file.Where(e => e.Value is not null).Take(MaxSettled), StringComparer.Ordinal)
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
    }

    // Via a side file moved over it: a reader never sees half a file
    private static void Write(string path, byte[] bytes)
    {
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
        var hotNames = hot is null ? "" : string.Join(";", hot.Order(StringComparer.Ordinal));
        return Path.Join(Folder, PortsFolder, Build, Hash(Format, sources, hotNames) + ".json");
    }

    private static string PipelinePath(uint vendor, uint model)
    {
        return Path.Join(Folder, $"pipelines-{vendor:x4}-{model:x4}.bin");
    }

    private static string Hash(params ReadOnlySpan<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < Math.Min(parts.Length, MaxParts); i++)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(parts[i]));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset(), 0, KeyBytes);
    }
}

// Recipe encoding: GlTap.Fixed as Caps, Depth, offset bits, eight blend words, stencil word; attributes nine numbers
// each (TerrainPools.Attribute). Scene and terrain share the program's sources.
internal static class PipelineRecipes
{
    public const int AttributeWords = 9, StateWords = 13, MaxInputs = 16, MaxColors = 8;
    public const string Terrain = "t|";

    public static ShaderCache.Recipe Packed((string Inputs, string Target, uint Topology) key, GlTap.Fixed state,
        TerrainPools.Attribute[] attributes, TerrainPipeline.Target into)
    {
        uint[] words =
        [
            state.Caps, state.Depth, BitConverter.SingleToUInt32Bits(state.OffsetFactor),
            BitConverter.SingleToUInt32Bits(state.OffsetUnits), state.Blend.B0, state.Blend.B1, state.Blend.B2,
            state.Blend.B3, state.Blend.B4, state.Blend.B5, state.Blend.B6, state.Blend.B7, state.Stencil
        ];
        var packed = new long[Math.Min(attributes.Length, MaxInputs) * AttributeWords];
        for (var i = 0; i < Math.Min(attributes.Length, MaxInputs); i++)
        {
            var a = attributes[i];
            ReadOnlySpan<long> one =
            [
                a.Location, a.Buffer, a.Size, a.Type, a.Normalized ? 1 : 0, a.Integer ? 1 : 0, a.Stride, a.Offset, a.Divisor
            ];
            one.CopyTo(packed.AsSpan(i * AttributeWords));
        }

        return new ShaderCache.Recipe(key.Inputs, key.Target, key.Topology, [.. into.Colors], into.Depth, words, packed);
    }

    public static (GlTap.Fixed State, TerrainPools.Attribute[] Attributes, TerrainPipeline.Target Target)? Unpacked(
        ShaderCache.Recipe recipe)
    {
        if (!NotNull(recipe) || recipe.Fixed.Length != StateWords ||
            recipe.Attributes.Length % AttributeWords != 0 || recipe.Attributes.Length > MaxInputs * AttributeWords ||
            recipe.Colors.Length > MaxColors) return null;
        var w = recipe.Fixed;
        var state = new GlTap.Fixed(w[0], w[1], BitConverter.UInt32BitsToSingle(w[2]), BitConverter.UInt32BitsToSingle(w[3]),
            new GlTap.BlendState(w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11]), w[12]);
        var a = recipe.Attributes;
        var attributes = new TerrainPools.Attribute[a.Length / AttributeWords];
        for (var i = 0; i < Math.Min(attributes.Length, MaxInputs); i++)
        {
            var at = i * AttributeWords;
            attributes[i] = new TerrainPools.Attribute((int)a[at], (int)a[at + 1], (int)a[at + 2], (int)a[at + 3], a[at + 4] != 0,
                a[at + 5] != 0) { Stride = (int)a[at + 6], Offset = a[at + 7], Divisor = (int)a[at + 8] };
        }

        return (state, attributes, new TerrainPipeline.Target([.. recipe.Colors], recipe.Depth));
    }

    // "" without cache or record
    public static string SourcesOf(int program)
    {
        if (!ShaderCache.Active || !Assert(program > 0) || GlTap.LinkedOf(program) is not { } linked) return "";
        return ShaderCache.Sources(linked.Vertex, linked.Fragment, linked.Attributes);
    }
}

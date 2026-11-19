using System.Diagnostics.CodeAnalysis;
using System.Text;
using HarmonyLib;
using Komet.Rendering;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

internal static partial class ProjectAssets
{
    private static readonly Dictionary<string, string> InterfaceErrors = new(StringComparer.Ordinal);
    private static readonly ShaderProgram Scratch = new();
    private static Action<ShaderProgram, bool>? _loadProgram;
    private static Action<ShaderProgram, string, HashSet<string>>? _collectUniforms;
    private static ICoreClientAPI? _interfaceApi;
    private static bool _readingOriginal;

    internal static void InstallInterfaceCheck(Harmony harmony, ICoreAPI api)
    {
        var load = AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
            [typeof(ShaderProgram), typeof(bool)]);
        var collect = AccessTools.DeclaredMethod(typeof(ShaderProgram), "collectUniformNames",
            [typeof(string), typeof(HashSet<string>)]);
        if (load is null || collect is null)
        {
            api.Logger.Warning(
                "Komet Shader Studio: engine bindings cannot be checked; game shader overrides are disabled.");
            return;
        }

        _collectUniforms = AccessTools.MethodDelegate<Action<ShaderProgram, string, HashSet<string>>>(collect);
        var check = new HarmonyMethod(typeof(ProjectAssets), nameof(CheckInterface)) { priority = Priority.Last };
        if (harmony.Patch(load, postfix: check) is not null)
        {
            _loadProgram = AccessTools.MethodDelegate<Action<ShaderProgram, bool>>(load);
        }
        else
        {
            ClearInterfaceCheck();
            api.Logger.Warning(
                "Komet Shader Studio: the binding check could not be installed; game shader overrides are disabled.");
        }
    }

    internal static void ClearInterfaceCheck() => (_loadProgram, _collectUniforms) = (null, null);

    internal static string? InterfaceError(string copy) => InterfaceErrors.GetValueOrDefault(StudioFiles.Full(copy));

    // The engine's setters index the declaration tables directly. It records declarations even when their
    // OpenGL location is -1, so comparing active GPU uniforms would miss bindings the game still needs.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification =
            "The original is only loaded as source. Disposing it would make OpenGL calls on uncompiled stages.")]
    // ReSharper disable once InconsistentNaming Harmony binds the engine's parameter name
    private static void CheckInterface(ShaderProgram program, bool useSSBOs)
    {
        if (_readingOriginal || _interfaceApi is not { } api || _loadProgram is not { } load ||
            _collectUniforms is not { } collect || !program.LoadFromFile || Winners.Count == 0) return;

        var affected = Winners.Where(pair => IsProgramStage(pair.Key, program.PassName) ||
                                             program.includes.Contains(Path.GetFileName(pair.Key))).ToArray();
        if (affected.Length == 0) return;

        var original = LoadOriginal(api, program, useSSBOs, load);
        var (required, samplers) = Bindings(original, collect);
        var (provided, textures) = Bindings(program, collect);
        var missing = required.Except(provided, StringComparer.Ordinal)
            .Concat(samplers.Except(textures, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;

        // Keep the actual registered program object. Its next Compile builds the original stages, so no
        // render callback ever sees the rejected interface, even on the first load of a saved project.
        (program.VertexShader, program.FragmentShader, program.GeometryShader) =
            (original.VertexShader, original.FragmentShader, original.GeometryShader);
        program.includes = original.includes;
        program.LoadError = original.LoadError;

        var error =
            $"{program.PassName}: missing engine shader bindings ({string.Join(", ", missing)}). The game shader is used instead.";
        foreach (var (_, copy) in affected.Bounded(MaxFiles)) InterfaceErrors[copy] = error;
        api.Logger.Warning("Komet Shader Studio: {0}", error);
    }

    private static ShaderProgram LoadOriginal(ICoreClientAPI api, ShaderProgram program, bool useSsbos,
        Action<ShaderProgram, bool> load)
    {
        var original = new ShaderProgram
        {
            PassId = program.PassId,
            PassName = program.PassName,
            AssetDomain = program.AssetDomain,
            Oit = program.Oit
        };

        // Load sources only. Use the same engine include expansion and shader edits as the candidate.
        // Other programs still see the project assets after the comparison.
        var includes = ShaderEdits.Includes();
        var assetsToRestore = new List<(IAsset Asset, byte[] Data)>();
        var includesToRestore = new Dictionary<string, string>(StringComparer.Ordinal);
        _readingOriginal = true;
        try
        {
            foreach (var (location, applied) in Applied.Bounded(MaxFiles))
            {
                if (api.Assets.TryGet(location) is { } asset && ReferenceEquals(asset.Data, applied.Draft))
                {
                    assetsToRestore.Add((asset, applied.Draft));
                    asset.Data = applied.Original;
                }

                var name = Path.GetFileName(location.Path);
                if (includes?.TryGetValue(name, out var current) == true &&
                    includesToRestore.TryAdd(name, current))
                {
                    includes[name] = Encoding.UTF8.GetString(applied.Original);
                }
            }

            load(original, useSsbos);
        }
        finally
        {
            foreach (var (asset, data) in assetsToRestore.Bounded(MaxFiles)) asset.Data = data;
            if (includes is not null)
            {
                foreach (var (name, source) in includesToRestore.Bounded(MaxFiles)) includes[name] = source;
            }

            _readingOriginal = false;
        }

        return original;
    }

    private static (HashSet<string> Names, string[] Samplers) Bindings(
        ShaderProgram program, Action<ShaderProgram, string, HashSet<string>> collect)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var scratch = Scratch; // one for every call: it only holds the collector's dictionary, no graphics resources
        scratch.textureLocations.Clear();
        foreach (var shader in (ReadOnlySpan<Shader?>)
                 [program.VertexShader, program.FragmentShader, program.GeometryShader])
        {
            if (shader?.Code is { } code) collect(scratch, code, names);
        }

        return (names, [.. scratch.textureLocations.Keys]);
    }
}

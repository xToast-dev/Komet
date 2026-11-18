using System.Text;
using Komet.Rendering;

namespace Komet.Sdk;

// The active projects' copies in the game's assets: before the registry reads its files (ShaderMods.Loading) each game
// shader file a project copies gets the winner's bytes (ShaderProjects.Winner). A copy runs its last good state
// (ShaderBuild's history) while one exists, else the file; a build under way tries its draft (Trial). Programs on a
// project's vanilla list (an override switched off, ShaderModules.KeepsGame) and files picked from another active
// project keep their file. Restored bytes are the ones found.
// Main thread.
internal static partial class ProjectAssets
{
    private const int MaxFiles = 512;

    private static readonly Dictionary<AssetLocation, (byte[] Original, byte[] Draft, string Copy)> Applied = [];
    private static readonly Dictionary<string, byte[]> Trials = new(StringComparer.Ordinal);

    // Copies whose state broke a program with no good state to fall back on: the game's file runs until they build
    private static readonly HashSet<string> Broken = new(StringComparer.Ordinal);

    // A copy that broke its program with no good state: the game's file runs until it builds
    public static bool IsBroken(string copy) => Broken.Contains(StudioFiles.Full(copy));

    // Which copy each game file got at the last load (game file -> copy path)
    public static Dictionary<string, string> Winners { get; } = new(StringComparer.OrdinalIgnoreCase);

    // A build's draft of a copy, tried at the next load
    internal static void Try(string copy, byte[] draft) => Trials[StudioFiles.Full(copy)] = draft;

    internal static void EndTrial(string copy) => _ = Trials.Remove(StudioFiles.Full(copy));

    // A build's verdict: a good copy runs again, a failed one without a good state gives way to the game's file
    internal static void Built(string copy, bool good)
    {
        var full = StudioFiles.Full(copy);
        _ = good ? Broken.Remove(full) : ShaderBuild.LastGood(full) is null && Broken.Add(full);
    }

    // Once a reload is through, ShaderMods asks this: a copy that broke a program and has no good state gives way. True
    // when one did, then the caller reloads once. A broken copy stays out until it builds, so this cannot loop.
    internal static bool Validate(ShaderMods.Diagnostic[] programs)
    {
        var gave = false;
        foreach (var program in programs.Bounded(MaxFiles))
        {
            if (program.Compiled || !Touches(program)) continue;

            foreach (var (game, copy) in Winners.ToArray().Bounded(MaxFiles))
            {
                if (Reads(program, game) && ShaderBuild.LastGood(copy) is null && Broken.Add(copy)) gave = true;
            }
        }

        return gave;
    }

    private static bool Reads(ShaderMods.Diagnostic program, string game) =>
        IsProgramStage(game, program.Pass) ||
               program.Includes.Any(i => Path.GetFileName(i).Equals(Path.GetFileName(game),
                   StringComparison.OrdinalIgnoreCase));

    private static bool IsProgramStage(string key, string program) =>
        ShaderAssets.AssetKey(key).StartsWith("shaders/" + program + ".", StringComparison.OrdinalIgnoreCase);

    // A program reads a file a project's copy replaced (its own stage files or an include)
    private static bool Touches(ShaderMods.Diagnostic program) => Winners.Keys.Any(key => Reads(program, key));

    internal static void Changed() => Winners.Clear();

    // ShaderMods.Loading: the winners' bytes in, the ones no project claims any more back to what was found
    internal static void Apply(ICoreClientAPI api)
    {
        InterfaceErrors.Clear();
        _interfaceApi = api;
        ShaderModules.Use(api);
        var wanted = _loadProgram is null ? [] : Wanted();
        foreach (var (location, applied) in Applied.ToArray().Bounded(MaxFiles))
        {
            if (!wanted.ContainsKey(location))
            {
                Restore(api, location, applied.Original, applied.Draft);
                _ = Applied.Remove(location);
            }
        }

        Winners.Clear();
        if (wanted.Count > 0)
        {
            GameValues.Ensure(api); // a project's copy may read komet… values
        }

        foreach (var (location, (copy, bytes)) in wanted.Bounded(MaxFiles))
        {
            if (api.Assets.TryGet(location) is not { Data: { } current } asset) continue;

            var original = Applied.TryGetValue(location, out var previous) && ReferenceEquals(current, previous.Draft)
                ? previous.Original
                : current;
            Applied[location] = (original, bytes, copy);
            asset.Data = bytes;
            Winners[ShaderProjects.GameFileOf(copy) ?? location.Path] = StudioFiles.Full(copy);
        }
    }

    // World end or the Studio's module gone: every asset back
    internal static void Clear(ICoreClientAPI? api)
    {
        if (api is not null)
        {
            foreach (var (location, applied) in Applied.ToArray().Bounded(MaxFiles))
            {
                Restore(api, location, applied.Original, applied.Draft);
            }
        }

        Applied.Clear();
        Trials.Clear();
        Broken.Clear();
        _interfaceApi = null;
        InterfaceErrors.Clear();
        Winners.Clear();
    }

    private static Dictionary<AssetLocation, (string Copy, byte[] Bytes)> Wanted()
    {
        Dictionary<AssetLocation, (string, byte[])> wanted = [];
        foreach (var project in ShaderProjects.Order().Bounded(MaxFiles))
        {
            foreach (var copy in ShaderProjects.Copies(project).Bounded(MaxFiles))
            {
                if (ShaderProjects.GameFileOf(copy) is not { } game ||
                    (Broken.Contains(StudioFiles.Full(copy)) && !Trials.ContainsKey(StudioFiles.Full(copy))) ||
                    ShaderModules.KeepsGame(game, project) || Unpicked(game, project) || wanted.Count >= MaxFiles)
                {
                    continue;
                }

                if (!ShaderAssets.Split(game, out _, out var path)) continue;

                var location = ShaderAssets.Location(path);
                if (!wanted.ContainsKey(location) && Bytes(copy) is { } bytes) wanted[location] = (copy, bytes);
            }
        }

        return wanted;
    }

    // Several active projects copy the file and the player picked another one's copy
    private static bool Unpicked(string game, string project) =>
        ShaderModules.Pick(game) is { } pick && pick != project && ShaderProjects.IsActive(pick) &&
               ShaderProjects.CopyPath(pick, game) is { } copy && File.Exists(copy);

    // The trial's draft, else the last good state, else the file
    private static byte[]? Bytes(string copy)
    {
        var full = StudioFiles.Full(copy);
        if (Trials.TryGetValue(full, out var draft)) return draft;

        var text = ShaderBuild.LastGood(full) ?? StudioFiles.Read(full);
        // an unedited copy keeps whatever the file is now (the game's, or a running shader mod's version)
        return text is { Length: > 0 } && !ShaderProjects.Unedited(full, text) ? Encoding.UTF8.GetBytes(text) : null;
    }

    private static void Restore(ICoreClientAPI api, AssetLocation location, byte[] original, byte[] draft)
    {
        if (api.Assets.TryGet(location) is { } asset && ReferenceEquals(asset.Data, draft)) asset.Data = original;
    }
}

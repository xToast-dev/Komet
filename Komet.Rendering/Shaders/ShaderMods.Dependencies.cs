namespace Komet.Rendering;

// ShaderMods: version mismatches, dependents and dependencies that lock a mod's switch.
internal static partial class ShaderMods
{
    // A dependency the mod was built for that is missing (Missing) or loaded in another major version
    public static (string Name, string Wanted, string Loaded)? Mismatch(ICoreAPI? api, string id)
    {
        if (api?.ModLoader is not { } loader ||
            loader.GetMod(id)?.Info?.Dependencies is not { } dependencies)
        {
            return null;
        }

        foreach (var dependency in dependencies.Bounded(32))
        {
            if (dependency.ModID is "game" or "survival" or "creative" ||
                dependency.Version is not { Length: > 0 } wanted || wanted == "*")
            {
                continue;
            }

            var loaded = loader.GetMod(dependency.ModID)?.Info;
            if (loaded is null) return (dependency.ModID, wanted, "");

            if (Major(wanted) != Major(loaded.Version)) return (loaded.Name, wanted, loaded.Version);
        }

        return null;
    }

    // Switched off, or a mod it was built for missing or of another major version; a mod others depend on runs either way
    private static bool Held(ICoreAPI api, Found mod, ILogger? logger)
    {
        if (ShaderState.Of(api).Stopped.Contains(mod.Id)) return !Needed(api, mod.Id, logger, "it is switched off");

        return Mismatch(api, mod.Id) is not null && !Needed(api, mod.Id, logger, "a mod it was built for differs");
    }

    // A mod other loaded mods depend on runs: held back, its dependents' code fails at the join
    private static bool Needed(ICoreAPI api, string id, ILogger? logger, string despite)
    {
        if (Dependents(api, id) is not { Length: > 0 } by) return false;

        logger?.Warning("Komet: {0} runs although {1}: needed by {2}", id, despite, string.Join(", ", by));
        return true;
    }

    // The loaded mods that depend on the mod, by name: held back, it would fail them (OverhaulLib Legacy Compat held back,
    // Tools Animations threw at the join). One whose systems HoldBack skips does not count: a shader mod without content
    // the player switched off, needed by none in turn (DRTAgX off lets SheyderMod go).
    public static string[] Dependents(ICoreAPI? api, string id) => Dependents(api, id, 0);

    private static string[] Dependents(ICoreAPI? api, string id, int depth)
    {
        if (api?.ModLoader?.Mods is not { } mods || !NotNull(id)) return [];

        return
        [
            .. mods.Bounded(MaxMods).Select(m => m?.Info).OfType<ModInfo>()
                .Where(info => info.Dependencies?.Any(d => d.ModID == id) == true && !Skips(api, info.ModID, depth))
                .Select(info => info.Name is { Length: > 0 } name ? name : info.ModID)
        ];
    }

    private static bool Skips(ICoreAPI api, string id, int depth) =>
        depth < MaxChain && ShaderState.Of(api).Stopped.Contains(id) &&
        All(api).Find(f => f.Id == id) is { Content: false } found &&
        (found.Replaces || found.Declared) && Dependents(api, id, depth + 1).Length == 0;

    // The found mods this one depends on (switched on, a held one starts first: its code calls into theirs)
    public static List<Found> Dependencies(ICoreAPI? api, Found mod) =>
        api?.ModLoader?.GetMod(mod.Id)?.Info?.Dependencies is { } wanted
            ? All(api).FindAll(f => wanted.Any(d => d.ModID == f.Id))
            : [];

    private static string Major(string? version) => version?.Split('.', 2)[0].Trim() ?? "";

    // The session ends (Komet's Dispose): the next one decides anew
    public static void Release()
    {
        Pauses.Clear(); // the world goes: a paused mod is held back from the start of the next
        Off.Clear(); // the hold patch stays until the next HoldBack: skipped systems' Dispose comes after Komet's
        Serving.Clear();
    }

    private static void Tell()
    {
        if (_api is not { } api || Off.Count == 0) return;

        var names = All(api).Where(m => Off.Contains(m.Id)).Select(m => m.Name);
        api.ShowChatMessage(Lang.Get("komet:hud-shaders-held", string.Join(", ", names)));
    }
}

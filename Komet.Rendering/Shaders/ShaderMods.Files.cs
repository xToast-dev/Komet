using System.IO.Compression;

namespace Komet.Rendering;

// ShaderMods: replaced shader files giving way to the game's (or a Komet variant) when a mod is held back.
internal static partial class ShaderMods
{
    // Also from Komet's AssetsLoaded, before any other mod starts: a mod that reads the game's shader files itself at its
    // start (SheyderMod reads its includes) must already find what the registry will
    public static void Swap(ICoreClientAPI api)
    {
        var mods = All(api);
        foreach (var mod in mods.Bounded(MaxMods))
        {
            if (Off.Contains(mod.Id))
            {
                var restored = 0;
                foreach (var file in mod.Replaced.Bounded(MaxEntries))
                {
                    restored += Restore(api, mod, file, Running(mods, file)) ? 1 : 0;
                }

                foreach (var entry in mod.Elsewhere.Bounded(MaxEntries))
                {
                    restored += Give(api, mods, mod, entry) ? 1 : 0;
                }

                if (Told.Add(mod.Id))
                {
                    api.Logger?.Notification("Komet shaders: {0} is held back, {1} of its {2} replaced files give way",
                        mod.Id, restored, mod.Replaced.Length + mod.Elsewhere.Length);
                }
            }
            else
            {
                foreach (var variant in mod.Variants.Bounded(MaxEntries))
                {
                    Stand(api, mod, variant);
                }
            }
        }
    }

    // domain:category/file gets the bytes of domain:config/komet/category/file
    private static void Stand(ICoreClientAPI api, Found mod, string variant)
    {
        if (variant.Split(':', 2) is not [var domain, var file] || api.Assets is not { } assets) return;

        if (assets.TryGet(new AssetLocation(domain, VariantPath + file)) is not { Data: { } data }) return;

        if (assets.TryGet(new AssetLocation(domain, file)) is { } target)
        {
            target.Data = data;
        }
        else
        {
            api.Logger?.Warning("Komet: {0}'s Komet version {1} has no {2} to stand in for", mod.Id, VariantPath + file,
                domain + ":" + file);
        }
    }

    // A file a held mod replaced in another mod's domain: a running mod's replacement of it, else the owner's own file
    private static bool Give(ICoreClientAPI api, List<Found> mods, Found mod, string entry)
    {
        if (entry.Split(':', 2) is not [var domain, var file] ||
            api.Assets?.TryGet(new AssetLocation(domain, file)) is not { Data: { } data } asset)
        {
            return false;
        }

        try
        {
            if (Bytes(mod, domain, file) is not { } mine || !data.AsSpan().SequenceEqual(mine)) return false;

            Found? instead = null;
            foreach (var other in mods.Bounded(MaxMods))
            {
                if (!Off.Contains(other.Id) && (other.Elsewhere.Contains(entry) || other.Id == domain)) instead = other;
            }

            if (instead is null || Bytes(instead, domain, file) is not { } theirs) return false;

            asset.Data = theirs;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: {0}'s own {1} could not be read back: {2}", domain, file, e.Message);
            return false;
        }
    }

    // The last running mod (in load order) that replaces the file too: its version stands in, not the game's
    private static Found? Running(List<Found> mods, string file)
    {
        Found? last = null;
        foreach (var other in mods.Bounded(MaxMods))
        {
            if (!Off.Contains(other.Id) && other.Replaced.Contains(file)) last = other;
        }

        return last;
    }

    private static bool Restore(ICoreClientAPI api, Found mod, string file, Found? instead)
    {
        // The registry reads shaders/ and shaderincludes/ into one include table by file name: an include a mod put under
        // shaders/ (DRTAgX's fogandlight.fsh) has its original under shaderincludes/
        var original = Path.Join(GamePaths.AssetsPath, "game", file);
        if (!File.Exists(original))
        {
            original = Path.Join(GamePaths.AssetsPath, "game", "shaderincludes", Path.GetFileName(file));
        }

        if (api.Assets?.TryGet(new AssetLocation("game", file)) is not { Data: { } data } asset) return false;

        try
        {
            if (Bytes(mod, "game", file) is not { } mine || !data.AsSpan().SequenceEqual(mine))
            {
                return false; // not the held mod's
            }
            // a running mod that replaces the file too (SheyderMod beside a held DRTAgX) keeps its version
            if (instead is not null && Bytes(instead, "game", file) is { } theirs)
            {
                asset.Data = theirs;
            }
            else if (File.Exists(original))
            {
                asset.Data = File.ReadAllBytes(original);
            }
            else
            {
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            api.Logger?.Warning("Komet shaders: the game's {0} could not be read, {1}'s stays: {2}", file, mod.Id,
                e.Message);
            return false;
        }
    }

    // The mod's own copy of a game file. The loaded asset is the mod's when the bytes match: the game reads zip mods from
    // an unpacked copy in its cache, so the asset's origin does not name the zip.
    private static byte[]? Bytes(Found mod, string domain, string file)
    {
        var inner = "assets/" + domain + "/" + file;
        if (Directory.Exists(mod.Source))
        {
            var path = Path.Join(mod.Source, inner);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        using var zip = ZipFile.OpenRead(mod.Source);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(inner, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !Assert(entry.Length < 1 << 24)) return null;

        using var stream = entry.Open();
        using var copy = new MemoryStream((int)entry.Length);
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

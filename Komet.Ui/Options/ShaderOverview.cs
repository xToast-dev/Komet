using System.ComponentModel;
using System.Diagnostics;
using Komet.Api;
using Komet.Core;
using Komet.Rendering;

namespace Komet.Options;

// Active shader page: refresh scheduling, switches and dependency activation.
internal static partial class ShaderOverview
{
    private const string PageId = "shaders-overview";
    private const int MaxListed = ShaderHub.MaxDefines + ShaderHub.MaxPasses;

    private static ICoreClientAPI? _api;
    private static bool _asked;
    private static int _generation;

    public static event Action? Refreshed;

    public static void Hook(ICoreClientAPI api)
    {
        if (!NotNull(api.Event)) return;

        _api = api;
        _generation++;
        _asked = false;
        foreach (var mod in ShaderState.Of(api).Off.Bounded(MaxListed))
        {
            ModShaders.Turn(mod, false);
        }

        // off first: a second Hook without Unhook would subscribe twice, and Unhook takes it off once
        ModShaders.Changed -= Ask;
        ShaderMods.Changed -= Ask;
        ModShaders.Changed += Ask;
        ShaderMods.Changed += Ask;
    }

    public static void Unhook()
    {
        ModShaders.Changed -= Ask;
        ShaderMods.Changed -= Ask;

        (_api, _asked) = (null, false);
        _generation++;
        ShaderState.Forget();
    }

    private static void Ask()
    {
        if (_asked || _api?.Event is not { } events || !NotNull(_api.World)) return;

        _asked = true;
        var generation = _generation;
        events.EnqueueMainThreadTask(() =>
        {
            if (generation != _generation) return;

            _asked = false;
            if (_api is { } api) Build(api);
        }, "komet-shader-overview");
    }

    public static void Build(ICoreClientAPI api)
    {
        var page = OptionPages.Page(PageId, T("shaders-overview"), T("section-shaders"));
        page.Legend = Legend;
        _ = page.Group(() =>
            T("shaders-renderer", ShaderHub.Renderer == KapiRenderer.Vulkan ? "Vulkan" : "OpenGL"));
        var mods = ModShaders.Mods();
        var found = ShaderMods.All(api);
        var declared = found.Where(f => f.Declared).ToList();
        var others = found
            .Where(f => !f.Declared && !mods.Contains(f.Id) && !mods.Contains(ShaderPacks.ModPrefix + f.Id)).ToList();
        mods = [.. mods.Where(m => !declared.Exists(d => m == d.Id || m == ShaderPacks.ModPrefix + d.Id))];
        if (mods.Count + others.Count + declared.Count == 0) _ = page.Group(T("shaders-none"));

        if (declared.Count > 0) _ = page.Group(T("shaders-declared"));

        foreach (var mod in declared.Bounded(MaxListed))
        {
            var details = Status(mod) + (ShaderPacks.About(ShaderPacks.ModPrefix + mod.Id) is null
                ? ""
                : " " + Details(api, ShaderPacks.ModPrefix + mod.Id));
            _ = page.Switch(mod.Name, () => ShaderMods.Wanted(api, mod), on => Turn(api, mod, on), details).Default(1)
                .Live()
                .LockedWhen(() => Needed(mod)).Tagged(() => Tags(mod)).Card(() => Facts(mod));
        }

        if (mods.Count > 0 && declared.Count + others.Count > 0) _ = page.Group(T("shaders-packs"));

        foreach (var mod in mods.Bounded(MaxListed))
        {
            Pack(api, page, mod, found.Find(f => f.Id == mod));
        }

        if (others.Count > 0) _ = page.Group(T("shaders-others"));

        foreach (var mod in others.Bounded(MaxListed))
        {
            Other(api, page, mod);
        }

        Files(api, page);
    }

    private static void Files(ICoreClientAPI api, OptionPage page) =>
        _ = page.Group(T("shaders-files"))
            .Button(T("shaders-folder"), T("do-open"), () => Open(ShaderPacks.UserFolder), T("shaders-folder-hint"))
            .Button(T("shaders-reload"), T("do-reload"), () => Reload(api), T("shaders-reload-hint"));

    // A shader mod that declares nothing: a switch when it replaces game shaders, else its facts
    private static void Other(ICoreClientAPI api, OptionPage page, ShaderMods.Found mod) =>
        _ = (mod.Replaces
                ? page.Switch(mod.Name, () => ShaderMods.Wanted(api, mod), on => Turn(api, mod, on), Status(mod))
                    .Default(1).Live()
                    .LockedWhen(() => Needed(mod))
                : page.Button(mod.Name, T("shaders-others-tag"), Nothing, Status(mod))).Tagged(() => Tags(mod))
            .Card(() => Facts(mod));

    // A shader pack's switch
    private static void Pack(ICoreClientAPI api, OptionPage page, string mod, ShaderMods.Found? files) =>
        _ = page.Switch(Name(api, mod), () => ModShaders.IsOn(mod), on =>
            {
                ModShaders.Turn(mod, on);
                Store(api);
            }, Details(api, mod) + (files is null ? "" : " " + Status(files))).Default(1)
            .Tagged(() => [.. Simple(mod).Select(f => (f.Icon, f.Kind))]).Card(() => Simple(mod));

    // Every pack read again from disk and the mods' files (a designer's edit shows without leaving the world; Modding ›
    // Templates after writing the example pack)
    internal static void Reload(ICoreClientAPI api)
    {
        _ = api.Assets?.Reload(AssetCategory.shaders); // the mods' shader files, as the game's .reload shaders
        _ = api.Assets?.Reload(AssetCategory.shaderincludes);
        // and their komet.json and Komet versions (config/komet.json, config/komet/...)
        foreach (var domain in ShaderMods.All(api).SelectMany(m => m.Variants.Select(v => v.Split(':')[0]).Append(m.Id))
                     .Distinct().Bounded(MaxListed))
        {
            _ = api.Assets?.Reload(new AssetLocation(domain, ShaderMods.VariantPath[..^1]));
        }

        ShaderPacks.Load(api);
        Build(api);
        ShaderHub.Reload();
        Refreshed?.Invoke();
    }

    // A shader mod found by its files (replacing shaders or declared): on, it starts (or resumes) at once, a held mod it
    // depends on first; off, it pauses at once, unless other mods depend on it (its row is locked then)
    private static void Turn(ICoreClientAPI api, ShaderMods.Found mod, bool on)
    {
        if (!on && ShaderMods.Dependents(api, mod.Id).Length > 0) return;

        if (on && !EnableDependencies(api, mod)) return;

        if (!ApplyTurn(api, mod, on))
        {
            _ = ActivationFailed(api, mod, "its start or pause callback failed");
            return;
        }

        Store(api);
        Build(api);
        Refreshed?.Invoke();
    }

    private static bool EnableDependencies(ICoreClientAPI api, ShaderMods.Found root)
    {
        var found = ShaderMods.All(api).ToDictionary(m => m.Id, StringComparer.Ordinal);
        found[root.Id] = root;
        if (!ShaderDependencies.Plan(root.Id, id => [.. ShaderMods.Dependencies(api, found[id]).Select(m => m.Id)],
                out var order, out var why))
        {
            return ActivationFailed(api, root, why);
        }

        foreach (var id in order.Bounded(ShaderDependencies.MaxMods))
        {
            if (ShaderMods.Mismatch(api, id) is { } mismatch) return ActivationFailed(api, root, $"{id}: {mismatch}");
        }

        foreach (var id in order.Bounded(ShaderDependencies.MaxMods))
        {
            if (id != root.Id && (ShaderMods.Off.Contains(id) || ShaderState.Of(api).Stopped.Contains(id)) &&
                !ApplyTurn(api, found[id], true))
            {
                return ActivationFailed(api, root, "dependency did not start: " + id);
            }
        }

        return true;
    }

    private static bool ActivationFailed(ICoreClientAPI api, ShaderMods.Found mod, string why)
    {
        api.Logger?.Warning("Komet: shader mod {0} was not activated: {1}", mod.Id, why);
        api.ShowChatMessage(T("shaders-found-blocked", mod.Name));
        Store(api);
        Build(api);
        return false;
    }

    private static bool ApplyTurn(ICoreClientAPI api, ShaderMods.Found mod, bool on)
    {
        ShaderMods.Want(api, mod, on);
        ModShaders.Turn(ShaderPacks.ModPrefix + mod.Id, on); // its komet.json settings and passes, at once either way
        ModShaders.Turn(mod.Id, on); // and what its code registered
        if (on && ShaderMods.Off.Contains(mod.Id))
        {
            if (!ShaderMods.Start(api, mod)) return false;

            api.ShowChatMessage(T("shaders-found-started", mod.Name));
        }
        else if (!on && mod.Replaces && !ShaderMods.Off.Contains(mod.Id))
        {
            if (!ShaderMods.Pause(api, mod)) return false;

            api.ShowChatMessage(T("shaders-found-paused", mod.Name));
        }

        return true;
    }

    private static void Nothing()
    {
        // The button only selects its detail card.
    }

    private static void Open(string folder)
    {
        try
        {
            _ = Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })
                ?.Dispose(); // the handle, not the process
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception
                                      or InvalidOperationException)
        {
            _api?.Logger?.Warning("Komet shaders: the folder {0} could not be opened: {1}", folder, e.Message);
        }
    }

    private static void Store(ICoreClientAPI api)
    {
        var off = ModShaders.Disabled();
        ShaderState.Of(api).Off = [.. off];
        ShaderState.Store(api);
    }

    private static string T(string key, params object[] args) => KometText.Translate("settings-" + key, args);
}

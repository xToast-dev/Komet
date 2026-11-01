using Komet.Api;
using Komet.Rendering;
using Komet.Core;

namespace Komet.Options;

// The Shaders section's first page, Active shaders. It names the renderer drawing now, then lists every shader pack and every
// mod with KometShaders registrations. Each gets a switch that takes all its defines, uniforms, edits and passes in or out
// (kept in ModConfig/komet/shader-settings.json), and a detail text with where it comes from, what it registers, and a pass
// that failed and why. The page is built anew when anything registers, is switched or fails, at most once a frame.
internal static class ShaderOverview
{
    private const string PageId = "shaders-overview";
    private const int MaxListed = KometShaders.MaxDefines + KometShaders.MaxPasses;

    private static ICoreClientAPI? _api;
    private static bool _asked;
    private static Action? _changed;

    // A click changed the packs on disk (the example): an open screen takes its pages anew
    public static event Action? Refreshed;

    public static void Hook(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(api.Event)) return;
        _api = api;
        foreach (var mod in ShaderState.Of(api).Off.Bounded(MaxListed)) ModShaders.Turn(mod, false);

        _changed ??= Ask;
        // off first: a second Hook without Unhook would subscribe twice, and Unhook takes it off once
        ModShaders.Changed -= _changed;
        ShaderMods.Changed -= _changed;
        ModShaders.Changed += _changed;
        ShaderMods.Changed += _changed;
    }

    public static void Unhook()
    {
        if (_changed is { } changed)
        {
            ModShaders.Changed -= changed;
            ShaderMods.Changed -= changed;
        }

        (_api, _asked) = (null, false);
        ShaderState.Forget();
    }

    private static void Ask()
    {
        if (_asked || _api?.Event is not { } events || !NotNull(_api.World)) return;
        _asked = true;
        events.EnqueueMainThreadTask(() =>
        {
            _asked = false;
            if (_api is { } api) Build(api);
        }, "komet-shader-overview");
    }

    // The page anew (KometOptions.Page with the same id replaces it in place)
    public static void Build(ICoreClientAPI api)
    {
        if (!NotNull(api)) return;
        var page = KometOptions.Page(PageId, T("shaders-overview"), T("section-shaders"));
        page.Legend = Legend;
        _ = page.Group(() => T("shaders-renderer", KometShaders.Renderer == KometRenderer.Vulkan ? "Vulkan" : "OpenGL"));
        var mods = ModShaders.Mods();
        var found = ShaderMods.All(api);
        var declared = found.Where(f => f.Declared).ToList();
        var others = found.Where(f => !f.Declared && !mods.Contains(f.Id) && !mods.Contains(ShaderPacks.ModPrefix + f.Id)).ToList();
        mods = [.. mods.Where(m => !declared.Exists(d => m == d.Id || m == ShaderPacks.ModPrefix + d.Id))];
        if (mods.Count + others.Count + declared.Count == 0) _ = page.Group(T("shaders-none"));
        if (declared.Count > 0) _ = page.Group(T("shaders-declared"));
        foreach (var mod in declared.Bounded(MaxListed))
        {
            var details = Status(mod) + (ShaderPacks.About(ShaderPacks.ModPrefix + mod.Id) is null ? "" : " " + Details(api, ShaderPacks.ModPrefix + mod.Id));
            _ = page.Switch(mod.Name, () => ShaderMods.Wanted(api, mod), on => Turn(api, mod, on), details).Default(1).Live()
                .Tagged(() => Tags(mod)).Card(() => Facts(mod));
        }

        if (mods.Count > 0 && declared.Count + others.Count > 0) _ = page.Group(T("shaders-packs"));
        foreach (var mod in mods.Bounded(MaxListed))
        {
            var files = found.Find(f => f.Id == mod);
            _ = page.Switch(Name(api, mod), () => ModShaders.IsOn(mod), on =>
            {
                ModShaders.Turn(mod, on);
                Store(api);
            }, Details(api, mod) + (files is null ? "" : " " + Status(files))).Default(1)
                .Tagged(() => [.. Simple(mod).Select(f => (f.Icon, f.Kind))]).Card(() => Simple(mod));
        }

        if (others.Count > 0) _ = page.Group(T("shaders-others"));
        foreach (var mod in others.Bounded(MaxListed))
            _ = (mod.Replaces
                ? page.Switch(mod.Name, () => ShaderMods.Wanted(api, mod), on => Turn(api, mod, on), Status(mod)).Default(0).Live()
                : page.Button(mod.Name, T("shaders-others-tag"), Nothing, Status(mod))).Tagged(() => Tags(mod)).Card(() => Facts(mod));
        _ = page.Group(T("shaders-files"))
            .Button(T("shaders-folder"), T("do-open"), () => Open(ShaderPacks.UserFolder), T("shaders-folder-hint"))
            .Button(T("shaders-reload"), T("do-reload"), () => Reload(api), T("shaders-reload-hint"));
    }

    // Every pack read again from disk and the mods' files (a designer's edit shows without leaving the world; Modding ›
    // Templates after writing the example pack)
    internal static void Reload(ICoreClientAPI api)
    {
        _ = api.Assets?.Reload(AssetCategory.shaders); // the mods' shader files, as the game's .reload shaders
        _ = api.Assets?.Reload(AssetCategory.shaderincludes);
        // and their komet.json and Komet versions (config/komet.json, config/komet/...)
        foreach (var domain in ShaderMods.All(api).SelectMany(m => m.Variants.Select(v => v.Split(':')[0]).Append(m.Id)).Distinct().Bounded(MaxListed))
            _ = api.Assets?.Reload(new AssetLocation(domain, ShaderMods.VariantPath[..^1]));
        ShaderPacks.Load(api);
        Build(api);
        KometShaders.Reload();
        Refreshed?.Invoke();
    }

    // A shader mod replacing game shaders: on, it starts (or resumes) at once; off, it pauses at once
    private static void Turn(ICoreClientAPI api, ShaderMods.Found mod, bool on)
    {
        ShaderMods.Want(api, mod, on);
        ModShaders.Turn(ShaderPacks.ModPrefix + mod.Id, on); // its komet.json settings and passes, at once either way
        ModShaders.Turn(mod.Id, on); // and what its code registered
        Store(api);
        if (on && ShaderMods.Off.Contains(mod.Id) && ShaderMods.Start(api, mod)) api.ShowChatMessage(T("shaders-found-started", mod.Name));
        else if (!on && mod.Replaces && !ShaderMods.Off.Contains(mod.Id) && ShaderMods.Pause(api, mod))
            api.ShowChatMessage(T("shaders-found-paused", mod.Name));
        Build(api);
        Refreshed?.Invoke();
    }

    // How the mod is bound in, then what holds for it now: the markers after its name (Modding › Shader reference explains them)
    private static List<(TagIcon Icon, TagKind Kind)> Tags(ShaderMods.Found mod)
    {
        // who drives it first, as a badge: the mod talks to Komet itself, Komet takes it over, or Komet only knows it
        List<(TagIcon, TagKind)> tags = [Bound(mod) switch
        {
            Binding.Speaks => (TagIcon.Link, TagKind.Speaks),
            Binding.TakesOver => (TagIcon.Comet, TagKind.TakesOver),
            _ => (TagIcon.Eye, TagKind.Plain)
        }];
        if (Runs(mod) is var (icon, kind, _)) tags.Add((icon, kind));
        if (mod.Declared) tags.Add((TagIcon.File, TagKind.Plain));
        if (ModShaders.Mods().Contains(mod.Id)) tags.Add((TagIcon.Code, TagKind.Plain));
        if (mod.Variants.Length > 0) tags.Add((TagIcon.Versions, TagKind.Plain));
        if (ShaderMods.Off.Contains(mod.Id)) tags.Add((TagIcon.Pause, TagKind.Plain));
        else
        {
            var (_, gl, vulkan, _) = ShaderMods.State(mod);
            if (gl.Length + vulkan.Length > 0 || Needs(mod) is not null) tags.Add((TagIcon.Warning, TagKind.Warn));
        }

        return tags;
    }

    // The detail's card for a found mod: how it is bound in, where it runs, its shaders, what it replaces, what holds
    private static List<(TagIcon Icon, TagKind Kind, string Text)> Facts(ShaderMods.Found mod)
    {
        List<(TagIcon, TagKind, string)> facts = [Bound(mod) switch
        {
            Binding.Speaks => (TagIcon.Link, TagKind.Speaks, T(mod.Declared ? "fact-speaks-json" : "fact-speaks-code")),
            Binding.TakesOver => (TagIcon.Comet, TagKind.TakesOver, T("fact-takesover")),
            _ => (TagIcon.Eye, TagKind.Plain, T("fact-found"))
        }];
        if (Runs(mod) is { } runs) facts.Add(runs);
        var (programs, gl, _, _) = ShaderMods.State(mod);
        if (ShaderMods.Off.Contains(mod.Id)) facts.Add((TagIcon.Pause, TagKind.Plain, T("fact-held")));
        else if (programs == 0) // a declared mod's shader files may be passes only: nothing to say then
        {
            if (!mod.Declared && (mod.Own.Length > 0 || mod.Replaces)) facts.Add((TagIcon.Shader, TagKind.Plain, T("fact-shaders-none")));
        }
        else if (gl.Length > 0) facts.Add((TagIcon.Shader, TagKind.Warn, T("fact-shaders-failed", gl.Length, programs, string.Join(", ", gl.Take(3)))));
        else facts.Add((TagIcon.Shader, TagKind.Plain, T("fact-shaders-ok", programs)));
        var game = mod.Replaced.Count(f => f.StartsWith("shaders/", StringComparison.Ordinal));
        if (Replacing(game, mod.Replaced.Length - game, mod.Elsewhere.Length) is { Length: > 0 } replacing)
            facts.Add((TagIcon.Replaces, TagKind.Plain, replacing));
        if (mod.Variants.Length > 0) facts.Add((TagIcon.Versions, TagKind.Plain, T("fact-variants", mod.Variants.Length)));
        if (Needs(mod) is { } needs) facts.Add((TagIcon.Warning, TagKind.Warn, needs));
        return facts;
    }

    // What it replaces, naming only what there is: "Replaces 15 game shaders, 2 game includes, 8 files of another mod"
    private static string Replacing(int shaders, int includes, int elsewhere)
    {
        List<string> parts = [];
        if (shaders > 0) parts.Add(T("fact-replaces-shaders", shaders));
        if (includes > 0) parts.Add(T("fact-replaces-includes", includes));
        if (elsewhere > 0) parts.Add(T("fact-replaces-elsewhere", elsewhere));
        return parts.Count == 0 ? "" : T("fact-replaces", string.Join(", ", parts));
    }

    // A pack or a code mod: it talks to Komet, and runs where Komet draws
    private static List<(TagIcon Icon, TagKind Kind, string Text)> Simple(string mod)
    {
        var pack = mod.StartsWith(ShaderPacks.ModPrefix, StringComparison.Ordinal);
        List<(TagIcon, TagKind, string)> facts = [(TagIcon.Link, TagKind.Speaks, T(pack ? "fact-speaks-pack" : "fact-speaks-code"))];
        if (Runs(null) is { } runs) facts.Add(runs);
        facts.Add((pack ? TagIcon.Folder : TagIcon.Code, TagKind.Plain, T(pack ? "legend-folder" : "legend-code")));
        return facts;
    }

    // Where it runs now: OpenGL (an OpenGL-only mod says why), or Vulkan with how its translation fares; null while held
    private static (TagIcon Icon, TagKind Kind, string Text)? Runs(ShaderMods.Found? mod)
    {
        if (mod is not null && ShaderMods.Off.Contains(mod.Id)) return null;
        if (KometShaders.Renderer != KometRenderer.Vulkan)
            return (TagIcon.OpenGl, TagKind.OpenGl, T(mod is not null && ModAdapters.OpenGlOnly(mod.Id) ? "fact-gl-only" : "fact-gl"));
        if (mod is null) return (TagIcon.Vulkan, TagKind.VulkanOk, T("fact-vk"));
        var (programs, _, vulkan, pending) = ShaderMods.State(mod);
        if (vulkan.Length > 0) return (TagIcon.Vulkan, TagKind.VulkanPartial, T("fact-vk-partial", vulkan.Length, programs, vulkan[0].Pass));
        if (pending > 0) return (TagIcon.Vulkan, TagKind.VulkanOk, T("fact-vk-porting", pending, programs));
        return (TagIcon.Vulkan, TagKind.VulkanOk, programs > 0 ? T("fact-vk-ok", programs) : T("fact-vk"));
    }

    // Every symbol of this page and what it means, for the detail while no row is chosen
    private static List<(TagIcon Icon, TagKind Kind, string Text)> Legend()
    {
        List<(TagIcon, TagKind, string)> legend =
        [
            (TagIcon.Link, TagKind.Heading, T("legend-binding")),
            (TagIcon.Link, TagKind.Speaks, T("legend-link")), (TagIcon.Comet, TagKind.TakesOver, T("legend-comet")),
            (TagIcon.Eye, TagKind.Plain, T("legend-eye")),
            (TagIcon.Vulkan, TagKind.Heading, T("legend-renderer")),
            (TagIcon.Vulkan, TagKind.VulkanOk, T("legend-vulkan")), (TagIcon.Vulkan, TagKind.VulkanPartial, T("legend-vulkan-partial")),
            (TagIcon.OpenGl, TagKind.OpenGl, T("legend-opengl")),
            (TagIcon.File, TagKind.Heading, T("legend-details")),
            (TagIcon.File, TagKind.Plain, T("legend-file")), (TagIcon.Folder, TagKind.Plain, T("legend-folder")),
            (TagIcon.Code, TagKind.Plain, T("legend-code")), (TagIcon.Versions, TagKind.Plain, T("legend-versions")),
            (TagIcon.Pause, TagKind.Plain, T("legend-pause")), (TagIcon.Warning, TagKind.Warn, T("legend-warning"))
        ];
        return legend;
    }

    private enum Binding
    {
        Found,
        Speaks,
        TakesOver
    }

    // komet.json or the code API: the mod talks to Komet; an adapter Komet ships: Komet takes it over
    private static Binding Bound(ShaderMods.Found mod)
    {
        if (mod.Declared || ModShaders.Mods().Contains(mod.Id)) return Binding.Speaks;
        return ModAdapters.Steers(mod.Id) ? Binding.TakesOver : Binding.Found;
    }

    // A dependency missing or of another major version: Komet does not start the mod
    private static string? Needs(ShaderMods.Found mod)
    {
        if (_api is null || ShaderMods.Mismatch(_api, mod.Id) is not var (name, wanted, loaded)) return null;
        return loaded.Length == 0 ? T("shaders-found-missing", name, wanted) : T("shaders-found-version", name, wanted, loaded);
    }

    // What the mod changes and how its shaders fare under the renderer drawing now
    private static string Status(ShaderMods.Found mod)
    {
        // the card says how it is bound in, where it runs, its shaders and what it replaces; the text adds the rest
        List<string> lines = [];
        var (_, _, vulkan, _) = ShaderMods.State(mod);
        if (vulkan.Length > 0) lines.Add(T("shaders-found-vulkan", vulkan.Length, vulkan.Length, vulkan[0].Pass, vulkan[0].Why));
        if (!ShaderMods.Off.Contains(mod.Id) && _api is not null && !ShaderMods.Wanted(_api, mod)) lines.Add(T("shaders-found-next"));
        if (ShaderMods.Off.Contains(mod.Id) && mod.Replaces) lines.Add(T("shaders-found-held"));
        if (!mod.Declared) lines.Add(T("shaders-others-join"));
        return string.Join(" ", lines);
    }

    // A found mod's row only shows its detail
    private static void Nothing()
    {
        // the detail is all there is
    }

    private static string Name(ICoreClientAPI api, string mod)
    {
        if (ShaderPacks.About(mod) is { } pack) return pack.Name;
        return api.ModLoader?.GetMod(mod)?.Info?.Name is { Length: > 0 } name ? name : mod;
    }

    // What the detail panel says: origin and author, what it registers, each pass and a failure's reason
    private static string Details(ICoreClientAPI api, string mod)
    {
        var lines = new List<string>();
        if (ShaderPacks.About(mod) is { } pack)
        {
            lines.Add(pack.Where.EndsWith(':') ? T("shaders-pack-declared", pack.Where[..^1]) : T("shaders-pack", ShaderPacks.Shown(pack.Where)));
            if (pack.Author is { Length: > 0 } author) lines.Add(T("shaders-by", author));
            lines.Add(T("shaders-settings", pack.Settings));
        }
        else
        {
            var info = api.ModLoader?.GetMod(mod)?.Info;
            lines.Add(T("shaders-mod", mod));
            if (info?.Authors is { Count: > 0 } authors) lines.Add(T("shaders-by", string.Join(", ", authors)));
        }

        var (defines, uniforms, edits) = ModShaders.Count(mod);
        lines.Add(T("shaders-counts", defines, uniforms, edits));
        foreach (var (owner, id, failure) in ModPasses.Listed().Bounded(KometShaders.MaxPasses))
            if (owner == mod)
                lines.Add(failure.Length > 0 ? T("shaders-pass-failed", id, failure) : T("shaders-pass", id));
        return string.Join(" ", lines);
    }

    private static void Open(string folder)
    {
        try
        {
            _ = Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose(); // the handle, not the process
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
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

    private static string T(string key, params object[] args) =>
        KometText.Translate("settings-" + key, args);
}

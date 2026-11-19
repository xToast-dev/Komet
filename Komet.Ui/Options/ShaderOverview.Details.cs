using Komet.Api;
using Komet.Core;
using Komet.Rendering;

namespace Komet.Options;

// Shader badges, cards and status text.
internal static partial class ShaderOverview
{
    private static List<(TagIcon Icon, TagKind Kind)> Tags(ShaderMods.Found mod)
    {
        List<(TagIcon, TagKind)> tags =
        [
            Bound(mod) switch
            {
                Binding.Speaks => (TagIcon.Link, TagKind.Speaks),
                Binding.TakesOver => (TagIcon.Comet, TagKind.TakesOver),
                _ => (TagIcon.Eye, TagKind.Plain)
            }
        ];
        if (Runs(mod) is var (icon, kind, _)) tags.Add((icon, kind));

        if (mod.Declared) tags.Add((TagIcon.File, TagKind.Plain));

        if (ModShaders.Mods().Contains(mod.Id)) tags.Add((TagIcon.Code, TagKind.Plain));

        if (mod.Variants.Length > 0) tags.Add((TagIcon.Versions, TagKind.Plain));

        if (NeededBy(mod) is not null) tags.Add((TagIcon.Lock, TagKind.Plain));

        if (ShaderMods.Off.Contains(mod.Id))
        {
            tags.Add((TagIcon.Pause, TagKind.Plain));
        }
        else
        {
            var (_, gl, vulkan, _) = ShaderMods.State(mod);
            if (gl.Length + vulkan.Length > 0 || Needs(mod) is not null) tags.Add((TagIcon.Warning, TagKind.Warn));
        }

        return tags;
    }

    private static List<(TagIcon Icon, TagKind Kind, string Text)> Facts(ShaderMods.Found mod)
    {
        List<(TagIcon, TagKind, string)> facts =
        [
            Bound(mod) switch
            {
                Binding.Speaks => (TagIcon.Link, TagKind.Speaks,
                    T(mod.Declared ? "fact-speaks-json" : "fact-speaks-code")),
                Binding.TakesOver => (TagIcon.Comet, TagKind.TakesOver, T("fact-takesover")),
                _ => (TagIcon.Eye, TagKind.Plain, T("fact-found"))
            }
        ];
        if (Runs(mod) is { } runs) facts.Add(runs);

        if (Compiled(mod) is { } compiled) facts.Add(compiled);

        var game = mod.Replaced.Count(f => f.StartsWith("shaders/", StringComparison.Ordinal));
        if (Replacing(game, mod.Replaced.Length - game, mod.Elsewhere.Length) is { Length: > 0 } replacing)
        {
            facts.Add((TagIcon.Replaces, TagKind.Plain, replacing));
        }

        if (mod.Variants.Length > 0)
        {
            facts.Add((TagIcon.Versions, TagKind.Plain, T("fact-variants", mod.Variants.Length)));
        }

        if (NeededBy(mod) is { } by) facts.Add((TagIcon.Lock, TagKind.Plain, T("fact-needed", by)));

        if (Needs(mod) is { } needs) facts.Add((TagIcon.Warning, TagKind.Warn, needs));

        return facts;
    }

    // How the mod's programs compiled; null for a declared mod whose files may be passes only
    private static (TagIcon, TagKind, string)? Compiled(ShaderMods.Found mod)
    {
        var (programs, gl, _, _) = ShaderMods.State(mod);
        if (ShaderMods.Off.Contains(mod.Id)) return (TagIcon.Pause, TagKind.Plain, T("fact-held"));

        if (programs == 0)
        {
            return !mod.Declared && (mod.Own.Length > 0 || mod.Replaces)
                ? (TagIcon.Shader, TagKind.Plain, T("fact-shaders-none"))
                : null;
        }

        return gl.Length > 0
            ? (TagIcon.Shader, TagKind.Warn,
                T("fact-shaders-failed", gl.Length, programs, string.Join(", ", gl.Take(3))))
            : (TagIcon.Shader, TagKind.Plain, T("fact-shaders-ok", programs));
    }

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
        List<(TagIcon, TagKind, string)> facts =
            [(TagIcon.Link, TagKind.Speaks, T(pack ? "fact-speaks-pack" : "fact-speaks-code"))];
        if (Runs(null) is { } runs) facts.Add(runs);

        facts.Add((pack ? TagIcon.Folder : TagIcon.Code, TagKind.Plain, T(pack ? "legend-folder" : "legend-code")));
        return facts;
    }

    // Where it runs now: OpenGL (an OpenGL-only mod says why), or Vulkan with how its translation fares; null while held
    private static (TagIcon Icon, TagKind Kind, string Text)? Runs(ShaderMods.Found? mod)
    {
        if (mod is not null && ShaderMods.Off.Contains(mod.Id)) return null;

        if (ShaderHub.Renderer != KapiRenderer.Vulkan)
        {
            return (TagIcon.OpenGl, TagKind.OpenGl,
                T(mod is not null && ModAdapters.OpenGlOnly(mod.Id) ? "fact-gl-only" : "fact-gl"));
        }

        if (mod is null) return (TagIcon.Vulkan, TagKind.VulkanOk, T("fact-vk"));

        var (programs, _, vulkan, pending) = ShaderMods.State(mod);
        if (vulkan.Length > 0)
        {
            return (TagIcon.Vulkan, TagKind.VulkanPartial,
                T("fact-vk-partial", vulkan.Length, programs, vulkan[0].Pass));
        }

        if (pending > 0) return (TagIcon.Vulkan, TagKind.VulkanOk, T("fact-vk-porting", pending, programs));

        return (TagIcon.Vulkan, TagKind.VulkanOk, programs > 0 ? T("fact-vk-ok", programs) : T("fact-vk"));
    }

    private static List<(TagIcon Icon, TagKind Kind, string Text)> Legend()
    {
        List<(TagIcon, TagKind, string)> legend =
        [
            (TagIcon.Link, TagKind.Heading, T("legend-binding")),
            (TagIcon.Link, TagKind.Speaks, T("legend-link")), (TagIcon.Comet, TagKind.TakesOver, T("legend-comet")),
            (TagIcon.Eye, TagKind.Plain, T("legend-eye")),
            (TagIcon.Vulkan, TagKind.Heading, T("legend-renderer")),
            (TagIcon.Vulkan, TagKind.VulkanOk, T("legend-vulkan")),
            (TagIcon.Vulkan, TagKind.VulkanPartial, T("legend-vulkan-partial")),
            (TagIcon.OpenGl, TagKind.OpenGl, T("legend-opengl")),
            (TagIcon.File, TagKind.Heading, T("legend-details")),
            (TagIcon.File, TagKind.Plain, T("legend-file")), (TagIcon.Folder, TagKind.Plain, T("legend-folder")),
            (TagIcon.Code, TagKind.Plain, T("legend-code")), (TagIcon.Versions, TagKind.Plain, T("legend-versions")),
            (TagIcon.Lock, TagKind.Plain, T("legend-lock")), (TagIcon.Pause, TagKind.Plain, T("legend-pause")),
            (TagIcon.Warning, TagKind.Warn, T("legend-warning"))
        ];
        return legend;
    }

    // komet.json or the code API: the mod talks to Komet; an adapter Komet ships: Komet takes it over
    private static Binding Bound(ShaderMods.Found mod)
    {
        if (mod.Declared || ModShaders.Mods().Contains(mod.Id)) return Binding.Speaks;

        return ModAdapters.Steers(mod.Id) ? Binding.TakesOver : Binding.Found;
    }

    // Other mods depend on it: its switch stays on, and why (above its hint); null while none does
    private static string? Needed(ShaderMods.Found mod) =>
        NeededBy(mod) is { } by ? T("shaders-found-needed", by) : null;

    // The mods that depend on a mod with a switch, by name; null for none
    private static string? NeededBy(ShaderMods.Found mod) =>
        (mod.Replaces || mod.Declared) && _api is not null &&
        ShaderMods.Dependents(_api, mod.Id) is { Length: > 0 } by
            ? string.Join(", ", by)
            : null;

    // A dependency missing or of another major version: Komet does not start the mod
    private static string? Needs(ShaderMods.Found mod)
    {
        if (_api is null || ShaderMods.Mismatch(_api, mod.Id) is not var (name, wanted, loaded)) return null;

        return loaded.Length == 0
            ? T("shaders-found-missing", name, wanted)
            : T("shaders-found-version", name, wanted, loaded);
    }

    private static string Status(ShaderMods.Found mod)
    {
        // the card says how it is bound in, where it runs, its shaders and what it replaces; the text adds the rest
        List<string> lines = [];
        var (_, _, vulkan, _) = ShaderMods.State(mod);
        if (vulkan.Length > 0)
        {
            lines.Add(T("shaders-found-vulkan", vulkan.Length, vulkan.Length, vulkan[0].Pass, vulkan[0].Why));
        }

        if (!ShaderMods.Off.Contains(mod.Id) && _api is not null && !ShaderMods.Wanted(_api, mod))
        {
            lines.Add(T("shaders-found-next"));
        }

        if (ShaderMods.Off.Contains(mod.Id) && mod.Replaces) lines.Add(T("shaders-found-held"));

        if (!mod.Declared) lines.Add(T("shaders-others-join"));

        return string.Join(" ", lines);
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
            lines.Add(pack.Where.EndsWith(':')
                ? T("shaders-pack-declared", pack.Where[..^1])
                : T("shaders-pack", ShaderPacks.Shown(pack.Where)));
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
        foreach (var (owner, id, failure) in ModPasses.Listed().Bounded(ShaderHub.MaxPasses))
        {
            if (owner == mod)
            {
                lines.Add(failure.Length > 0 ? T("shaders-pass-failed", id, failure) : T("shaders-pass", id));
            }
        }

        return string.Join(" ", lines);
    }

    private enum Binding
    {
        Found,
        Speaks,
        TakesOver
    }
}

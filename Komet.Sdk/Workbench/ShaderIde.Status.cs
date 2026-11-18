using Komet.Hud;
using Komet.Rendering;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

internal enum OverrideRun
{
    Active,
    NotVisible,
    NotApplied
}

// One state per override, the same word everywhere (the TEST chip, the editor's head, its strip): Active (the game draws
// it now), Not visible (loaded and compiled, not used in the last frames) or Not applied (the game runs something else,
// with the reason). Applied means the registry got this copy's bytes (ProjectAssets.Winners) and the program compiled.
// "Used" is DrawCounter: ShaderProgramBase.Use of the program within the last 30 frames.
internal sealed partial class ShaderIde
{
    private readonly HashSet<string> _toldNotVisible = new(StringComparer.Ordinal);

    private (OverrideRun State, string Reason)? StatusOf(ShaderDocument? document)
    {
        if (document is null || StudioLink.GameFileOf(document.Path) is not { } game ||
            StudioLink.Relative(game) is not { } relative || StudioLink.ProjectOf(document.Path) is not { } project ||
            !ShaderPaths.IsStage(game))
        {
            return null;
        }

        var program = Path.GetFileNameWithoutExtension(game);
        if (NotApplied(document.Path, relative, project, program) is { } why) return (OverrideRun.NotApplied, why);

        if (DrawCounter.Drawn(program)) return (OverrideRun.Active, L("why-active"));

        if (ModuleOf(document) is { Why: not ModuleWhy.None } module) return (OverrideRun.NotVisible, Reason(module));

        // no known cause: what the counter saw goes along (the sky showed "not visible" while drawn, cause unknown)
        var evidence = DrawCounter.Evidence(program);
        if (_toldNotVisible.Add(program))
        {
            AddLog(program + ": " + L("state-not-drawn") + " – " + evidence); // OUTPUT keeps it in full
        }

        return (OverrideRun.NotVisible, Why(ModuleWhy.NotDrawn) + " [" + evidence + "]");
    }

    // Not visible with its cause named: the cloud mode or the render setting that keeps the shader from drawing
    private static string Reason(ShaderModule module)
    {
        if (module.Why == ModuleWhy.PostSetting && SettingOf(module) is { } key)
        {
            return L("why-post-setting-named", key);
        }

        return Why(module.Why);
    }

    // The render setting a not visible shader waits for (the status line's link), null when none
    private static string? SettingOf(ShaderModule? module)
    {
        var name = module?.Id.ToLowerInvariant() ?? "";
        return module?.Why switch
        {
            ModuleWhy.CloudMode => "CloudRenderMode",
            ModuleWhy.PostSetting when name.Contains("ssao", StringComparison.Ordinal) => "SSAOQuality",
            ModuleWhy.PostSetting when name.Contains("godray", StringComparison.Ordinal) => "GodRayQuality",
            ModuleWhy.PostSetting when name.Contains("bloom", StringComparison.Ordinal) => "Bloom",
            ModuleWhy.PostSetting => "FXAA",
            _ => null
        };
    }


    private string? NotApplied(string copy, string relative, string project, string program)
    {
        if (ProjectInfo(project) is not { Active: true }) return L("why-pack-off");

        if (ShaderModules.KeepsGame(relative, project)) return L("why-override-off");

        if (ProjectAssets.InterfaceError(copy) is { } error) return error;

        if (ProjectAssets.IsBroken(copy)) return L("why-broken");

        if (!ProjectAssets.Winners.TryGetValue(relative, out var winner)) return L("why-not-loaded");

        if (!StudioLink.PathEquals(winner, StudioFiles.Full(copy)))
        {
            return L("why-other-wins", StudioLink.ProjectOf(winner) ?? "?");
        }

        return ShaderMods.CompiledNow(program) == false ? L("why-compile-failed") : null;
    }

    // What the game runs of an editable override: the editor's text (built since the last change) or the last build
    private double GameShows(HudUi ui, double right, double y, double h, ShaderDocument document)
    {
        if (document.ReadOnly || StudioLink.GameFileOf(document.Path) is null) return right;

        var live = !_unbuilt && !_busy;
        var text = live ? L("game-shows-live") : L("game-shows-last-build");
        var tw = ui.TextW(ui.F.Small, text) + ui.Px(14);
        var (cy, ch) = (y + (h - ui.Px(22)) / 2, ui.Px(22));
        ui.Fill(right - tw, cy, tw, ch, (live ? HudUi.Ok : StudioColors.Pending) with { A = 0.12 });
        ui.Text(right - tw + ui.Px(7), cy, ch, ui.F.Small, text, live ? HudUi.Ok : StudioColors.Pending);
        Tip(right - tw, cy, tw, ch, L(live ? "tip-game-shows-live" : "tip-game-shows-last-build"));
        return right - tw - ui.Px(14);
    }

    private static string StateWord(OverrideRun state) =>
        state switch
        {
            OverrideRun.Active => L("state-active"),
            OverrideRun.NotVisible => L("state-not-drawn"),
            _ => L("state-not-applied")
        };

    private static Rgba StateInk(OverrideRun state) =>
        state switch
        {
            OverrideRun.Active => HudUi.Ok,
            OverrideRun.NotVisible => StudioColors.Pending,
            _ => HudUi.Err
        };
}

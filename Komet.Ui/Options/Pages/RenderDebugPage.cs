using Komet.Core;
using Komet.Rendering;
using static Komet.Options.OptionsText;

namespace Komet.Options;

internal static class RenderDebugPage
{
    internal const string Id = "komet-render-debug";

    internal static OptionPage Build(string section, Action<string> navigate)
    {
        var page = new OptionPage(Id, T("dev-render-title"), section) { Capacity = OptionPage.MaxRows };
        _ = page.Group(T("dev-render-session"))
            .Button(T("dev-render-reset"), T("do-reset"), RenderDebug.Reset, T("dev-render-session-hint"))
            .Button(T("dev-render-refresh"), T("do-reload"), () => navigate(Id));
        _ = page.Group(T("dev-render-view"))
            .Choice(T("dev-render-mode"), [T("dev-render-fill"), T("dev-render-wire"), T("dev-render-points")],
                () => (int)RenderDebug.Mode, v => RenderDebug.SetView((RenderDebug.View)v), T("dev-render-view-hint"))
            .EnabledWhen(() => RenderDebug.Installed && Links.Vulkan.NonSolidFill).Default(0)
            .Live()
            .Switch(T("dev-render-no-cull"), () => RenderDebug.NoCull, RenderDebug.SetNoCull, T("dev-render-cull-hint"))
            .EnabledWhen(() => RenderDebug.Installed).Default(0).Live()
            .Switch(T("dev-render-no-depth"), () => RenderDebug.NoDepth, RenderDebug.SetNoDepth,
                T("dev-render-depth-hint"))
            .EnabledWhen(() => RenderDebug.Installed).Default(0).Live();
        if (!RenderDebug.Installed) _ = page.Group(T("dev-render-unavailable"));

        if (!Links.Vulkan.NonSolidFill) _ = page.Group(T("dev-render-no-wire"));

        _ = page.Group(T("dev-render-terrain"));
        foreach (var pass in Enum.GetValues<EnumChunkRenderPass>().Bounded(16))
        {
            var captured = (int)pass;
            _ = page.Switch(T("dev-render-pass", pass), () => RenderDebug.TerrainAllowed(captured),
                    enabled => RenderDebug.SetTerrain(captured, enabled), T("dev-render-pass-hint"))
                .EnabledWhen(() => RenderDebug.Installed).Default(1).Live();
        }

        _ = page.Group(T("dev-render-shaders"));
        var passes = ModPasses.Listed().ToArray();
        if (passes.Length == 0) _ = page.Group(T("dev-render-no-passes"));

        foreach (var (mod, id, failure) in passes.Bounded(ShaderHub.MaxPasses))
        {
            _ = page.Switch(mod + ":" + id, () => RenderDebug.ShaderAllowed(mod, id),
                    on => RenderDebug.SetShader(mod, id, on),
                    T("dev-render-shader-hint") + (failure.Length == 0 ? "" : "\n" + failure))
                .Default(1).Live();
        }

        return page;
    }
}

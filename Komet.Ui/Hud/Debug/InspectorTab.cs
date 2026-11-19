using System.Globalization;
using System.Text;
using Komet.Core;
using Komet.Rendering;

namespace Komet.Hud;

// The render inspector tab (wide, live): view modes, culling and depth switches, terrain passes, shader passes, shader
// mods, renderers and programs, with search and filters; RenderDebug applies the overrides
internal sealed partial class InspectorTab(ICoreClientAPI capi) : IHudTab
{
    // ReSharper disable once ReplaceWithPrimaryConstructorParameter - the other partial files use it, not the parameter
    private readonly ICoreClientAPI _capi = capi;
    private readonly HashSet<string> _expanded = [];
    private long _programRefresh;
    private ShaderMods.Diagnostic[] _programs = [];
    private int _renderMatches;
    private RenderDebug.RendererInfo[] _renderers = [];
    private bool _sources;

    public string Id => "inspector";
    public bool Wide => true;

    public bool Live() => true;

    public void Draw(HudUi ui, HudBody body)
    {
        _body = body;
        var copy = T("hud-v-dbg-copy");
        _ = ui.LinkText(ui.X + ui.W - ui.TextW(ui.F.Body, copy), ui.Y, copy, () =>
        {
            if (NotNull(_capi.Input))
            {
                _capi.Input.ClipboardText = RenderReport();
                _capi.ShowChatMessage(T("hud-debug-copied"));
            }
        });
        RenderToolbar(ui);
        ui.Rule();
        _renderMatches = 0;
        RenderControls(ui);
        ui.Wrap(R("scope"), HudUi.Warn, ui.F.Small);
        ui.Space(8);
        ui.Line(R("terrain"), font: ui.F.Strong);
        foreach (var pass in Enum.GetValues<EnumChunkRenderPass>().Bounded(16))
        {
            if (!Matches("game", "Terrain", false, "game Terrain " + pass)) continue;

            var id = (int)pass;
            ToggleRow(ui, (!RenderDebug.TerrainAllowed(id) ? "* " : "") + pass + " (#" + id + ")",
                RenderDebug.TerrainAllowed(id),
                () => RenderDebug.SetTerrain(id, !RenderDebug.TerrainAllowed(id)));
        }

        RenderPasses(ui);
        RenderMods(ui);
        RenderPrograms(ui);
        if (_renderMatches == 0) ui.Wrap(R("no-matches"), HudUi.Warn);
    }

    private static string T(string key, params object[] args) => KometText.Translate(key, args);

    private static string R(string key, params object[] args) => T("hud-render-" + key, args);

    private void RenderControls(HudUi ui)
    {
        _ = ui.Chips(ui.X, ui.Y, [
            (R("reset"), false, RenderDebug.Reset),
            (R("sources"), _sources, () => _sources = !_sources)
        ]);
        ui.Y += ui.ChipHeight + ui.Px(6);
        if (!RenderDebug.Installed) ui.Wrap(R("unavailable"), HudUi.Warn);

        var x = ui.X;
        foreach (var mode in Enum.GetValues<RenderDebug.View>().Bounded(3))
        {
            var available = RenderDebug.Installed && (mode == RenderDebug.View.Filled || Links.Vulkan.NonSolidFill);
            x += ui.Chip(x, ui.Y, R(mode.ToString()), RenderDebug.Mode == mode,
                available ? () => RenderDebug.SetView(mode) : null) + ui.Px(5);
        }

        ui.Y += ui.ChipHeight + ui.Px(6);
        if (!Links.Vulkan.NonSolidFill) ui.Wrap(R("unsupported"), HudUi.Warn, ui.F.Small);

        ToggleRow(ui, (RenderDebug.NoCull ? "* " : "") + R("cull"), RenderDebug.NoCull,
            () => RenderDebug.SetNoCull(!RenderDebug.NoCull));
        ToggleRow(ui, (RenderDebug.NoDepth ? "* " : "") + R("depth"), RenderDebug.NoDepth,
            () => RenderDebug.SetNoDepth(!RenderDebug.NoDepth));
    }

    private static void ToggleRow(HudUi ui, string label, bool on, Action action) =>
        ui.Line((on ? "[ON]  " : "[OFF] ") + label, on ? HudUi.Ok : HudUi.Warn, click: action);

    private void RenderPasses(HudUi ui)
    {
        var passes = ModPasses.Diagnostics();
        ui.Space(8);
        ui.Line(R("passes", passes.Length), font: ui.F.Strong);
        if (passes.Length == 0) ui.Wrap(R("empty"), HudUi.Dim);

        foreach (var pass in passes.Bounded(ShaderHub.MaxPasses))
        {
            if (!Matches(pass.Mod, "AfterFinalComposition", pass.Failed,
                    pass.Mod + " " + pass.Id + " " + PassDetails(pass) + " " + pass.Fragment))
            {
                continue;
            }

            ToggleRow(ui, (!RenderDebug.ShaderAllowed(pass.Mod, pass.Id) ? "* " : "") + pass.Mod + ":" + pass.Id,
                RenderDebug.ShaderAllowed(pass.Mod, pass.Id),
                () => RenderDebug.SetShader(pass.Mod, pass.Id, !RenderDebug.ShaderAllowed(pass.Mod, pass.Id)));
            ui.Wrap(PassDetails(pass), pass.Failed ? HudUi.Err : HudUi.Dim, ui.F.Small);
            if (_sources) Expand(ui, "pass:" + pass.Mod + ":" + pass.Id, R("source"), pass.Fragment);

            ui.Space(6);
        }
    }

    private static string PassDetails(ModPasses.Diagnostic p) =>
        R("pass-detail", p.RenderStage, p.Order, p.Program, ModShaders.IsOn(p.Mod),
            p.LastEnabled?.ToString() ?? R("unobserved"),
            p.Failed ? p.Failure : R("no-error")) + "\n" + R("inputs") +
        " scene, depth, resolution, time, uv\n" +
        R("uniforms") + " " + string.Join(", ", p.Uniforms);

    private void RenderMods(HudUi ui)
    {
        ui.Space(8);
        ui.Line(R("mods"), font: ui.F.Strong);
        foreach (var mod in ModShaders.Mods().Bounded(4096))
        {
            var pack = ShaderPacks.About(mod);
            var origin = pack is { } p
                ? $"{p.Name} · {p.Author} · {p.Where}"
                : _capi.ModLoader?.GetMod(mod)?.Info?.Name ?? mod;
            if (!Matches(mod, "", false,
                    mod + " " + origin + " " + string.Join(" ", ModShaders.Registrations(mod))))
            {
                continue;
            }

            ui.Wrap(mod + " · " + origin, HudUi.Soft, ui.F.Small);
            ui.Wrap(string.Join("\n", ModShaders.Registrations(mod)), HudUi.Dim, ui.F.Small);
        }

        foreach (var mod in ShaderMods.All(_capi).Bounded(4096))
        {
            if (Matches(mod.Id, "", false, mod.Id + " " + mod.Name + " " + mod.Source + " " +
                                           string.Join(" ",
                                               mod.Replaced.Concat(mod.Elsewhere).Concat(mod.Own)
                                                   .Concat(mod.Variants))))
            {
                Expand(ui, "mod:" + mod.Id, mod.Name + " · " + mod.Id,
                    mod.Source + "\n" + R("replaces") + "\n" + string.Join("\n", mod.Replaced.Concat(mod.Elsewhere)) +
                    "\n" + R("own") + "\n" + string.Join("\n", mod.Own) +
                    "\n" + R("variants") + "\n" + string.Join("\n", mod.Variants));
            }
        }
    }

    private void RenderPrograms(HudUi ui)
    {
        if (Environment.TickCount64 >= _programRefresh)
        {
            _programs = ShaderMods.Diagnostics(_capi);
            _renderers = RenderDebug.Renderers(_capi);
            _programRefresh = Environment.TickCount64 + 1000;
        }

        ui.Space(8);
        ui.Line(R("renderers", _renderers.Length), font: ui.F.Strong);
        foreach (var renderer in _renderers.Bounded(4096))
        {
            if (Matches(renderer.Mods.Length == 0 ? "game" : string.Join("\n", renderer.Mods),
                    renderer.Stage.ToString(), false,
                    renderer.Stage + " " + renderer.Name + " " + renderer.Type + " " + renderer.Assembly + " " +
                    string.Join(" ", renderer.Mods)))
            {
                Expand(ui, "renderer:" + renderer.Stage + ":" + renderer.Name + ":" + renderer.Type,
                    renderer.Stage + " · " + renderer.Name,
                    renderer.Type + "\n" + renderer.Assembly + "\nMods: " + string.Join(", ", renderer.Mods));
            }
        }

        var programs = _programs;
        ui.Space(8);
        ui.Line(R("programs", programs.Length), font: ui.F.Strong);
        ui.Wrap(R("program-scope"), HudUi.Dim, ui.F.Small);
        foreach (var p in programs.Bounded(4096))
        {
            var portFailure = ShaderMods.Ported.GetValueOrDefault(p.Pass) ?? "";
            if (!Matches(p.Owners.Length == 0 ? "game" : string.Join("\n", p.Owners), "",
                    !p.Compiled || portFailure.Length > 0,
                    p.Domain + " " + p.Pass + " " + portFailure + " " +
                    string.Join(" ", p.Owners.Concat(p.Includes))))
            {
                continue;
            }

            var key = "program:" + p.Domain + ":" + p.Pass;
            var text = R("program-detail", p.Domain, p.Pass, p.Id, p.Compiled,
                p.Owners.Length == 0 ? R("unknown-owner") : string.Join(", ", p.Owners));
            if (portFailure.Length > 0) text += " · " + R("port-error", portFailure);

            Expand(ui, key, text, R("includes") + "\n" + string.Join("\n", p.Includes));
        }
    }

    private void Expand(HudUi ui, string key, string label, string detail)
    {
        ui.Line((_expanded.Contains(key) ? "− " : "+ ") + label, HudUi.Link, ui.F.Small, () =>
        {
            if (!_expanded.Remove(key)) _ = _expanded.Add(key);
        });
        if (_expanded.Contains(key)) ui.Wrap(label + "\n" + detail, HudUi.Dim, ui.F.Small);
    }

    private string RenderReport()
    {
        var text = new StringBuilder("Komet Render Debug\n");
        _ = text.AppendLine(R("scope"));
        _ = text.AppendLine(R("override-summary", RenderDebug.OverrideCount,
            R(RenderDebug.ShowingReference ? "reference-active" : "current-active"),
            R(RenderDebug.HasReference ? "reference-saved" : "reference-missing")));
        _ = text.AppendLine(CultureInfo.InvariantCulture,
            $"Renderer: {ShaderHub.Renderer}; view: {RenderDebug.Mode}; no cull: {RenderDebug.NoCull}; no depth: {RenderDebug.NoDepth}");
        foreach (var pass in Enum.GetValues<EnumChunkRenderPass>().Bounded(16))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"Terrain {pass}: {RenderDebug.TerrainAllowed((int)pass)}");
        }

        foreach (var p in ModPasses.Diagnostics().Bounded(ShaderHub.MaxPasses))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"\n{p.Mod}:{p.Id}; debug allowed: {RenderDebug.ShaderAllowed(p.Mod, p.Id)}");
            _ = text.AppendLine(PassDetails(p));
            _ = text.AppendLine(p.Fragment);
        }

        foreach (var mod in ModShaders.Mods().Bounded(4096))
        {
            _ = text.AppendLine("\n" + mod + "\n" + string.Join("\n", ModShaders.Registrations(mod)));
        }

        foreach (var mod in ShaderMods.All(_capi).Bounded(4096))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"\n{mod.Id}: {mod.Source}\n{string.Join("\n", mod.Replaced.Concat(mod.Elsewhere).Concat(mod.Own).Concat(mod.Variants))}");
        }

        foreach (var p in ShaderMods.Diagnostics(_capi).Bounded(4096))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"\n{p.Domain}:{p.Pass}; program={p.Id}; compiled={p.Compiled}; owners={string.Join(", ", p.Owners)}\nIncludes:\n{string.Join("\n", p.Includes)}");
        }

        foreach (var r in RenderDebug.Renderers(_capi).Bounded(4096))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"\nRenderer {r.Stage}: {r.Name}\n{r.Type} · {r.Assembly}\nMods: {string.Join(", ", r.Mods)}");
        }

        return text.ToString();
    }
}

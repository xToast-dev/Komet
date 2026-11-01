using System.Reflection;
using Komet.Core;

namespace Komet.Hud;

// Version page (no tab) and this build: version and source from modinfo.json, channel, commit and edition (from the
// configuration and modinfo.json's version suffix) from the assembly's metadata
internal sealed partial class HudOverlay
{
    private static Assembly Self => KometMod.Assembly; // the mod's, stamped by the build; set before the HUD exists

    // Assertions do not inline without the optimizer: every Komet timing is inflated several times
    private static readonly bool DebugBuild =
        Self.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Debug";

    private static string? _editionStamp; // the overlay asks on every composition
    private static string? _builtStamp; // the version tab asks on every recompose (RefreshMs)

    private static string BuiltStamp() => _builtStamp ??= Metadata(Self, "Built");

    private static string Metadata(Assembly assembly, string key)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Take(64).FirstOrDefault(a => a.Key == key)?.Value ?? "";
    }

    private static (string Version, bool Preview, string Commit, string SourcePath) ReadBuild(ICoreClientAPI capi)
    {
        var mod = capi.ModLoader.GetMod(KometMod.Id);
        if (!NotNull(mod) || !Assert(mod.Info.Version.Length > 0)) return ("", false, "", "");
        return (mod.Info.Version, Metadata(Self, "Channel") == "preview", Metadata(Self, "Commit"),
            mod.SourcePath ?? "");
    }

    // Komet.csproj stamps every build from its configuration and modinfo.json's version suffix
    private static string Edition()
    {
        var stamped = _editionStamp ??= Metadata(Self, "Edition");
        return Assert(stamped.Length > 0) && Assert(stamped is "release" or "pre" or "dev") ? stamped : "dev";
    }

    private (string Text, Rgba Fill, Rgba Color) EditionBadge()
    {
        var edition = Edition();
        var text = T("hud-v-edition-" + edition, _build.Version);
        var (fill, color) = edition switch
        {
            "release" => (HudUi.Ok with { A = 0.25 }, HudUi.Ok),
            "pre" => (HudUi.Warn with { A = 0.22 }, HudUi.Warn),
            _ => (HudUi.Accent, Rgba.White(1))
        };
        return (text, fill, color);
    }

    // The menu footer's chips: the short edition, then a newer version or a file that differs, by name; "checked" quietly
    private List<(string Text, Rgba Color)> VersionState()
    {
        var (_, _, color) = EditionBadge();
        List<(string, Rgba)> chips = [(T("hud-v-edition-short-" + Edition(), _build.Version), color)];
        if (_update?.Report is not { } r) return chips;
        if (r.State == UpdateState.Outdated) chips.Add((T("hud-v-check-newer", r.Newest), HudUi.Warn));
        else if (r.State == UpdateState.Mismatch) chips.Add((T("hud-v-check-mismatch"), HudUi.Err));
        else if (r.State == UpdateState.Verified) chips.Add((T("hud-v-check-verified"), HudUi.Ok));
        return chips;
    }

    private (string Text, Rgba Fill, Rgba Color)? CheckBadge()
    {
        if (_update is not { } check) return null;
        var r = check.Report;
        (string Key, Rgba Color) b = r.State switch
        {
            UpdateState.Verified => ("hud-v-check-verified", HudUi.Ok),
            UpdateState.Mismatch => ("hud-v-check-mismatch", HudUi.Err),
            UpdateState.Outdated => ("hud-v-check-outdated", HudUi.Warn),
            UpdateState.Checking => ("hud-v-check-running", HudUi.Dim),
            UpdateState.Unverified or UpdateState.NoRelease => ("hud-v-check-none", HudUi.Dim),
            _ => ("hud-v-check-failed", HudUi.Dim)
        };
        return (T(b.Key, r.Newest), b.Color with { A = 0.22 }, b.Color);
    }

    private void VersionView(HudUi ui)
    {
        var (text, fill, color) = EditionBadge();
        ui.Space(8);
        ui.Text(ui.X, ui.Y, ui.F.HugeRow, ui.F.Huge, "Komet " + _build.Version);
        var w = ui.TextW(ui.F.Small, text) + ui.Px(12);
        ui.Fill(ui.X + ui.W - w, ui.Y + (ui.F.HugeRow - ui.F.SmallRow - ui.Px(4)) / 2, w, ui.F.SmallRow + ui.Px(4), fill, ui.Px(3));
        ui.Text(ui.X + ui.W - w + ui.Px(6), ui.Y + (ui.F.HugeRow - ui.F.SmallRow) / 2, ui.F.SmallRow, ui.F.Small, text, color);
        ui.Y += ui.F.HugeRow;
        ui.Line(T("hud-v-edition-about-" + Edition()), HudUi.Dim, ui.F.Small);
        var built = KometText.LocalTime(BuiltStamp());
        ui.Space(6);
        ui.Grid(3, 3, 5, (i, x, y, cw) => i switch
        {
            0 => ui.Stat(x, y, cw, T("hud-v-ver-commit"), _build.Commit.Length > 0 ? _build.Commit : "–"),
            1 => ui.Stat(x, y, cw, T("hud-v-ver-built"), built.Length > 0 ? built : T("hud-v-ver-local")),
            _ => ui.Stat(x, y, cw, T("hud-v-ver-file"), Path.GetFileName(_build.SourcePath) is { Length: > 0 } file ? file : "–")
        });
        CheckSection(ui);
    }

    private void CheckSection(HudUi ui)
    {
        ui.Header(T("hud-v-ver-check"));
        if (_update is not { } check)
        {
            ui.Wrap(T("hud-v-ver-check-off"), HudUi.Dim, ui.F.Small);
            ui.Space(6);
            _ = ui.Go(ui.X, ui.Y, T("hud-v-ver-check-now"), RunUpdateCheck);
            ui.Y += ui.GoHeight;
            return;
        }

        var r = check.Report;
        var (notice, noticeColor) = r.Notice();
        var dot = ui.Dot(ui.X, ui.Y, ui.F.BodyRow, noticeColor ?? HudUi.Dim) + ui.Px(6);
        ui.Text(ui.X + dot, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, KometText.Translate(notice, r.Detail), ui.W - dot), noticeColor);
        ui.Y += ui.F.BodyRow + ui.Px(2);
        var none = KometText.Translate("verify-none");
        ui.Row(KometText.Translate("verify-build"), check.BuildTag, HudUi.Dim);
        ui.Row(KometText.Translate("verify-release"), r.Tag.Length > 0 ? r.Tag : none, HudUi.Dim);
        ui.Row(KometText.Translate("verify-released"), r.Released.Length > 0 ? r.Released : none, HudUi.Dim);
        ui.Row(KometText.Translate("verify-newest"), r.Newest.Length > 0 ? r.Newest : none, HudUi.Dim);
        Hashes(ui, r);
        var verdict = r.Verdict();
        var named = r.Tag.Length > 0 ? r.Tag : check.BuildTag;
        var verdictColor = r.State == UpdateState.Mismatch ? HudUi.Err : HudUi.Soft;
        if (r.Match) verdictColor = HudUi.Ok;
        ui.Space(4);
        ui.Wrap(KometText.Translate(verdict, r.State == UpdateState.Failed ? r.Detail : named), verdictColor, ui.F.Small);
        ui.Space(6);
        _ = ui.Go(ui.X, ui.Y, KometText.Translate("verify-recheck"), check.Start, enabled: r.State != UpdateState.Checking);
        ui.Y += ui.GoHeight;
    }

    private static void Hashes(HudUi ui, UpdateReport r)
    {
        ui.Label(KometText.Translate("verify-sha256"));
        var cell = ui.TextW(ui.F.Mono, "0");
        var label = Math.Max(ui.TextW(ui.F.Small, KometText.Translate("verify-installed")), ui.TextW(ui.F.Small, KometText.Translate("verify-github")))
                    + ui.Px(10);
        foreach (var (key, hash, other) in (ReadOnlySpan<(string, string, string)>)[("verify-installed", r.Installed, r.Published),
                     ("verify-github", r.Published, r.Installed)])
        {
            ui.Text(ui.X, ui.Y, ui.F.MonoRow, ui.F.Small, KometText.Translate(key), HudUi.Dim);
            if (hash.Length != UpdateReport.HashLength || cell * hash.Length > ui.W - label)
                ui.Text(ui.X + label, ui.Y, ui.F.MonoRow, ui.F.Mono, hash.Length > 0 ? ui.Fit(ui.F.Mono, hash, ui.W - label) : "–", HudUi.Dim);
            else
                for (var i = 0; i < UpdateReport.HashLength; i++)
                {
                    Rgba? color = null;
                    if (other.Length == UpdateReport.HashLength) color = hash[i] == other[i] ? HudUi.Ok : HudUi.Err;
                    ui.Text(ui.X + label + i * cell, ui.Y, ui.F.MonoRow, ui.F.Mono, hash[i..(i + 1)], color);
                }

            ui.Y += ui.F.MonoRow;
        }
    }
}

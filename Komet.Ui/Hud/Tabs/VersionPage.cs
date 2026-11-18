using Komet.Core;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Behind the title's badges, not in the tab bar: this build (version, edition, commit, file) and its update check
internal sealed class VersionPage(HudBuild build) : IHudTab
{
    public string Id => "version";

    public void Draw(HudUi ui, HudBody body)
    {
        var (text, fill, color) = build.EditionBadge();
        ui.Space(8);
        ui.Text(ui.X, ui.Y, ui.F.HugeRow, ui.F.Huge, "Komet " + build.Version);
        var w = ui.TextW(ui.F.Small, text) + ui.Px(12);
        ui.Fill(ui.X + ui.W - w, ui.Y + (ui.F.HugeRow - ui.F.SmallRow - ui.Px(4)) / 2, w, ui.F.SmallRow + ui.Px(4),
            fill, ui.Px(3));
        ui.Text(ui.X + ui.W - w + ui.Px(6), ui.Y + (ui.F.HugeRow - ui.F.SmallRow) / 2, ui.F.SmallRow, ui.F.Small, text,
            color);
        ui.Y += ui.F.HugeRow;
        ui.Line(T("hud-v-edition-about-" + HudBuild.Edition()), HudUi.Dim, ui.F.Small);
        var built = KometText.LocalTime(HudBuild.BuiltStamp());
        ui.Space(6);
        ui.Grid(3, 3, 5, (i, x, y, cw) => i switch
        {
            0 => ui.Stat(x, y, cw, T("hud-v-ver-commit"), build.Commit.Length > 0 ? build.Commit : "–"),
            1 => ui.Stat(x, y, cw, T("hud-v-ver-built"), built.Length > 0 ? built : T("hud-v-ver-local")),
            _ => ui.Stat(x, y, cw, T("hud-v-ver-file"),
                Path.GetFileName(build.SourcePath) is { Length: > 0 } file ? file : "–")
        });
        CheckSection(ui);
    }

    private void CheckSection(HudUi ui)
    {
        ui.Header(T("hud-v-ver-check"));
        if (build.Update is not { } check)
        {
            ui.Wrap(T("hud-v-ver-check-off"), HudUi.Dim, ui.F.Small);
            ui.Space(6);
            _ = ui.Go(ui.X, ui.Y, T("hud-v-ver-check-now"), build.RunUpdateCheck);
            ui.Y += ui.GoHeight;
            return;
        }

        var r = check.Report;
        var (notice, noticeColor) = r.Notice();
        var dot = ui.Dot(ui.X, ui.Y, ui.F.BodyRow, noticeColor ?? HudUi.Dim) + ui.Px(6);
        ui.Text(ui.X + dot, ui.Y, ui.F.BodyRow, ui.F.Body,
            ui.Fit(ui.F.Body, KometText.Translate(notice, r.Detail), ui.W - dot), noticeColor);
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
        ui.Wrap(KometText.Translate(verdict, r.State == UpdateState.Failed ? r.Detail : named), verdictColor,
            ui.F.Small);
        ui.Space(6);
        _ = ui.Go(ui.X, ui.Y, KometText.Translate("verify-recheck"), check.Start,
            enabled: r.State != UpdateState.Checking);
        ui.Y += ui.GoHeight;
    }

    private static void Hashes(HudUi ui, UpdateReport r)
    {
        ui.Label(KometText.Translate("verify-sha256"));
        var cell = ui.TextW(ui.F.Mono, "0");
        var label = Math.Max(ui.TextW(ui.F.Small, KometText.Translate("verify-installed")),
                        ui.TextW(ui.F.Small, KometText.Translate("verify-github")))
                    + ui.Px(10);
        foreach (var (key, hash, other) in (ReadOnlySpan<(string, string, string)>)
                 [
                     ("verify-installed", r.Installed, r.Published),
                     ("verify-github", r.Published, r.Installed)
                 ])
        {
            ui.Text(ui.X, ui.Y, ui.F.MonoRow, ui.F.Small, KometText.Translate(key), HudUi.Dim);
            if (hash.Length != UpdateReport.HashLength || cell * hash.Length > ui.W - label)
            {
                ui.Text(ui.X + label, ui.Y, ui.F.MonoRow, ui.F.Mono,
                    hash.Length > 0 ? ui.Fit(ui.F.Mono, hash, ui.W - label) : "–", HudUi.Dim);
            }
            else
            {
                for (var i = 0; i < UpdateReport.HashLength; i++)
                {
                    Rgba? color = null;
                    if (other.Length == UpdateReport.HashLength) color = hash[i] == other[i] ? HudUi.Ok : HudUi.Err;

                    ui.Text(ui.X + label + i * cell, ui.Y, ui.F.MonoRow, ui.F.Mono, hash[i..(i + 1)], color);
                }
            }

            ui.Y += ui.F.MonoRow;
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;

namespace Komet.Hud;

// Checksum window: the sha256 of the installed zip next to the one CI published for the same release, character by character.
internal sealed class HudVerifyDialog(
    ICoreClientAPI capi,
    HudSettings settings,
    HudFonts fonts,
    SnapGrid snap,
    Func<UpdateCheck?> update,
    Action recheck) : HudDialog(capi, settings, fonts, snap, "verify")
{
    private const double ButtonGap = 8;
    private const int HashLength = SHA256.HashSizeInBytes * 2;
    private UpdateReport? _shown;

    // A new report (a check finished, or "check again" started one) redraws
    public override void OnRenderGUI(float deltaTime)
    {
        var report = update()?.Report;
        if (!ReferenceEquals(report, _shown)) (_shown, Dirty) = (report, true);
        base.OnRenderGUI(deltaTime);
    }

    // What the comparison says, as a lang key and a colour
    internal static (string Key, Rgba Color) Verdict(UpdateReport r)
    {
        return r.State switch
        {
            UpdateState.Checking => ("verify-checking", HudCanvas.Neutral),
            UpdateState.Failed => ("verify-failed", HudCanvas.Neutral),
            UpdateState.NoRelease => ("verify-norelease", HudCanvas.Warning),
            _ when r.Match => ("verify-match", HudCanvas.Good),
            _ when r.Installed.Length > 0 && r.Published.Length > 0 => ("verify-mismatch", HudCanvas.Error),
            _ when r.Tag.Length == 0 => ("verify-norelease", HudCanvas.Warning),
            _ when r.Installed.Length == 0 => ("verify-nofile", HudCanvas.Neutral),
            _ => ("verify-nochecksum", HudCanvas.Warning)
        };
    }

    protected override double Build()
    {
        var (r, check, none) = (_shown ?? UpdateReport.Pending, update(), Translate("none"));
        var cell = "0123456789abcdef".Max(digit =>
            Canvas.TextWidth(Fonts.Text, digit.ToString(CultureInfo.InvariantCulture)));
        if (!Assert(cell > 0)) return 0;
        var own = check is null ? none : check.BuildTag; // the build's own tag, also when GitHub has no release of it
        var named = r.Tag.Length > 0
            ? r.Tag
            : own; // what a verdict names: "no release {0}" names the one it looked for
        var (verdict, color) = Verdict(r);
        var text = HudText.Translate(verdict, r.State == UpdateState.Failed ? r.Detail : named);
        Title();
        Row("file",
            y => Canvas.Text(ControlX, y, RowH, Fonts.Text, check?.FileName is { Length: > 0 } file ? file : none));
        Row("build", y => Canvas.Text(ControlX, y, RowH, Fonts.Text, own));
        Row("release",
            y => Canvas.Text(ControlX, y, RowH, Fonts.Text, r.Tag.Length > 0 ? r.Tag : none,
                r.Tag.Length > 0 ? null : HudCanvas.Dim));
        Row("released", y => Canvas.Text(ControlX, y, RowH, Fonts.Text, r.Released.Length > 0 ? r.Released : none,
            r.Released.Length > 0 ? null : HudCanvas.Dim));
        Header("sha256");
        Row("installed", y => Hash(y, cell, r.Installed, r.Published));
        Row("github", y => Hash(y, cell, r.Published, ""));
        Header("result");
        Rows.Add((RowH, y => Canvas.Text(Pad, y, RowH, Fonts.Text, text, color)));
        Row("newest", y => Newest(y, r, none));
        Rule();
        Buttons(r);
        return HashLength * cell;
    }

    private void Newest(double y, UpdateReport r, string none)
    {
        var newest = r.Newest.Length > 0 ? r.Newest : none;
        Canvas.Text(ControlX, y, RowH, Fonts.Text, newest, r.Newest.Length > 0 ? null : HudCanvas.Dim);
        if (r.State is UpdateState.Checking or UpdateState.Failed || r.Newest.Length == 0) return;
        var (key, color) = r.State == UpdateState.Outdated
            ? ("update", HudCanvas.Warning)
            : ("current", HudCanvas.Good);
        _ = Canvas.Badge(ControlX + Canvas.TextWidth(Fonts.Text, newest) + scaled(HudLine.BadgeGap), y, RowH, Fonts,
            Translate(key), color);
    }

    // Close at the right edge, check again beside it
    private void Buttons(UpdateReport r)
    {
        string[] buttons = [Translate("recheck"), Translate("close")];
        var width = buttons.Max(text => Canvas.BadgeWidth(Fonts, text));
        if (!Assert(width > 0) || !Assert(Rows.Count > 0)) return;
        Action<double> draw = y =>
        {
            var x = Width - Pad - width;
            Hits.Add(new HitBox(x, y, width, RowH, _ => TryClose()));
            _ = Canvas.Badge(x, y, RowH, Fonts, buttons[1], HudCanvas.Neutral, width);
            x -= width + scaled(ButtonGap);
            Hits.Add(new HitBox(x, y, width, RowH, _ => recheck()));
            _ = Canvas.Badge(x, y, RowH, Fonts, buttons[0],
                r.State == UpdateState.Checking ? HudCanvas.Neutral with { A = 0.4 } : HudCanvas.Accent,
                width);
        };
        Rows.Add((RowH, draw));
    }

    // One cell per hex digit so both hashes line up; with a reference to compare with, each digit is green or red by whether it matches
    private void Hash(double y, double cell, string hash, string reference)
    {
        if (hash.Length == 0)
        {
            Canvas.Text(ControlX, y, RowH, Fonts.Text, Translate("none"), HudCanvas.Dim);
            return;
        }

        if (!Assert(cell > 0) || !Assert(hash.Length == HashLength) ||
            !Assert(reference.Length is 0 or HashLength)) return;
        for (var i = 0; i < Math.Min(hash.Length, HashLength); i++)
        {
            Rgba? color = null;
            if (reference.Length > 0) color = hash[i] == reference[i] ? HudCanvas.Good : HudCanvas.Error;
            Canvas.Text(ControlX + i * cell, y, RowH, Fonts.Text, hash[i..(i + 1)], color);
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

// Checksum window: the sha256 of the installed zip next to the one CI published for the same release, character by character.
internal sealed class HudVerifyDialog : HudDialog
{
    private const double ButtonGap = 8;
    private const int HashLength = SHA256.HashSizeInBytes * 2;
    private static readonly Rgba Good = new(0.35, 0.75, 0.45, 1), Bad = HudCanvas.Error;
    private static readonly string[] Labels = ["file", "build", "release", "released", "installed", "github", "newest"];

    private readonly Func<UpdateCheck?> _update;
    private readonly Action _recheck;
    private UpdateReport? _shown;

    public HudVerifyDialog(ICoreClientAPI capi, HudSettings settings, Func<UpdateCheck?> update, Action recheck) : base(capi, settings, "verify")
    {
        _update = NotNull(update) ? update : static () => null;
        _recheck = NotNull(recheck) ? recheck : static () => { };
    }

    // A new report (a check finished, or "check again" started one) redraws
    public override void OnRenderGUI(float deltaTime)
    {
        var report = _update()?.Report;
        if (!ReferenceEquals(report, _shown)) (_shown, Dirty) = (report, true);
        base.OnRenderGUI(deltaTime);
    }

    protected override double Build()
    {
        var (s, r, check) = (Settings, _shown ?? UpdateReport.Pending, _update());
        string[] buttons = [Translate("recheck"), Translate("close")];
        var none = Translate("none");
        double labelW = LabelColumn(Labels), cell = 0, buttonW = 0;
        foreach (var digit in "0123456789abcdef".Bounded(16)) cell = Math.Max(cell, HudCanvas.TextWidth(s.Text, digit.ToString(CultureInfo.InvariantCulture)));
        foreach (var text in buttons.Bounded(MaxRows)) buttonW = Math.Max(buttonW, HudCanvas.BadgeWidth(s.Header, text));
        double valueX = Pad + labelW + Gap, width = valueX + (HashLength * cell) + Pad;
        if (!Assert(labelW > 0) || !Assert(cell > 0) || !Assert(buttonW > 0)) return 0;
        var (tag, newest) = (r.Tag.Length > 0 ? r.Tag : none, r.Newest.Length > 0 ? r.Newest : none);
        var (verdictKey, verdictColor) = r.State switch
        {
            UpdateState.Checking => ("checking", HudCanvas.Neutral),
            UpdateState.Failed => ("failed", HudCanvas.Neutral),
            _ when r.Match => ("match", Good),
            _ when r.Installed.Length > 0 && r.Published.Length > 0 => ("mismatch", Bad),
            _ when r.Tag.Length == 0 => ("norelease", HudCanvas.Warning),
            _ when r.Installed.Length == 0 => ("nofile", HudCanvas.Neutral),
            _ => ("nochecksum", HudCanvas.Warning),
        };
        var (newestKey, newestColor) = r.State == UpdateState.Outdated ? ("update", HudCanvas.Warning) : ("current", Good);

        Title();
        Row("file", y => Canvas.Text(valueX, y, RowH, s.Text, check?.FileName is { Length: > 0 } file ? file : none));
        Row("build", y => Canvas.Text(valueX, y, RowH, s.Text, Translate("build-text", check is null ? none : tag)));
        Row("release", y => Canvas.Text(valueX, y, RowH, s.Text, tag, r.Tag.Length > 0 ? null : HudCanvas.Dim));
        Row("released", y => Canvas.Text(valueX, y, RowH, s.Text, r.Released.Length > 0 ? r.Released : none, r.Released.Length > 0 ? null : HudCanvas.Dim));
        Header("sha256");
        Row("installed", y => Hash(y, r.Installed, r.Published));
        Row("github", y => Hash(y, r.Published, ""));
        Header("result");
        Rows.Add((RowH, y => Canvas.Text(Pad, y, RowH, s.Text, Translate(verdictKey, r.State == UpdateState.Failed ? r.Detail : r.Tag), verdictColor)));
        Row("newest", y =>
        {
            Canvas.Text(valueX, y, RowH, s.Text, newest, r.Newest.Length > 0 ? null : HudCanvas.Dim);
            if (r.State is not (UpdateState.Checking or UpdateState.Failed) && r.Newest.Length > 0)
                _ = Canvas.Badge(valueX + HudCanvas.TextWidth(s.Text, newest) + scaled(HudLine.BadgeGap), y, RowH, s.Header, Translate(newestKey), newestColor);
        });
        Rule();
        Rows.Add((RowH, y =>
        {
            var x = width - Pad - buttonW;
            Hits.Add(new HitBox(x, y, buttonW, RowH, _ => TryClose()));
            _ = Canvas.Badge(x, y, RowH, s.Header, buttons[1], HudCanvas.Neutral, buttonW);
            x -= buttonW + scaled(ButtonGap);
            Hits.Add(new HitBox(x, y, buttonW, RowH, _ => _recheck()));
            _ = Canvas.Badge(x, y, RowH, s.Header, buttons[0], r.State == UpdateState.Checking ? HudCanvas.Neutral with { A = 0.4 } : HudCanvas.Accent, buttonW);
        }));
        return width;

        // One cell per hex digit so both hashes line up; with a reference to compare with, each digit is green or red by whether it matches
        void Hash(double y, string hash, string reference)
        {
            if (hash.Length == 0) { Canvas.Text(valueX, y, RowH, s.Text, none, HudCanvas.Dim); return; }
            if (!Assert(hash.Length == HashLength) || !Assert(reference.Length is 0 or HashLength)) return;
            for (var i = 0; i < Math.Min(hash.Length, HashLength); i++)
            {
                Rgba? color = null;
                if (reference.Length > 0) color = hash[i] == reference[i] ? Good : Bad;
                Canvas.Text(valueX + (i * cell), y, RowH, s.Text, hash[i..(i + 1)], color);
            }
        }
    }
}

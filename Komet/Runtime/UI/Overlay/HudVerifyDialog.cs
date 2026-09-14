using System.Security.Cryptography;
using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

// Checksum window: the sha256 of the installed zip next to the one CI published for the same release, character by character.
internal sealed class HudVerifyDialog : GuiDialog
{
    private const double RowGap = 4, ButtonGap = 8;
    private const int HashLength = SHA256.HashSizeInBytes * 2, MaxRows = 16;
    private static readonly Rgba Good = new(0.35, 0.75, 0.45, 1), Bad = HudCanvas.Error;

    private sealed record HitBox(double X, double Y, double W, double H, Action Click);

    private readonly HudSettings _settings;
    private readonly HudCanvas _canvas;
    private readonly Func<UpdateCheck?> _update;
    private readonly Action _recheck;
    private readonly List<HitBox> _hits = [];
    private UpdateReport? _shown;

    public HudVerifyDialog(ICoreClientAPI capi, HudSettings settings, Func<UpdateCheck?> update, Action recheck) : base(capi)
    {
        (_settings, _update, _recheck) = (settings, update, recheck);
        _canvas = new HudCanvas(capi);
        if (NotNull(settings) && NotNull(update)) settings.Changed += () => _shown = null;
    }

    public override string? ToggleKeyCombinationCode => null;
    public bool Contains(double px, double py) => IsOpened() && _canvas.Contains(px, py);
    public override void OnGuiOpened() => _shown = null;

    public override void OnRenderGUI(float deltaTime)
    {
        var report = _update()?.Report;
        if (!Finite(deltaTime) || !Assert(capi.Render.FrameWidth > 0)) return;
        if (!ReferenceEquals(report, _shown) || _shown is null) Render(report);
        _canvas.Draw(Math.Round((capi.Render.FrameWidth - _canvas.Width) / 2), Math.Round((capi.Render.FrameHeight - _canvas.Height) / 2));
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (args.Button != EnumMouseButton.Left || !Assert(_canvas.Width > 0) || !Assert(_canvas.Height > 0)) return;
        double x = args.X - _canvas.X, y = args.Y - _canvas.Y;
        _hits.Find(h => x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H)?.Click();
    }

    public override void Dispose()
    {
        base.Dispose();
        _canvas.Dispose();
    }

    private void Render(UpdateReport? report)
    {
        _shown = report ?? UpdateReport.Pending;
        _hits.Clear();
        var s = _settings;
        var check = _update();
        double pad = scaled(HudPanel.Padding), gap = scaled(HudPanel.Gap), buttonGap = scaled(ButtonGap);
        double rowH = HudCanvas.BadgeHeight(s.Header) + scaled(RowGap), headerRow = HudCanvas.HeaderHeight(s.Header);
        var titleRow = Math.Max(HudCanvas.LineHeight(s.Title), HudCanvas.BadgeHeight(s.Header)) + scaled(RowGap);
        if (!Assert(rowH > 0) || !Assert(headerRow > 0) || !Assert(titleRow > 0)) return;
        string[] labels = ["file", "build", "release", "released", "installed", "github", "newest"];
        string[] buttons = [HudSettings.Translate("verify-recheck"), HudSettings.Translate("verify-close")];
        var none = HudSettings.Translate("verify-none");
        double labelW = 0, cell = 0, buttonW = 0;
        foreach (var key in labels.Bounded(MaxRows)) labelW = Math.Max(labelW, HudCanvas.TextWidth(s.Text, HudSettings.Translate("verify-" + key)));
        foreach (var digit in "0123456789abcdef".Bounded(16)) cell = Math.Max(cell, HudCanvas.TextWidth(s.Text, digit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var text in buttons.Bounded(MaxRows)) buttonW = Math.Max(buttonW, HudCanvas.BadgeWidth(s.Header, text));
        double valueX = pad + labelW + gap, width = valueX + (HashLength * cell) + pad, right = width - pad;
        if (!Assert(labelW > 0) || !Assert(cell > 0) || !Assert(buttonW > 0)) return;

        var (state, tag, newest) = (_shown.State, _shown.Tag.Length > 0 ? _shown.Tag : none, _shown.Newest.Length > 0 ? _shown.Newest : none);
        var (verdictKey, verdictColor) = state switch
        {
            UpdateState.Checking => ("verify-checking", HudCanvas.Neutral),
            UpdateState.Failed => ("verify-failed", HudCanvas.Neutral),
            _ when _shown.Match => ("verify-match", Good),
            _ when _shown.Installed.Length > 0 && _shown.Published.Length > 0 => ("verify-mismatch", Bad),
            _ when _shown.Tag.Length == 0 => ("verify-norelease", HudCanvas.Warning),
            _ when _shown.Installed.Length == 0 => ("verify-nofile", HudCanvas.Neutral),
            _ => ("verify-nochecksum", HudCanvas.Warning),
        };
        var verdict = HudSettings.Translate(verdictKey, state == UpdateState.Failed ? _shown.Detail : _shown.Tag);
        var (newestKey, newestColor) = state == UpdateState.Outdated ? ("verify-update", HudCanvas.Warning) : ("verify-current", Good);

        var rows = new List<(double Height, Action<double> Draw)>
        {
            (titleRow, y =>
            {
                var title = HudSettings.Translate("hud-title");
                _canvas.Text(pad, y, titleRow, s.Title, title);
                _ = _canvas.Badge(pad + HudCanvas.TextWidth(s.Title, title) + scaled(HudLine.BadgeGap), y, titleRow, s.Header, HudSettings.Translate("verify-title"), HudCanvas.Accent);
                _canvas.Text(right - HudCanvas.TextWidth(s.Title, "×"), y, titleRow, s.Title, "×");
                _hits.Add(new HitBox(right - titleRow, y, titleRow, titleRow, () => TryClose()));
            }),
        };
        Row("file", y => _canvas.Text(valueX, y, rowH, s.Text, check?.FileName is { Length: > 0 } file ? file : none));
        Row("build", y => _canvas.Text(valueX, y, rowH, s.Text, HudSettings.Translate("verify-build-text", check is null ? none : tag)));
        Row("release", y => _canvas.Text(valueX, y, rowH, s.Text, tag, _shown.Tag.Length > 0 ? null : HudCanvas.Dim));
        Row("released", y => _canvas.Text(valueX, y, rowH, s.Text, _shown.Released.Length > 0 ? _shown.Released : none, _shown.Released.Length > 0 ? null : HudCanvas.Dim));
        Header("sha256");
        Row("installed", y => Hash(y, _shown.Installed, _shown.Published));
        Row("github", y => Hash(y, _shown.Published, null));
        Header("result");
        rows.Add((rowH, y => _canvas.Text(pad, y, rowH, s.Text, verdict, verdictColor)));
        Row("newest", y =>
        {
            _canvas.Text(valueX, y, rowH, s.Text, newest, _shown.Newest.Length > 0 ? null : HudCanvas.Dim);
            if (state is not (UpdateState.Checking or UpdateState.Failed) && _shown.Newest.Length > 0)
                _ = _canvas.Badge(valueX + HudCanvas.TextWidth(s.Text, newest) + scaled(HudLine.BadgeGap), y, rowH, s.Header, HudSettings.Translate(newestKey), newestColor);
        });
        rows.Add((HudCanvas.RuleHeight, y => _canvas.Rule(pad, y, width - (2 * pad), HudCanvas.RuleHeight)));
        rows.Add((rowH, y =>
        {
            var x = right - buttonW;
            _hits.Add(new HitBox(x, y, buttonW, rowH, () => TryClose()));
            _ = _canvas.Badge(x, y, rowH, s.Header, buttons[1], HudCanvas.Neutral, buttonW);
            x -= buttonW + buttonGap;
            _hits.Add(new HitBox(x, y, buttonW, rowH, _recheck));
            _ = _canvas.Badge(x, y, rowH, s.Header, buttons[0], state == UpdateState.Checking ? HudCanvas.Neutral with { A = 0.4 } : HudCanvas.Accent, buttonW);
        }));

        var height = 2 * pad;
        foreach (var (rowHeight, _) in rows.Bounded(MaxRows)) height += rowHeight;
        if (!Assert(rows.Count <= MaxRows) || !Assert(height > 0)) return;
        _canvas.Begin(width, height);
        if (!Assert(_canvas.Width >= width)) return;
        _canvas.Fill(0, 0, width, height, HudCanvas.PanelBackground with { A = Math.Max(s.Opacity, 0.9) }, scaled(4));
        var top = pad;
        foreach (var (rowHeight, draw) in rows.Bounded(MaxRows))
        {
            draw(top);
            top += rowHeight;
        }
        _canvas.End();
        return;

        void Header(string key)
        {
            if (!Assert(key.Length > 0) || !Assert(rows.Count > 0)) return;
            rows.Add((HudCanvas.RuleHeight, y => _canvas.Rule(pad, y, width - (2 * pad), HudCanvas.RuleHeight)));
            rows.Add((headerRow, y => _canvas.Header(pad, y, width - (2 * pad), headerRow, s.Header, HudSettings.Translate("verify-" + key))));
        }

        void Row(string key, Action<double> value)
        {
            if (!Assert(Array.IndexOf(labels, key) >= 0) || !Assert(rows.Count > 0)) return;   // the label column is measured from labels
            rows.Add((rowH, y =>
            {
                _canvas.Text(pad, y, rowH, s.Text, HudSettings.Translate("verify-" + key));
                value(y);
            }));
        }

        // One cell per hex digit so both hashes line up; with a reference, each digit is green or red by whether it matches.
        void Hash(double y, string hash, string? reference)
        {
            if (hash.Length == 0) { _canvas.Text(valueX, y, rowH, s.Text, none, HudCanvas.Dim); return; }
            if (!Assert(hash.Length == HashLength) || !Assert(reference is null || reference.Length == HashLength)) return;
            for (var i = 0; i < Math.Min(hash.Length, HashLength); i++)
            {
                Rgba? color = null;
                if (reference is not null) color = hash[i] == reference[i] ? Good : Bad;
                _canvas.Text(valueX + (i * cell), y, rowH, s.Text, hash[i..(i + 1)], color);
            }
        }
    }
}

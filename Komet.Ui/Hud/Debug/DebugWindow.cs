using Komet.Core;
namespace Komet.Hud;

// The debug window (Ctrl+F8): head with language and copy, capture controls or progress, section jumps, the last
// protocol in a scrolling pane, footer.
internal sealed class DebugWindow : HudDialog
{
    private const int MaxLines = 8192, MaxSections = 64;
    private const double RefreshMs = 250, MaxWidth = 860, MaxHeight = 600;
    private const float Depth = 70;
    private static readonly int[] Durations = [10, 30, 60, HudOverlay.MaxSeconds];
    private static readonly int[] Thresholds = [25, 50, 100];

    private readonly HudSettings _settings;
    private readonly HudOverlay _hud;
    private string[] _lines = [];
    private (int Line, string Title)[] _sections = [];
    private string _shown = "", _mode = "record";
    private int _seconds = 30, _threshold = 50;
    private double _scroll, _paneTop, _paneH;

    public DebugWindow(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, HudOverlay hud, SnapGrid snap)
        : base(capi, settings, fonts, snap, Depth)
    {
        (_settings, _hud) = (settings, hud);
    }

    public override double DrawOrder => 0.91;

    public void Toggle(bool show = false)
    {
        Show(!show);
        Invalidate(true);
    }

    // A running capture redraws its progress every RefreshMs
    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !IsOpened()) return;
        var size = Fitted(MaxWidth, MaxHeight);
        if (!ReferenceEquals(_hud.LastProtocol, _shown)) Load(_hud.LastProtocol);
        if (_hud.DebugProgress.Running) Invalidate();
        if (Recompose(size, RefreshMs)) Compose(size);
        if (!Canvas.Ready) return;
        Present(_settings.DebugX, _settings.DebugY);
        Hover();
    }

    private void Load(string text)
    {
        _shown = text;
        _lines = text.Length == 0 ? [] : [.. text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Take(MaxLines)];
        var sections = new List<(int, string)> { (0, T("hud-v-dbg-head")) };
        for (var i = 0; i < Math.Min(_lines.Length, MaxLines) && sections.Count < MaxSections; i++)
            if (_lines[i].StartsWith('[') && _lines[i].IndexOf(']', StringComparison.Ordinal) is var close and > 1)
                sections.Add((i, _lines[i][(close + 1)..].Trim().TrimEnd('=').Trim()));
        (_sections, _scroll) = ([.. sections], 0);
        Invalidate(true);
    }

    private static string T(string key, params object[] args) => KometText.Translate(key, args);

    private void Compose((double W, double H) size)
    {
        HudUi.Ground(Canvas, size.W, size.H, HudUi.Back with { A = 0.97 }, scaled(4));
        var ui = new HudUi(Canvas, Fonts, (scaled(10), size.W - scaled(20), 0, size.H, 0), Hits) { Y = scaled(6) };
        Head(ui, size.W);
        Band(ui, size.W);
        Controls(ui);
        Band(ui, size.W);
        if (_lines.Length > 0)
        {
            Jumps(ui);
            Band(ui, size.W);
        }

        var footer = Fonts.SmallRow + scaled(8);
        Pane(size, ui.Y, size.H - ui.Y - footer);
        Footer(new HudUi(Canvas, Fonts, (scaled(10), size.W - scaled(20), 0, size.H, 0), Hits) { Y = size.H - footer }, size.W);
        Canvas.End(Hits);
    }

    private static void Band(HudUi ui, double width)
    {
        ui.Space(6);
        ui.Fill(0, ui.Y, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(6);
    }

    // "Close" and its cross are one hit box reaching the window's right edge
    private void Head(HudUi ui, double width)
    {
        ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "Komet Debug");
        ui.Text(ui.X + ui.TextW(ui.F.Strong, "Komet Debug") + ui.Px(6), ui.Y, ui.F.StrongRow, ui.F.Small, DebugFiles.Protocol, HudUi.Dim);
        var right = ui.X + ui.W;
        var close = T("hud-v-dbg-close");
        var cross = ui.Px(8);
        ui.Cross(right - cross, ui.Y + (ui.F.StrongRow - cross) / 2, cross, HudUi.Link);
        var link = right - cross - ui.Px(5) - ui.TextW(ui.F.Body, close);
        ui.Text(link, ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, ui.F.BodyRow, ui.F.Body, close, HudUi.Link);
        ui.Click(link, ui.Y, width - link, ui.F.StrongRow, () => TryClose());
        right = link - ui.Px(8);
        ui.Text(right - ui.TextW(ui.F.Body, "·"), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, ui.F.BodyRow, ui.F.Body, "·", HudUi.Dim);
        right -= ui.TextW(ui.F.Body, "·") + ui.Px(8);
        var copy = T("hud-v-dbg-copy");
        right -= ui.LinkText(right - ui.TextW(ui.F.Body, copy), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, copy, Copy) + ui.Px(14);
        var chips = ui.TextW(ui.F.Small, "English") + ui.TextW(ui.F.Small, "Deutsch") + ui.Px(32);
        _ = ui.Chips(right - chips, ui.Y + (ui.F.StrongRow - ui.ChipHeight) / 2, [("English", _settings.DebugLanguage == "en",
            () => Language("en")), ("Deutsch", _settings.DebugLanguage == "de", () => Language("de"))]);
        ui.Y += ui.F.StrongRow;
    }

    private void Language(string code)
    {
        if (code == _settings.DebugLanguage) return;
        var section = _sections.Length > 0 ? Current() : 0;
        _settings.DebugLanguage = code;
        _hud.Relabel(code);
        Load(_hud.LastProtocol);
        if (Index(section, _sections.Length)) _scroll = _sections[section].Line * Fonts.MonoRow;
    }

    private void Copy()
    {
        if (_shown.Length == 0 || !NotNull(capi.Input)) return;
        capi.Input.ClipboardText = _shown;
        capi.ShowChatMessage(T("hud-debug-copied"));
    }

    private void Controls(HudUi ui)
    {
        var progress = _hud.DebugProgress;
        if (progress.Running) Running(ui, progress);
        else Setup(ui);
    }

    private void Setup(HudUi ui)
    {
        var start = T(_mode switch { "now" => "hud-v-dbg-start-now", "spike" => "hud-v-dbg-start-spike", _ => "hud-v-dbg-start-record" });
        var x = ui.Chips(ui.X, ui.Y + ui.Px(3), [(T("hud-v-dbg-now"), _mode == "now", () => _mode = "now"),
            (T("hud-v-dbg-record"), _mode == "record", () => _mode = "record"), (T("hud-v-dbg-spike"), _mode == "spike", () => _mode = "spike")]);
        x += ui.Px(12);
        if (_mode == "record")
            foreach (var seconds in Durations.Bounded(4))
            {
                var label = seconds < HudOverlay.MaxSeconds ? seconds + " s" : T("hud-v-dbg-max-min", HudOverlay.MaxSeconds / 60);
                x += ui.Chip(x, ui.Y + ui.Px(3), label, _seconds == seconds, () => _seconds = seconds) + ui.Px(4);
            }
        else if (_mode == "spike")
            foreach (var ms in Thresholds.Bounded(3))
                x += ui.Chip(x, ui.Y + ui.Px(3), "> " + ms + " ms", _threshold == ms, () => _threshold = ms) + ui.Px(4);
        else
        {
            ui.Text(x, ui.Y + ui.Px(3), ui.ChipHeight, ui.F.Small, T("hud-v-dbg-now-time"), HudUi.Dim);
            x += ui.TextW(ui.F.Small, T("hud-v-dbg-now-time")) + ui.Px(4);
        }
        var (go, join) = (ui.X + ui.W - ui.TextW(ui.F.Strong, start) - ui.Px(24), T("hud-v-dbg-join"));
        // Right-aligned against Start; left out when it would meet the chips
        var joinW = ui.TextW(ui.F.Small, join) + ui.Px(26);
        var joinX = go - joinW;
        if (joinX >= x + ui.Px(8))
            _ = ui.Chip(joinX, ui.Y + ui.Px(3), join, _settings.DebugOnJoin, () => _settings.DebugOnJoin = !_settings.DebugOnJoin);
        _ = ui.Go(go, ui.Y, start, Start);
        ui.Y += ui.GoHeight + ui.Px(4);
        Result(ui);
    }

    private void Start()
    {
        _hud.Capture(_mode, _mode == "record" ? _seconds : 0, _threshold);
    }

    private void Result(HudUi ui)
    {
        if (_lines.Length == 0)
        {
            var key = _mode switch { "now" => "hud-v-dbg-about-now", "spike" => "hud-v-dbg-about-spike", _ => "hud-v-dbg-about-record" };
            ui.Line(T(key) + " · " + Command(), HudUi.Dim, ui.F.Small);
            return;
        }

        var check = ui.Check(ui.X, ui.Y, ui.F.SmallRow, HudUi.Ok) + ui.Px(5);
        ui.Text(ui.X + check, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-dbg-done", _lines.Length), HudUi.Ok);
        ui.Y += ui.F.SmallRow;
        var compare = _hud.LastCompare;
        if (compare.Rows.Length == 0) return;
        var x = ui.X;
        var lead = T("hud-v-dbg-against", compare.Stamp) + " ";
        ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, lead, HudUi.Dim);
        x += ui.TextW(ui.F.Small, lead);
        foreach (var (key, before, now) in compare.Rows.Bounded(16))
            if (Change(key, before, now) is { } part && x + ui.TextW(ui.F.Small, part.Text + part.Delta) < ui.X + ui.W)
            {
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Text);
                x += ui.TextW(ui.F.Small, part.Text);
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Delta, part.Better ? HudUi.Ok : HudUi.Err);
                x += ui.TextW(ui.F.Small, part.Delta) + ui.Px(8);
            }

        ui.Y += ui.F.SmallRow;
        if (compare.Unlike.Length > 0) ui.Line(T("hud-v-dbg-unlike", compare.Unlike), HudUi.Warn, ui.F.Small);
    }

    // "Ø 7,12 ms" and "−0,41"; null for a number the line leaves out
    private static (string Text, string Delta, bool Better)? Change(string key, double before, double now)
    {
        if (!double.IsFinite(before) || !double.IsFinite(now)) return null;
        var (label, decimals, lowerBetter) = key switch
        {
            "avg" => ("Ø", 2, true), "low1" => ("1% Low", 0, false), "worst" => ("max", 0, true), "over50" => (">50 ms %", 1, true),
            "gc" => ("GC %", 1, true), "alloc" => ("MB/s", 0, true), _ => ("", 0, true)
        };
        if (label.Length == 0) return null;
        var delta = now - before;
        var sign = delta >= 0 ? "+" : "−";
        return ($"{label} {KometText.Num(now, decimals)} ", sign + KometText.Num(Math.Abs(delta), decimals), lowerBetter ? delta <= 0 : delta >= 0);
    }

    private string Command()
    {
        var language = _settings.DebugLanguage == "de" ? " de" : "";
        var mode = _mode switch { "now" => " now", "spike" => " spike " + _threshold, _ => " " + _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return ".komet debug" + mode + language; // a client command: "/" goes to the server
    }

    private void Running(HudUi ui, (bool Running, bool Armed, double Elapsed, double Total, long Frames, int Spikes) p)
    {
        var h = ui.GoHeight;
        ui.Fill(ui.X, ui.Y + h / 2 - ui.Px(3.5), ui.Px(7), ui.Px(7), p.Armed ? HudUi.Warn : HudUi.Err, ui.Px(3.5));
        var state = p.Frames == 0 ? T("hud-debug-writing") : T("hud-v-dbg-recording-open", Clock(p.Elapsed));
        if (p.Total > 0) state = T("hud-v-dbg-recording", Clock(p.Elapsed), Clock(p.Total));
        if (p.Armed) state = T("hud-v-dbg-armed", _threshold);
        ui.Text(ui.X + ui.Px(12), ui.Y, h, ui.F.Body, state);
        var stop = T(p.Armed ? "hud-v-dbg-cancel" : "hud-v-dbg-stop");
        var stopX = ui.X + ui.W - ui.TextW(ui.F.Strong, stop) - ui.Px(24);
        var stats = T("hud-v-dbg-stats", KometText.Num(p.Frames), p.Spikes);
        ui.TextRight(stopX - ui.Px(12), ui.Y, h, ui.F.Small, stats, HudUi.Dim);
        if (p.Frames > 0) _ = ui.Go(stopX, ui.Y, stop, _hud.EndCapture, stop: true);
        ui.Y += h + ui.Px(5);
        var share = p.Total > 0 ? Math.Clamp(p.Elapsed / p.Total, 0, 1) : 0;
        ui.Fill(ui.X, ui.Y, ui.W, ui.Px(4), Rgba.White(0.1), ui.Px(2));
        ui.Fill(ui.X, ui.Y, ui.W * share, ui.Px(4), HudUi.Stop, ui.Px(2));
        ui.Y += ui.Px(4);
    }

    private static string Clock(double seconds) =>
        double.IsFinite(seconds) ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : "0:00";

    private void Jumps(HudUi ui)
    {
        var current = Current();
        ui.Text(ui.X, ui.Y, ui.ChipHeight, ui.F.Small, T("hud-v-dbg-jump"), HudUi.Dim);
        var x = ui.X + ui.TextW(ui.F.Small, T("hud-v-dbg-jump")) + ui.Px(6);
        for (var i = 0; i < Math.Min(_sections.Length, MaxSections); i++)
        {
            var (line, _) = _sections[i];
            var label = i == 0 ? T("hud-v-dbg-head") : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            x += ui.Chip(x, ui.Y, label, i == current, () => _scroll = line * Fonts.MonoRow) + ui.Px(2);
        }

        ui.Text(x + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small, ui.Fit(ui.F.Small, _sections[current].Title, ui.X + ui.W - x - ui.Px(6)));
        ui.Y += ui.ChipHeight;
    }

    private int Current()
    {
        var top = (int)(_scroll / Math.Max(1, Fonts.MonoRow));
        var current = 0;
        for (var i = 0; i < Math.Min(_sections.Length, MaxSections); i++)
            if (_sections[i].Line <= top + 1) current = i;
        return current;
    }

    private void Pane((double W, double H) size, double top, double height)
    {
        if (!Assert(height > 0)) return;
        (_paneTop, _paneH) = (top, height);
        var content = _lines.Length * Fonts.MonoRow + scaled(20);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, content - height));
        Canvas.Clip(0, top, size.W, height);
        var ui = new HudUi(Canvas, Fonts, (scaled(12), size.W - scaled(36), top + scaled(6), top + height, _scroll), Hits);
        if (_lines.Length == 0)
        {
            ui.Space(30);
            ui.Wrap(T(_hud.DebugProgress.Running ? "hud-v-dbg-pane-waiting" : "hud-v-dbg-pane-empty"), HudUi.Dim);
        }

        var first = Math.Max(0, (int)(_scroll / Fonts.MonoRow) - 1);
        var count = (int)(height / Fonts.MonoRow) + 3;
        for (var i = first; i < Math.Min(Math.Min(_lines.Length, first + count), MaxLines); i++)
            ui.Text(ui.X, i * Fonts.MonoRow, Fonts.MonoRow, Fonts.Mono, _lines[i]);
        Canvas.Unclip();
        ScrollBar(size.W - scaled(14), scaled(10), top, height, content, _scroll);
    }

    private void Footer(HudUi ui, double width)
    {
        ui.Fill(0, ui.Y, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(4);
        ui.Row(T("hud-v-dbg-footer"), T(_settings.DebugLanguage == "de" ? "hud-v-dbg-footer-de" : "hud-v-dbg-footer-en"), HudUi.Dim, HudUi.Dim,
            ui.F.Small);
    }

    // The left button drags by the head or jumps the pane to a click on its scroll bar
    protected override bool Grabs(double x, double y, EnumMouseButton button)
    {
        if (!Finite(x + y) || button != EnumMouseButton.Left) return false;
        if (y >= _paneTop && y < _paneTop + _paneH && x >= Canvas.Width - scaled(18)) ScrollTo(y);
        return y < _paneTop;
    }

    protected override void Moved(double x, double y)
    {
        if (Finite(x) && Finite(y)) (_settings.DebugX, _settings.DebugY) = (x, y);
    }

    private void ScrollTo(double y)
    {
        var content = _lines.Length * Fonts.MonoRow + scaled(20);
        if (content <= _paneH) return;
        _scroll = Math.Clamp((y - _paneTop) / _paneH, 0, 1) * (content - _paneH);
        Invalidate(true);
    }

    protected override void Scrolled(double notches)
    {
        if (Finite(notches)) _scroll += notches * Fonts.MonoRow * 3;
    }
}

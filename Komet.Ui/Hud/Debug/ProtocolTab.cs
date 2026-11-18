using System.Globalization;
using Komet.Core;

namespace Komet.Hud;

// The protocol tab of the dockable window (K): capture controls or progress, section jumps and the last
// protocol (DebugReport) inline in the window's scrolling body.
internal sealed class ProtocolTab(ICoreClientAPI capi, HudSettings settings, DebugCapture capture) : IHudTab
{
    private const int MaxLines = 8192, MaxSections = 64;
    private static readonly int[] Durations = [10, 30, 60, DebugCapture.MaxSeconds];
    private static readonly int[] Thresholds = [25, 50, 100];
    private HudBody? _body;
    private (string Text, bool Heading)[] _lines = [];
    private double _linesTop, _row = 1;
    private string _mode = "record";
    private int _seconds = 30, _threshold = 50;
    private (int Line, string Title)[] _sections = [];
    private DebugReport _shown = DebugReport.Empty;

    public string Id => "protocol";
    public bool Wide => true;

    // A new protocol or a running capture
    public bool Live()
    {
        if (!ReferenceEquals(capture.LastReport, _shown))
        {
            Load(capture.LastReport);
            return true;
        }

        return capture.Progress.Running;
    }

    public void Draw(HudUi ui, HudBody body)
    {
        _body = body;
        Head(ui);
        ui.Rule();
        Controls(ui);
        if (_lines.Length > 0)
        {
            ui.Rule();
            Jumps(ui);
        }

        ui.Rule();
        Lines(ui);
        ui.Rule();
        ui.Row(T("hud-v-dbg-footer"),
            T(settings.DebugLanguage == "de" ? "hud-v-dbg-footer-de" : "hud-v-dbg-footer-en"), HudUi.Dim, HudUi.Dim,
            ui.F.Small);
    }

    // The report's rows as Text writes them: head, then per section a blank line, its heading and its lines
    private void Load(DebugReport report)
    {
        _shown = report;
        var lines = new List<(string, bool)>(Math.Min(report.Lines, MaxLines));
        var sections = new List<(int, string)> { (0, T("hud-v-dbg-head")) };
        foreach (var line in report.Head.Bounded(DebugReport.MaxLines))
        {
            lines.Add((line, false));
        }

        foreach (var section in report.Sections.Bounded(DebugReport.MaxSections))
        {
            lines.Add(("", false));
            if (sections.Count < MaxSections) sections.Add((lines.Count, section.Title));

            lines.Add((DebugReport.Heading(section), true));
            foreach (var line in section.Lines.Bounded(DebugReport.MaxLines))
            {
                lines.Add((line, false));
            }
        }

        _lines = [.. lines.Take(MaxLines)];
        _sections = [.. sections.Where(s => s.Item1 < _lines.Length)];
        Scroll(0);
    }

    private void Scroll(double y)
    {
        if (_body is { } body) body.Scroll = y;
    }

    private static string T(string key, params object[] args) => KometText.Translate(key, args);

    private void Head(HudUi ui)
    {
        ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Small, DebugFiles.Protocol, HudUi.Dim);
        var right = ui.X + ui.W;
        var copy = T("hud-v-dbg-copy");
        right -=
            ui.LinkText(right - ui.TextW(ui.F.Body, copy), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, copy, Copy) +
            ui.Px(14);
        var chips = ui.TextW(ui.F.Small, "English") + ui.TextW(ui.F.Small, "Deutsch") + ui.Px(32);
        _ = ui.Chips(right - chips, ui.Y + (ui.F.StrongRow - ui.ChipHeight) / 2, [
            ("English", settings.DebugLanguage == "en", () => Language("en")),
            ("Deutsch", settings.DebugLanguage == "de", () => Language("de"))
        ]);
        ui.Y += ui.F.StrongRow;
    }

    private void Language(string code)
    {
        if (code == settings.DebugLanguage) return;

        var section = _sections.Length > 0 ? Current() : 0;
        settings.DebugLanguage = code;
        capture.Relabel(code);
        Load(capture.LastReport);
        if (Index(section, _sections.Length)) Scroll(_linesTop + _sections[section].Line * _row);
    }

    private void Copy()
    {
        if (!NotNull(capi.Input) || capture.LastText.Length == 0) return;

        capi.Input.ClipboardText = capture.LastText;
        capi.ShowChatMessage(T("hud-debug-copied"));
    }

    private void Controls(HudUi ui)
    {
        var progress = capture.Progress;
        if (progress.Running)
        {
            Running(ui, progress);
        }
        else
        {
            Setup(ui);
        }
    }

    private void Setup(HudUi ui)
    {
        var start = T(_mode switch
        {
            "now" => "hud-v-dbg-start-now", "spike" => "hud-v-dbg-start-spike", _ => "hud-v-dbg-start-record"
        });
        var x = ui.Chips(ui.X, ui.Y + ui.Px(3), [
            (T("hud-v-dbg-now"), _mode == "now", () => _mode = "now"),
            (T("hud-v-dbg-record"), _mode == "record", () => _mode = "record"),
            (T("hud-v-dbg-spike"), _mode == "spike", () => _mode = "spike")
        ]);
        x += ui.Px(12);
        if (_mode == "record")
        {
            foreach (var seconds in Durations.Bounded(4))
            {
                var label = seconds < DebugCapture.MaxSeconds
                    ? seconds + " s"
                    : T("hud-v-dbg-max-min", DebugCapture.MaxSeconds / 60);
                x += ui.Chip(x, ui.Y + ui.Px(3), label, _seconds == seconds, () => _seconds = seconds) + ui.Px(4);
            }
        }
        else if (_mode == "spike")
        {
            foreach (var ms in Thresholds.Bounded(3))
            {
                x += ui.Chip(x, ui.Y + ui.Px(3), "> " + ms + " ms", _threshold == ms, () => _threshold = ms) + ui.Px(4);
            }
        }
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
        {
            _ = ui.Chip(joinX, ui.Y + ui.Px(3), join, settings.DebugOnJoin,
                () => settings.DebugOnJoin = !settings.DebugOnJoin);
        }

        _ = ui.Go(go, ui.Y, start, Start);
        ui.Y += ui.GoHeight + ui.Px(4);
        Result(ui);
    }

    private void Start() => capture.Capture(_mode, _mode == "record" ? _seconds : 0, _threshold);

    private void Result(HudUi ui)
    {
        if (_lines.Length == 0)
        {
            var key = _mode switch
            {
                "now" => "hud-v-dbg-about-now", "spike" => "hud-v-dbg-about-spike", _ => "hud-v-dbg-about-record"
            };
            ui.Line(T(key) + " · " + Command(), HudUi.Dim, ui.F.Small);
            return;
        }

        var check = ui.Check(ui.X, ui.Y, ui.F.SmallRow, HudUi.Ok) + ui.Px(5);
        ui.Text(ui.X + check, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-dbg-done", _shown.Lines), HudUi.Ok);
        ui.Y += ui.F.SmallRow;
        var compare = capture.LastCompare;
        if (compare.Rows.Length == 0) return;

        var x = ui.X;
        var lead = T("hud-v-dbg-against", compare.Stamp) + " ";
        ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, lead, HudUi.Dim);
        x += ui.TextW(ui.F.Small, lead);
        foreach (var (key, before, now) in compare.Rows.Bounded(16))
        {
            if (Change(key, before, now) is { } part && x + ui.TextW(ui.F.Small, part.Text + part.Delta) < ui.X + ui.W)
            {
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Text);
                x += ui.TextW(ui.F.Small, part.Text);
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Delta, part.Better ? HudUi.Ok : HudUi.Err);
                x += ui.TextW(ui.F.Small, part.Delta) + ui.Px(8);
            }
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
            "avg" => ("Ø", 2, true), "low1" => ("1% Low", 0, false), "worst" => ("max", 0, true),
            "over50" => (">50 ms %", 1, true),
            "gc" => ("GC %", 1, true), "alloc" => ("MB/s", 0, true), _ => ("", 0, true)
        };
        if (label.Length == 0) return null;

        var delta = now - before;
        var sign = delta >= 0 ? "+" : "−";
        return ($"{label} {KometText.Num(now, decimals)} ", sign + KometText.Num(Math.Abs(delta), decimals),
            lowerBetter ? delta <= 0 : delta >= 0);
    }

    private string Command()
    {
        var language = settings.DebugLanguage == "de" ? " de" : "";
        var mode = _mode switch
        {
            "now" => " now", "spike" => " spike " + _threshold,
            _ => " " + _seconds.ToString(CultureInfo.InvariantCulture)
        };
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
        if (p.Frames > 0) _ = ui.Go(stopX, ui.Y, stop, capture.EndCapture, true);

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
            var label = i == 0 ? T("hud-v-dbg-head") : i.ToString(CultureInfo.InvariantCulture);
            x += ui.Chip(x, ui.Y, label, i == current, () => Scroll(_linesTop + line * _row)) + ui.Px(2);
        }

        ui.Text(x + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small,
            ui.Fit(ui.F.Small, _sections[current].Title, ui.X + ui.W - x - ui.Px(6)));
        ui.Y += ui.ChipHeight;
    }

    private int Current()
    {
        var top = (int)(((_body?.Scroll ?? 0) - _linesTop) / Math.Max(1, _row));
        var current = 0;
        for (var i = 0; i < Math.Min(_sections.Length, MaxSections); i++)
        {
            if (_sections[i].Line <= top + 1) current = i;
        }

        return current;
    }

    // The protocol inline: only the lines in view are drawn, the rest only counted
    private void Lines(HudUi ui)
    {
        if (_lines.Length == 0)
        {
            ui.Space(30);
            ui.Wrap(T(capture.Progress.Running ? "hud-v-dbg-pane-waiting" : "hud-v-dbg-pane-empty"), HudUi.Dim);
            return;
        }

        (_linesTop, _row) = (ui.Y, ui.F.MonoRow);
        for (var i = 0; i < Math.Min(_lines.Length, MaxLines); i++)
        {
            if (ui.Shown(_linesTop + i * _row, _row))
            {
                var (text, heading) = _lines[i];
                ui.Text(ui.X, _linesTop + i * _row, _row, ui.F.Mono, text, heading ? HudUi.Link : null);
            }
        }

        ui.Y = _linesTop + _lines.Length * _row;
    }
}

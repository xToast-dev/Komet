using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Hud;

// Text and drawing the overlay, the tabs and the reports share
internal static class HudParts
{
    public static string T(string key, params object[] args) => KometText.Translate(key, args);

    public static string N(double value, int decimals = 0) => KometText.Num(value, decimals);

    public static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    // Mark names without engine namespaces, so one long type name cannot widen a column; pseudo causes in words
    public static string Describe(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Assert(name.Length < 1024)) return "";

        if (name[0] == '~') return KometText.Cached("hud-cause-", name);

        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return name;

        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && dash < dot
            ? string.Concat(name.AsSpan(0, dash + 1), name.AsSpan(dot + 1))
            : name[(dot + 1)..];
    }

    // NaN: not measured; infinite: broken clock
    public static string Millis(double ms)
    {
        if (double.IsNaN(ms)) return "–";

        return Finite(ms) && Assert(ms >= 0) ? KometText.Format(ms, "F1") : "";
    }

    // The frame-time history, newest right
    public static void Graph(HudUi ui, FrameStats frames, double height, bool guides)
    {
        var n = Math.Min(frames.Recorded, HudCanvas.GraphFrames);
        var first = frames.HistoryLength - n;
        if (n < 2)
        {
            ui.Fill(ui.X, ui.Y, ui.W, height, HudUi.TileBack);
            ui.Y += height;
            return;
        }

        ui.Graph(height, n, i => frames.HistoryMs(first + i), guides);
    }

    public static void ChipRow(HudUi ui, string label, ReadOnlySpan<(string Text, bool On, Action Click)> chips)
    {
        double width = 0;
        foreach (var (text, _, _) in chips.Bounded(8))
        {
            width += ui.TextW(ui.F.Small, text) + ui.Px(18);
        }

        var h = Math.Max(ui.F.BodyRow, ui.ChipHeight);
        ui.Text(ui.X, ui.Y, h, ui.F.Body, label);
        _ = ui.Chips(ui.X + ui.W - width + ui.Px(4), ui.Y + (h - ui.ChipHeight) / 2, chips);
        ui.Y += h + ui.Px(2);
    }
}

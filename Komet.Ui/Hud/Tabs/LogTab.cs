using Komet.Diagnostics;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// The game log's newest entries, filtered by level; a click opens an entry's full text
internal sealed class LogTab(LogStats log) : IHudTab
{
    private const int ShownLines = 40;
    private readonly Dictionary<(string Key, double Width, bool Open), string[]> _lines = []; // wrapping measures
    private readonly HashSet<string> _open = [];
    private string _filter = "all";
    private (int Log, int Fonts) _linesOf = (-1, -1); // the log snapshot and font sizes _lines was wrapped for

    public string Id => "log";
    public HudNeeds Needs => HudNeeds.Log;

    public void Shown() => _ = Task.Run(log.Sample);

    public void Draw(HudUi ui, HudBody body)
    {
        ui.Space(5);
        var x = ui.Chips(ui.X, ui.Y, [
            (T("hud-v-all"), _filter == "all", () => _filter = "all"),
            (T("hud-v-warnings"), _filter == "wn", () => _filter = "wn"),
            (T("hud-v-errors"), _filter == "er", () => _filter = "er")
        ]);
        var hint = T("hud-v-log-newest");
        ui.TextRight(ui.X + ui.W, ui.Y, ui.ChipHeight, ui.F.Small, hint, HudUi.Dim);
        var room = ui.X + ui.W - ui.TextW(ui.F.Small, hint) - x - ui.Px(18);
        if (room > ui.Px(40))
        {
            ui.Text(x + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small, ui.Fit(ui.F.Small, "client-main.log", room),
                HudUi.Faint);
        }

        ui.Y += ui.ChipHeight + ui.Px(6);
        var shown = 0;
        for (var back = 0; back < LogStats.KeepEntries && shown < ShownLines; back++)
        {
            if (log.Entry(back) is not { } entry) break;

            if ((_filter == "wn" && entry.Level < LogLevel.Warning) ||
                (_filter == "er" && entry.Level != LogLevel.Error))
            {
                continue;
            }

            EntryRow(ui, entry, shown % 2 == 1);
            shown++;
        }

        if (shown == 0) ui.Line(T("hud-v-log-empty"), HudUi.Dim);
    }

    private void EntryRow(HudUi ui, LogEntry entry, bool odd)
    {
        var (pad, column) = (ui.Px(4), ui.TextW(ui.F.Small, "00:00:00") + ui.Px(18));
        var key = entry.Time + entry.Text;
        var open = _open.Contains(key);
        var lines = Lines(ui, key, entry.Text, ui.W - column - ui.Px(6), open);
        var h = Math.Max(lines.Length * ui.F.BodyRow, 2 * ui.F.SmallRow) + 2 * pad;
        (Rgba Color, Rgba? Stripe) look = entry.Level switch
        {
            LogLevel.Error => (HudUi.Err, HudUi.Err), LogLevel.Warning => (HudUi.Warn, HudUi.Warn),
            LogLevel.Debug => (HudUi.Dim, null),
            _ => (Rgba.White(0.86), null)
        };
        var (color, stripe) = look;
        if (odd || open) ui.Fill(ui.X, ui.Y, ui.W, h, Rgba.White(open ? 0.06 : 0.025), ui.Px(3));

        if (stripe is { } s) ui.Fill(ui.X, ui.Y + pad, ui.Px(3), h - 2 * pad, s, ui.Px(1.5));

        ui.Text(ui.X + ui.Px(9), ui.Y + pad, ui.F.SmallRow, ui.F.Small, entry.Time, HudUi.Dim);
        if (entry.Source.Length > 0)
        {
            ui.Text(ui.X + ui.Px(9), ui.Y + pad + ui.F.SmallRow, ui.F.SmallRow, ui.F.Small,
                ui.Fit(ui.F.Small, entry.Source, column - ui.Px(12)),
                entry.Source == "komet" ? HudUi.Link : HudUi.Faint);
        }

        for (var i = 0; i < Math.Min(lines.Length, 24); i++)
        {
            ui.Text(ui.X + column, ui.Y + pad + i * ui.F.BodyRow, ui.F.BodyRow, ui.F.Body, lines[i], color);
        }

        ui.Click(ui.X, ui.Y, ui.W, h, () => _ = _open.Add(key) || _open.Remove(key));
        ui.Y += h + ui.Px(2);
    }

    private string[] Lines(HudUi ui, string key, string text, double width, bool open)
    {
        var of = (log.Version, ui.F.Epoch);
        if (of != _linesOf || _lines.Count >= LogStats.KeepEntries * 2)
        {
            _lines.Clear();
            _linesOf = of;
        }

        if (!_lines.TryGetValue((key, width, open), out var lines))
        {
            _lines[(key, width, open)] = lines = ui.Lines(text, ui.F.Body, width, open ? 24 : 2);
        }

        return lines;
    }
}

using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Behind the window's gear, not in the tab bar: what the overlay shows, its look, spike hints and the keys
internal sealed class SettingsPage(ICoreClientAPI capi, HudSettings settings, Action openOptions) : IHudTab
{
    public string Id => "settings";

    public void Draw(HudUi ui, HudBody body)
    {
        var s = settings;
        ui.Header(T("hud-v-overlay-shows"));
        _ = ui.Chips(ui.X, ui.Y, [
            ("FPS", s.ShowFps, () => s.ShowFps = !s.ShowFps), ("1% Low", s.ShowLows, () => s.ShowLows = !s.ShowLows),
            (T("hud-v-frametime"), s.ShowFrametime, () => s.ShowFrametime = !s.ShowFrametime),
            ("Graph", s.ShowGraph, () => s.ShowGraph = !s.ShowGraph),
            (T("hud-v-mods-total"), s.ShowMods, () => s.ShowMods = !s.ShowMods)
        ]);
        ui.Y += ui.ChipHeight + ui.Px(2);
        Look(ui, s);
        ui.Header(T("hud-v-spike-hints"));
        ChipRow(ui, T("hud-v-show-from"), [
            (T("hud-off"), s.ToastMs <= 0, () => s.ToastMs = 0), ("25 ms", Near(s.ToastMs, 25), () => s.ToastMs = 25),
            ("50 ms", Near(s.ToastMs, 50), () => s.ToastMs = 50),
            ("100 ms", Near(s.ToastMs, 100), () => s.ToastMs = 100)
        ]);
        ui.Line(T("hud-v-toast-rate"), HudUi.Dim, ui.F.Small);
        ui.Header(T("hud-v-keys"));
        ui.Row(T("hud-v-key-overlay"), KeyName("komet-hud-toggle"));
        ui.Row(T("hud-v-key-window"), KeyName("komet-hud-window"));
        ui.Row(T("hud-v-key-debug"), KeyName("komet-hud-debug"));
        ui.Space(6);
        ui.Wrap(T("hud-v-remembered"), HudUi.Dim, ui.F.Small);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-open-options"), openOptions, ui.F.Small);
        ui.Y += ui.F.SmallRow;
    }

    private static void Look(HudUi ui, HudSettings s)
    {
        ui.Header(T("hud-v-look-section"));
        ChipRow(ui, T("hud-v-position"), [
            (T("hud-v-pos-tl"), s.Corner == HudCorner.TopLeft, () => s.Corner = HudCorner.TopLeft),
            (T("hud-v-pos-tr"), s.Corner == HudCorner.TopRight, () => s.Corner = HudCorner.TopRight),
            (T("hud-v-pos-bl"), s.Corner == HudCorner.BottomLeft, () => s.Corner = HudCorner.BottomLeft),
            (T("hud-v-pos-br"), s.Corner == HudCorner.BottomRight, () => s.Corner = HudCorner.BottomRight)
        ]);
        ChipRow(ui, T("hud-v-opacity"), [
            ("60 %", Near(s.Opacity, 0.6), () => s.Opacity = 0.6),
            ("80 %", Near(s.Opacity, 0.8), () => s.Opacity = 0.8),
            ("100 %", Near(s.Opacity, 1), () => s.Opacity = 1)
        ]);
        ChipRow(ui, T("hud-v-fontsize"), [
            ("90 %", Near(s.FontScale, 0.9), () => s.FontScale = 0.9),
            ("100 %", Near(s.FontScale, 1), () => s.FontScale = 1),
            ("120 %", Near(s.FontScale, 1.2), () => s.FontScale = 1.2)
        ]);
    }

    // As bound in the game's controls, which may differ from Komet's defaults. The engine's own
    // KeyCombination.ToString writes the modifiers in English, so they are built from lang keys here.
    private string KeyName(string code) =>
        NotNull(capi.Input.HotKeys) && capi.Input.HotKeys.TryGetValue(code, out var key) &&
        key?.CurrentMapping is { } mapping
            ? KeyLabel(mapping)
            : "–";

    private static string KeyLabel(KeyCombination m)
    {
        if (m.KeyCode < 0) return "?";

        var label = (m.Ctrl ? T("hud-v-ctrl") + "+" : "") + (m.Alt ? T("hud-v-alt") + "+" : "") +
                    (m.Shift ? T("hud-v-shift") + "+" : "") + m.PrimaryAsString();
        return m.SecondKeyCode > 0 ? label + "+" + m.SecondaryAsString() : label;
    }
}

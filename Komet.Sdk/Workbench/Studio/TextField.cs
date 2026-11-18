using Komet.Hud;

namespace Komet.Sdk;

// A single-line input: text, caret, a select-all state (the next key replaces it), clipboard; drawn as the draft's
// fields (a hairline box, the accent line when focused, a placeholder in the faint tone)
internal sealed class TextField(int max = 200)
{
    private bool _all;

    public string Text { get; private set; } = "";
    private int Caret { get; set; }

    // Bumped on every change: owners compare it
    public int Version { get; private set; }

    public void Set(string text, bool selectAll = false)
    {
        text = text.Length > max ? text[..max] : text;
        if (text != Text)
        {
            Text = text;
            Version++;
        }

        (Caret, _all) = (Text.Length, selectAll && Text.Length > 0);
    }

    public void SelectAll() => (_all, Caret) = (Text.Length > 0, Text.Length);

    public void Type(char c)
    {
        if (char.IsControl(c)) return;

        Insert(c.ToString());
    }

    private void Insert(string value)
    {
        value = value.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
        if (_all) (Text, Caret, _all) = ("", 0, false);

        if (Text.Length + value.Length > max) value = value[..Math.Max(0, max - Text.Length)];

        Text = Text.Insert(Caret, value);
        Caret += value.Length;
        Version++;
    }

    // Editing keys; true: taken (Enter, Escape, Tab and arrows up/down stay with the owner)
    public bool Key(GlKeys key, bool ctrl, ICoreClientAPI capi)
    {
        switch (key)
        {
            case GlKeys.BackSpace when _all: Set(""); return true;
            case GlKeys.BackSpace when Caret > 0: Remove(Caret - 1); return true;
            case GlKeys.Delete when _all: Set(""); return true;
            case GlKeys.Delete when Caret < Text.Length: Remove(Caret); return true;
            case GlKeys.Left: (Caret, _all) = (Math.Max(0, Caret - 1), false); return true;
            case GlKeys.Right: (Caret, _all) = (Math.Min(Text.Length, Caret + 1), false); return true;
            case GlKeys.Home: (Caret, _all) = (0, false); return true;
            case GlKeys.End: (Caret, _all) = (Text.Length, false); return true;
            case GlKeys.A when ctrl: SelectAll(); return true;
            case GlKeys.C when ctrl: capi.Input.ClipboardText = Text; return true;
            case GlKeys.X when ctrl: capi.Input.ClipboardText = Text; Set(""); return true;
            case GlKeys.V when ctrl: Insert(capi.Input.ClipboardText ?? ""); return true;
            case GlKeys.BackSpace or GlKeys.Delete: return true;
            default: return false;
        }
    }

    private void Remove(int at)
    {
        Text = Text.Remove(at, 1);
        Caret = Math.Min(at, Text.Length);
        Version++;
    }

    // The field in a box at x, y; the box's height
    public double Draw(HudUi ui, double x, double y, double w, string placeholder, bool focused, bool framed = true)
    {
        var h = ui.F.BodyRow + ui.Px(8);
        if (framed)
        {
            ui.Fill(x, y, w, h, HudUi.Track with { A = 0.05 });
            Frame(ui, x, y, w, h, focused ? HudUi.Accent : HudUi.Divider);
        }

        var pad = ui.Px(8);
        if (Text.Length == 0)
        {
            ui.Text(x + pad, y, h, ui.F.Body, ui.Fit(ui.F.Body, placeholder, w - 2 * pad), HudUi.Faint);
        }
        else
        {
            if (_all && focused)
            {
                ui.Fill(x + pad, y + ui.Px(4), Math.Min(ui.TextW(ui.F.Body, Text), w - 2 * pad), h - ui.Px(8),
                    HudUi.SelectionBack);
            }

            ui.Text(x + pad, y, h, ui.F.Body, ui.Fit(ui.F.Body, Text, w - 2 * pad));
        }

        if (focused)
        {
            var cx = x + pad + Math.Min(ui.TextW(ui.F.Body, Text[..Caret]), w - 2 * pad);
            ui.Fill(cx, y + ui.Px(5), Math.Max(1, ui.Px(1)), h - ui.Px(10), HudUi.Accent);
        }

        return h;
    }

    // A one-pixel outline
    public static void Frame(HudUi ui, double x, double y, double w, double h, Rgba color)
    {
        var one = Math.Max(1, ui.Px(1));
        ui.Fill(x, y, w, one, color);
        ui.Fill(x, y + h - one, w, one, color);
        ui.Fill(x, y, one, h, color);
        ui.Fill(x + w - one, y, one, h, color);
    }
}

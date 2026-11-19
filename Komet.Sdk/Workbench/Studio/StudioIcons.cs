using Komet.Hud;

namespace Komet.Sdk;

// The draft's line icons, drawn: folder (a star inside for a project), file with a mark per type, the house of the
// Vintagestory folder, a lock, a magnifier, a clock, the tree's triangle
internal static class StudioIcons
{
    public static void Folder(HudUi ui, double x, double cy, double s, Rgba color, bool star = false)
    {
        var w = Math.Max(1, ui.Px(1.3));
        double l = x, r = x + s, t = cy - s * 0.32, b = cy + s * 0.38;
        ui.Stroke(l, t, l + s * 0.4, t, color, w);
        ui.Stroke(l + s * 0.4, t, l + s * 0.5, t + s * 0.1, color, w);
        ui.Stroke(l + s * 0.5, t + s * 0.1, r, t + s * 0.1, color, w);
        ui.Stroke(r, t + s * 0.1, r, b, color, w);
        ui.Stroke(r, b, l, b, color, w);
        ui.Stroke(l, b, l, t, color, w);
        if (star) ui.Disc(x + s * 0.55, cy + s * 0.08, s * 0.16, color);
    }

    // ext: the file's extension in lower case
    public static void File(HudUi ui, double x, double cy, double s, string ext)
    {
        var color = Color(ext);
        var w = Math.Max(1, ui.Px(1.3));
        double l = x + s * 0.12, r = x + s * 0.82, t = cy - s * 0.45, b = cy + s * 0.45, fold = s * 0.22;
        ui.Stroke(l, t, r - fold, t, color, w);
        ui.Stroke(r - fold, t, r, t + fold, color, w);
        ui.Stroke(r, t + fold, r, b, color, w);
        ui.Stroke(r, b, l, b, color, w);
        ui.Stroke(l, b, l, t, color, w);
        if (ext == ".json")
        {
            ui.Ring(x + s * 0.47, cy + s * 0.05, s * 0.16, color, w);
        }
        else if (ext is ".fsh" or ".vsh" or ".gsh" or ".ash")
        {
            ui.Stroke(l + s * 0.15, cy, r - s * 0.15, cy, color, w);
            ui.Stroke(l + s * 0.15, cy + s * 0.18, r - s * 0.3, cy + s * 0.18, color, w);
        }
    }

    private static Rgba Color(string ext) =>
        ext switch
        {
            ".fsh" => StudioColors.Fn,
            ".vsh" or ".gsh" or ".ash" => StudioColors.Type,
            ".json" => StudioColors.Gold,
            ".zip" => HudUi.Soft,
            _ => HudUi.Faint
        };

    public static void Home(HudUi ui, double x, double cy, double s, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.3));
        double l = x + s * 0.1, r = x + s * 0.9, b = cy + s * 0.42, m = x + s / 2, t = cy - s * 0.45;
        ui.Stroke(l, cy - s * 0.05, m, t, color, w);
        ui.Stroke(m, t, r, cy - s * 0.05, color, w);
        ui.Stroke(l, cy - s * 0.05, l, b, color, w);
        ui.Stroke(r, cy - s * 0.05, r, b, color, w);
        ui.Stroke(l, b, r, b, color, w);
    }

    public static void Lock(HudUi ui, double x, double cy, double s, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.2));
        ui.Fill(x, cy - s * 0.05, s * 0.8, s * 0.5, color);
        ui.Ring(x + s * 0.4, cy - s * 0.1, s * 0.25, color, w, Math.PI);
    }

    public static void Magnifier(HudUi ui, double x, double cy, double s, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.4));
        ui.Ring(x + s * 0.42, cy - s * 0.08, s * 0.3, color, w);
        ui.Stroke(x + s * 0.64, cy + s * 0.14, x + s * 0.92, cy + s * 0.42, color, w);
    }

    public static void Clock(HudUi ui, double x, double cy, double s, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.3));
        ui.Ring(x + s / 2, cy, s * 0.4, color, w);
        ui.Stroke(x + s / 2, cy - s * 0.22, x + s / 2, cy, color, w);
        ui.Stroke(x + s / 2, cy, x + s * 0.66, cy + s * 0.12, color, w);
    }

    // The tree's triangle: right when shut, down when open
    public static void Twist(HudUi ui, double x, double cy, double s, bool open, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.3));
        var h = s * 0.3;
        if (open)
        {
            ui.Stroke(x + s / 2 - h, cy - h / 2, x + s / 2, cy + h / 2, color, w);
            ui.Stroke(x + s / 2, cy + h / 2, x + s / 2 + h, cy - h / 2, color, w);
        }
        else
        {
            ui.Stroke(x + s / 2 - h / 2, cy - h, x + s / 2 + h / 2, cy, color, w);
            ui.Stroke(x + s / 2 + h / 2, cy, x + s / 2 - h / 2, cy + h, color, w);
        }
    }

    // The draft's small filled triangles of the Frame rows' up and down buttons
    public static void Triangle(HudUi ui, double cx, double cy, double s, bool up, Rgba color)
    {
        var w = Math.Max(1, ui.Px(1.6));
        var d = up ? -1 : 1;
        for (var i = 0; i < 4; i++)
        {
            var k = i / 3.0;
            ui.Stroke(cx - s / 2 * k, cy - d * s / 3 + d * s * 0.6 * k, cx + s / 2 * k,
                cy - d * s / 3 + d * s * 0.6 * k,
                color, w);
        }
    }

    public static void Close(HudUi ui, double cx, double cy, double s, Rgba color) =>
        ui.Cross(cx - s / 2, cy - s / 2, s, color);

    // Enlarge: four corner brackets around (cx, cy)
    public static void Enlarge(HudUi ui, double cx, double cy, double s, Rgba color)
    {
        var (w, h, k) = (Math.Max(1, ui.Px(1.3)), s / 2, s * 0.3);
        foreach (var (sx, sy) in (ReadOnlySpan<(int, int)>)[(-1, -1), (1, -1), (-1, 1), (1, 1)])
        {
            var (x, y) = (cx + sx * h, cy + sy * h);
            ui.Stroke(x, y, x - sx * k, y, color, w);
            ui.Stroke(x, y, x, y - sy * k, color, w);
        }
    }

    // Split: a frame halved by a line, the right half filled while the split is on
    public static void Split(HudUi ui, double cx, double cy, double s, Rgba color, bool on)
    {
        var (w, hw, hh) = (Math.Max(1, ui.Px(1.3)), s / 2, s * 0.38);
        if (on) ui.Fill(cx, cy - hh, hw, hh * 2, color with { A = 0.45 });

        ui.Stroke(cx - hw, cy - hh, cx + hw, cy - hh, color, w);
        ui.Stroke(cx - hw, cy + hh, cx + hw, cy + hh, color, w);
        ui.Stroke(cx - hw, cy - hh, cx - hw, cy + hh, color, w);
        ui.Stroke(cx + hw, cy - hh, cx + hw, cy + hh, color, w);
        ui.Stroke(cx, cy - hh - ui.Px(2), cx, cy + hh + ui.Px(2), color, w);
    }

    // The explorer's override mark: a filled dot while the override is on, a hollow ring while it is off
    public static void Override(HudUi ui, double cx, double cy, double r, Rgba color, bool on)
    {
        if (on)
        {
            ui.Disc(cx, cy, r, color);
        }
        else
        {
            ui.Ring(cx, cy, r, color, Math.Max(1, ui.Px(1.3)));
        }
    }
}

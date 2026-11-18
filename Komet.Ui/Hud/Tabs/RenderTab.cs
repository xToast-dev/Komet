using Komet.Core;
using Komet.Options;
using Komet.Vulkan;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// The renderer, Vulkan's share, where the frame goes (passes) and the big render numbers
internal sealed class RenderTab(HudSampler sampler) : IHudTab
{
    private static readonly Rgba[] PassColors =
    [
        new(150 / 255.0, 120 / 255.0, 220 / 255.0, 1), new(110 / 255.0, 150 / 255.0, 230 / 255.0, 1),
        new(80 / 255.0, 170 / 255.0, 200 / 255.0, 1), new(89 / 255.0, 191 / 255.0, 115 / 255.0, 1),
        new(160 / 255.0, 200 / 255.0, 90 / 255.0, 1), new(200 / 255.0, 190 / 255.0, 60 / 255.0, 1),
        new(230 / 255.0, 150 / 255.0, 50 / 255.0, 1), Rgba.White(0.35), Rgba.White(0.15)
    ];

    private bool _vulkanOpen, _more;

    public string Id => "render";
    public HudNeeds Needs => HudNeeds.Occlusion;

    public void Draw(HudUi ui, HudBody body)
    {
        var l = sampler.Live;
        var vk = l.Vulkan;
        var running = VulkanRenderer.State == VulkanRenderer.Phase.Running;
        ui.Space(8);
        var y = ui.Y;
        ui.Fill(ui.X, y + ui.F.StrongRow / 2 - ui.Px(3.5), ui.Px(7), ui.Px(7), running ? HudUi.Ok : HudUi.Dim,
            ui.Px(3.5));
        ui.Text(ui.X + ui.Px(12), y, ui.F.StrongRow, ui.F.Strong, VulkanMode.Renderer());
        ui.TextRight(ui.X + ui.W, y, ui.F.StrongRow, ui.F.Small,
            running ? N(vk.MemoryMb / 1024.0, 1) + " GB" : "OpenGL", HudUi.Dim);
        ui.Y = y + ui.F.StrongRow + ui.Px(6);
        var share = vk.Draws + vk.Left > 0 ? 100 * vk.Draws / (vk.Draws + vk.Left) : 0;
        if (running) // OpenGL alone has no share to split
        {
            ui.Segments(ui.Y, ui.Px(4), [(share, HudUi.Accent), (100 - share, Rgba.White(0.35))], 100);
            ui.Y += ui.Px(7);
        }

        DrawShare(ui, running, share, vk);
        Passes(ui, l);
        BigNumbers(ui, l);
        Footer(ui, running, vk);
    }

    private static void Passes(HudUi ui, HudLive l)
    {
        ui.Label(T("hud-v-frame-goes", N(l.TotalMs > 0 ? l.TotalMs : l.AvgMs, 1)));
        Span<(double, Rgba)> parts = stackalloc (double, Rgba)[HudLive.MaxPasses];
        for (var i = 0; i < Math.Min(l.PassCount, HudLive.MaxPasses); i++)
        {
            parts[i] = (l.Passes[i].Ms, PassColors[i % PassColors.Length]);
        }

        ui.Segments(ui.Y, ui.Px(12), parts[..l.PassCount]);
        ui.Y += ui.Px(16);
        ui.Grid(3, l.PassCount, 12, (i, x, py, w) =>
        {
            ui.Fill(x, py + (ui.F.SmallRow - ui.Px(7)) / 2, ui.Px(7), ui.Px(7), PassColors[i % PassColors.Length]);
            ui.Text(x + ui.Px(11), py, ui.F.SmallRow, ui.F.Small,
                ui.Fit(ui.F.Small, PassName(l.Passes[i].Key), w - ui.Px(48)));
            ui.TextRight(x + w, py, ui.F.SmallRow, ui.F.Small, N(l.Passes[i].Ms, 2));
            return ui.F.SmallRow;
        });
    }

    private void DrawShare(HudUi ui, bool running, double share,
        (double Draws, double Left, double Segments, long MemoryMb, string[] Missing) vk)
    {
        // what Vulkan leaves is missing, OpenGL draws none of it
        var line = running ? T("hud-v-vk-only-share", N(share)) : T("hud-v-vk-off");
        ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, line, HudUi.Dim);
        if (running && vk.Left > 0)
        {
            var link = T("hud-v-vk-missing", N(vk.Left, 1));
            var x = ui.X + ui.W - ui.TextW(ui.F.Small, link) - ui.Px(11);
            _ = ui.LinkText(x, ui.Y, link, () => _vulkanOpen = !_vulkanOpen, ui.F.Small);
            _ = ui.Caret(ui.X + ui.W - ui.Px(7), ui.Y, ui.F.SmallRow, _vulkanOpen, HudUi.Link);
        }

        ui.Y += ui.F.SmallRow;
        if (_vulkanOpen)
        {
            foreach (var kind in vk.Missing.Bounded(6))
            {
                ui.Line(kind, HudUi.Dim, ui.F.Small); // what Vulkan could not draw, most first
            }
        }
    }

    private static void BigNumbers(HudUi ui, HudLive l)
    {
        ui.Space(16);
        ui.Grid(4, 4, 8, (i, x, y, _) =>
        {
            var (value, label) = i switch
            {
                0 => (N(l.GpuMs, 1) + " ms", T("hud-v-gpu-time")),
                1 => (N(l.DrawCalls), "Draw Calls"),
                2 => (N(l.DrawnTriangles / 1e6, 1) + " M", T("hud-v-triangles-drawn", N(l.Triangles / 1e6, 1))),
                _ => (double.IsFinite(l.CulledPercent) ? N(l.CulledPercent) + " %" : "–", T("hud-v-hidden"))
            };
            ui.Text(x, y, ui.F.BigRow, ui.F.Big, value);
            ui.Text(x, y + ui.F.BigRow, ui.F.SmallRow, ui.F.Small, label, HudUi.Dim);
            return ui.F.BigRow + ui.F.SmallRow;
        });
    }

    private void Footer(HudUi ui, bool running,
        (double Draws, double Left, double Segments, long MemoryMb, string[] Missing) vk)
    {
        ui.Space(16);
        // Vulkan's numbers are all zero while it does not draw
        var draws = sampler.Live.DrawCalls;
        var line = running
            ? T("hud-v-render-footer-only", N(vk.Segments, 1), N(vk.MemoryMb / 1024.0, 1), N(vk.Draws), N(draws))
            : T("hud-v-render-footer-gl", N(draws));
        var more = T(_more ? "hud-v-less" : "hud-v-more");
        ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small,
            ui.Fit(ui.F.Small, line, ui.W - ui.TextW(ui.F.Small, more) - ui.Px(8)), HudUi.Dim);
        _ = ui.LinkText(ui.X + ui.W - ui.TextW(ui.F.Small, more), ui.Y, more, () => _more = !_more, ui.F.Small);
        ui.Y += ui.F.SmallRow;
        if (!_more) return;

        ui.Space(3);
        if (VulkanRenderer.Status is { Length: > 0 } status) ui.Wrap(status, HudUi.Dim, ui.F.Small);

        if (VulkanCore.Enabled) ui.Wrap(VulkanCore.Status, HudUi.Dim, ui.F.Small);

        ui.Wrap(HudPanels.UploadCapText(), HudUi.Dim, ui.F.Small);
    }

    private static string PassName(string key) =>
        KometText.Cached("hud-pass-", key) is var name && !name.StartsWith("hud-", StringComparison.Ordinal)
            ? name
            : key.TrimStart('~');
}

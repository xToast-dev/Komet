using Komet.Core;
using Komet.Vulkan;

namespace Komet.Options;

// The renderer: OpenGL (the engine's own), Vulkan-only (Vulkan draws everything) or Vulkan compute (the device runs,
// OpenGL draws and presents). They are the three values of the VulkanOnly knob; the other Vulkan knobs stay as they are.
internal static class VulkanMode
{
    private const string Knob = "VulkanOnly";

    // The settings' choices, each with its mode
    public static readonly (string Name, int Mode)[] Choices = [("opengl", 0), ("vulkan", 1), ("compute", 2)];

    private static readonly string[] RendererKeys =
        ["hud-renderer-opengl", "hud-renderer-vulkan", "hud-renderer-failed", "hud-renderer-safe", "hud-renderer-compute"];

    private static readonly Func<string>[]
        Renderers = Array.ConvertAll(RendererKeys, static key => KometText.Once(key));


    // Found once: the built-in table is fixed
    private static int CoreKnob
    {
        get
        {
            if (field < 0) field = Knobs.Find(Knob);

            return Assert(field >= 0) && Assert(field < Knobs.BuiltInCount) ? field : -1;
        }
    } = -1;

    public static int Choice(int mode) => Index(mode, Choices.Length) ? mode : 0;

    public static int Of(System.Func<int, int> value)
    {
        var knob = CoreKnob;
        if (!NotNull(value) || knob < 0) return 0;

        var mode = value(knob);
        return Index(mode, Choices.Length) ? mode : 0;
    }

    public static void Set(int mode, Action<int, int> write)
    {
        var knob = CoreKnob;
        if (!NotNull(write) || !Index(mode, Choices.Length) || knob < 0) return;

        write(knob, mode);
    }

    public static string Renderer() => Renderers[RendererIndex()]();

    public static string RendererKey() => RendererKeys[RendererIndex()];

    public static int RendererCode() => RendererIndex();

    private static int RendererIndex()
    {
        if (VulkanRenderer.Compute) return 4;

        return VulkanRenderer.SafetyReason.Length > 0
            ? 3
            : VulkanRenderer.State switch
            {
                VulkanRenderer.Phase.Running => 1,
                VulkanRenderer.Phase.Failed => 2,
                _ => 0
            };
    }
}

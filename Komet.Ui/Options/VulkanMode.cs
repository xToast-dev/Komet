using Komet.Core;
using Komet.Vulkan;

namespace Komet.Options;

// The renderer: OpenGL (the engine's own) or Vulkan (knob VulkanOnly: Vulkan draws everything, OpenGL rests). The
// shader cache and the other Vulkan knobs stay as they are.
internal static class VulkanMode
{
    public const string Knob = "VulkanOnly";

    // The settings' choices, each with its mode
    public static readonly (string Name, int Mode)[] Choices = [("opengl", 0), ("vulkan", 1)];

    private static readonly string[] RendererKeys = ["hud-renderer-opengl", "hud-renderer-vulkan", "hud-renderer-failed"];

    private static readonly Func<string>[] Renderers = Array.ConvertAll(RendererKeys, static key => KometText.Once(key));

    private static int _knob = -1;

    public static int Choice(int mode) => Index(mode, Choices.Length) ? mode : 0;

    // Found once: the built-in table is fixed
    public static int CoreKnob
    {
        get
        {
            if (_knob < 0) _knob = Knobs.Find(Knob);
            return Assert(_knob >= 0) && Assert(_knob < Knobs.BuiltInCount) ? _knob : -1;
        }
    }

    public static int Of(System.Func<int, int> value)
    {
        var knob = CoreKnob;
        if (!NotNull(value) || knob < 0) return 0;
        return value(knob) != 0 ? 1 : 0;
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

    private static int RendererIndex() => VulkanRenderer.State switch
    {
        VulkanRenderer.Phase.Running => 1,
        VulkanRenderer.Phase.Failed => RendererKeys.Length - 1,
        _ => 0
    };
}

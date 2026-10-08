using Komet.Vulkan;

namespace Komet.Options;

// One mode for the Vulkan switches, which stay knobs of their own (the settings page shows each, the bench arms and
// komet-hud.json set them singly). A switch is on from its mode up; the shader cache and the lean handoff (0) are on in every mode.
internal static class VulkanMode
{
    public const int Switches = 18;
    public static readonly string[] Names = ["off", "terrain", "scene", "all"];

    private static readonly (string Key, int From)[] Levels =
    [
        ("VulkanShaderCache", 0), ("VulkanLazyHandoff", 0), ("VulkanCore", 1), ("VulkanTerrain", 1), ("VulkanOpaque", 1),
        ("VulkanShadows", 1), ("VulkanLiquidDepth", 1), ("VulkanTransparent", 1), ("VulkanWaterPlants", 1),
        ("VulkanOwnDepth", 1), ("VulkanScene", 2), ("VulkanSky", 2), ("VulkanEntities", 2), ("VulkanParticles", 2),
        ("VulkanPost", 2), ("VulkanOther", 2), ("VulkanWindow", 3), ("VulkanPresent", 3)
    ];

    private static readonly Func<string>[] Renderers =
    [
        HudText.Once("hud-renderer-opengl"), HudText.Once("hud-renderer-terrain"), HudText.Once("hud-renderer-scene"),
        HudText.Once("hud-renderer-all"), HudText.Once("hud-renderer-failed")
    ];

    private static int[] _knobs = [];

    // Knob indices of Levels, found once: the built-in knob table is fixed for the process
    public static int[] Indices
    {
        get
        {
            if (_knobs.Length == 0) _knobs = Array.ConvertAll(Levels, level => Knobs.Find(level.Key));
            return Assert(_knobs.Length == Switches) && Assert(Array.IndexOf(_knobs, -1) < 0) ? _knobs : [];
        }
    }

    public static int CoreKnob => Indices.Length == Switches ? Indices[2] : -1;

    // The highest mode whose switches are all on: a mix set singly shows as the mode it covers
    public static int Of(System.Func<int, int> value)
    {
        var (knobs, mode) = (Indices, Names.Length - 1);
        if (!NotNull(value) || knobs.Length == 0) return 0;
        for (var i = 0; i < Math.Min(knobs.Length, Switches); i++)
            if (Levels[i].From > 0 && value(knobs[i]) == 0) mode = Math.Min(mode, Levels[i].From - 1);
        return Assert(mode >= 0) ? mode : 0;
    }

    public static void Set(int mode, Action<int, int> write)
    {
        var knobs = Indices;
        if (!NotNull(write) || !Index(mode, Names.Length)) return;
        for (var i = 0; i < Math.Min(knobs.Length, Switches); i++) write(knobs[i], mode >= Levels[i].From ? 1 : 0);
    }

    // What draws the frame now: OpenGL, or Vulkan with the mode the live switches cover
    public static string Renderer()
    {
        var index = VulkanRenderer.State switch
        {
            VulkanRenderer.Phase.Running => Math.Max(1, Of(static knob => Knobs.At(knob).Get())),
            VulkanRenderer.Phase.Failed => Renderers.Length - 1,
            _ => 0
        };
        return Index(index, Renderers.Length) ? Renderers[index]() : "";
    }
}

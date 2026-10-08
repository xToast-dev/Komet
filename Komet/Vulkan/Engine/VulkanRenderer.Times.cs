using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// Draws and dispatches name their section before they are recorded; what else a segment records (clears, copies,
// barriers) counts to the section before.
internal static partial class VulkanRenderer
{
    public enum Drawn
    {
        Terrain,
        Sky,
        Entities,
        Particles,
        Post,
        Other,
        Compute
    }

    private const int Whats = 7, MaxStages = 32;
    private static readonly int[] SectionIds = new int[MaxStages * Whats]; // 0: not made yet (0 is the frame's transfers)

    internal static void Mark(Drawn what)
    {
        if (!VulkanFrame.Measuring || _renderer is not { } renderer || _api?.World is not ClientMain game) return;
        var stage = (int)Stage(game);
        if (!Index(stage, MaxStages) || !Assert(what <= Drawn.Compute)) return;
        var at = stage * Whats + (int)what;
        if (SectionIds[at] == 0) SectionIds[at] = VulkanFrame.SectionOf(Group(Stage(game)) + " " + Named(what));
        renderer.Frame.Section(SectionIds[at]);
    }

    private static void Mark(Part part)
    {
        if (!Assert(part <= Part.Other)) return;
        Mark(part switch
        {
            Part.Sky => Drawn.Sky,
            Part.Entities => Drawn.Entities,
            Part.Particles => Drawn.Particles,
            Part.Post => Drawn.Post,
            _ => Drawn.Other
        });
    }

    // RenderCost's groups by the stages the terrain is drawn in: the shadow maps, opaque, and the liquid depth before, the
    // transparent pass and the one after it
    private static readonly EnumRenderStage[] TerrainStages =
    [
        EnumRenderStage.ShadowFar, EnumRenderStage.ShadowNear, EnumRenderStage.Opaque, EnumRenderStage.Before,
        EnumRenderStage.OIT, EnumRenderStage.AfterOIT
    ];

    private static readonly int[] TerrainGroups = [0, 0, 1, 2, 2, 2], TerrainSections = [-1, -1, -1, -1, -1, -1];

    // While Vulkan draws the opaque terrain: the HUD's terrain GPU rows from the last timed frame's sections, in RenderCost's
    // groups; false while OpenGL draws it (its timer queries tell then)
    public static bool TerrainGpuMs(Span<double> groups) => TimesTerrain && TerrainMs(_renderer!.Frame, groups);

    public static bool TimesTerrain => Runs(Pass.Opaque) && _renderer is not null;

    // The opaque terrain's pipeline statistics in the last counted frame (Occlusion's order) while Vulkan draws it, and the area it
    // drew into; NaN while OpenGL draws it
    public static double TerrainStatistic(int at) => TimesTerrain ? _renderer!.Frame.LastStatistic(at) : double.NaN;

    public static double TerrainPixels => TimesTerrain ? _renderer!.Frame.CountedPixels : double.NaN;

    // The whole frame's GPU time while Vulkan draws all of it (the window too): Vulkan's busy time in the last timed frame; NaN
    // while OpenGL draws any of it
    public static double FrameGpuMs => State == Phase.Running && _windowing && _renderer is { } renderer
        ? renderer.Frame.LastBusyMs
        : double.NaN;

    internal static bool TerrainMs(VulkanFrame frame, Span<double> groups)
    {
        if (!NotNull(frame) || !Assert(groups.Length >= 3)) return false;
        groups.Clear();
        for (var i = 0; i < TerrainStages.Length; i++)
        {
            if (TerrainSections[i] < 0)
                TerrainSections[i] = VulkanFrame.SectionOf(Group(TerrainStages[i]) + " " + Named(Drawn.Terrain));
            groups[TerrainGroups[i]] += frame.LastMs(TerrainSections[i]);
        }

        return Assert(TerrainGroups.Length == TerrainStages.Length); // NaN before the first timed frame, as OpenGL's rows
    }

    private static string Group(EnumRenderStage stage) => !Assert(Enum.IsDefined(stage)) ? "unknown" : stage switch
    {
        EnumRenderStage.Before => "before",
        EnumRenderStage.ShadowFar => "shadow far",
        EnumRenderStage.ShadowNear => "shadow near",
        EnumRenderStage.Opaque => "opaque",
        EnumRenderStage.OIT => "transparent",
        EnumRenderStage.AfterOIT => "after transparent",
        EnumRenderStage.AfterPostProcessing or EnumRenderStage.AfterBlit => "post",
        EnumRenderStage.AfterFinalComposition or EnumRenderStage.Ortho or EnumRenderStage.Done => "window",
        _ => stage.ToString().ToLowerInvariant()
    };

    private static string Named(Drawn what) => !Assert(what <= Drawn.Compute) ? "unknown" : what switch
    {
        Drawn.Terrain => "terrain",
        Drawn.Sky => "sky",
        Drawn.Entities => "entities",
        Drawn.Particles => "particles",
        Drawn.Post => "post-processing",
        Drawn.Compute => "compute",
        _ => "other"
    };
}

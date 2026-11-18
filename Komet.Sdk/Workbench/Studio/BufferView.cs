using System.Globalization;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

internal enum BufferKind
{
    Final,
    Color,
    Hdr,
    Depth,
    Normal,
    Position,
    Shadow
}

// One render target the preview can show: the engine's framebuffer, the attachment (-1 the depth texture), how it reads,
// and the setting it needs to exist (a ClientSettings name, "" always there)
internal sealed record BufferTarget(string Group, string Name, EnumFrameBuffer Fb, int Attachment, BufferKind Kind,
    string Needs);

// The preview's render target view. Only while the preview is on screen (the screen calls Picture only then) and only the
// shown target: one draw of a full-screen triangle into a small RGBA8 target, linearised depth, normals as color,
// shadow maps with a contrast curve, one channel as grey, a split against a frozen vanilla picture, the inspected
// pixel ringed (the picture lies over the HUD canvas, so the ring is drawn here). The engine's
// textures are sampled through a sampler object without depth compare, so their own parameters stay untouched.
// The pixel inspector reads 1 px of every target once per click. Nothing exists while the workbench is closed.
internal sealed partial class BufferView : IDisposable
{
    internal const int MaxTargets = 32;
    private const int Width = 640;

    public static readonly BufferTarget[] Targets =
    [
        new("Final", "Final image", EnumFrameBuffer.Default, 0, BufferKind.Final, ""),
        new("Final", "Primary color", EnumFrameBuffer.Primary, 0, BufferKind.Color, ""),
        new("Final", "Primary glow", EnumFrameBuffer.Primary, 1, BufferKind.Color, ""),
        new("Final", "Luma (FXAA input)", EnumFrameBuffer.Luma, 0, BufferKind.Color, ""),
        new("Shadows", "Shadow map near", EnumFrameBuffer.ShadowmapNear, -1, BufferKind.Shadow, "ShadowMapQuality2"),
        new("Shadows", "Shadow map far", EnumFrameBuffer.ShadowmapFar, -1, BufferKind.Shadow, "ShadowMapQuality1"),
        new("G-Buffer", "Depth", EnumFrameBuffer.Primary, -1, BufferKind.Depth, ""),
        new("G-Buffer", "Normals", EnumFrameBuffer.Primary, 2, BufferKind.Normal, "SSAOQuality"),
        new("G-Buffer", "Positions", EnumFrameBuffer.Primary, 3, BufferKind.Position, "SSAOQuality"),
        new("G-Buffer", "Liquid depth", EnumFrameBuffer.LiquidDepth, -1, BufferKind.Depth, ""),
        new("G-Buffer", "Transparent accum", EnumFrameBuffer.Transparent, 0, BufferKind.Hdr, ""),
        new("G-Buffer", "Transparent reveal", EnumFrameBuffer.Transparent, 1, BufferKind.Color, ""),
        new("Post", "SSAO", EnumFrameBuffer.SSAO, 0, BufferKind.Color, "SSAOQuality"),
        new("Post", "SSAO blurred", EnumFrameBuffer.SSAOBlurVertical, 0, BufferKind.Color, "SSAOQuality"),
        new("Post", "Find bright (bloom)", EnumFrameBuffer.FindBright, 0, BufferKind.Color, ""),
        new("Post", "Bloom blur mid", EnumFrameBuffer.BlurVerticalMedRes, 0, BufferKind.Color, ""),
        new("Post", "Bloom blur low", EnumFrameBuffer.BlurVerticalLowRes, 0, BufferKind.Color, ""),
        new("Post", "God rays", EnumFrameBuffer.GodRays, 0, BufferKind.Color, "GodRayQuality")
    ];

    public static readonly float[] Contrasts = [1, 4, 16, 64, 256];

    private readonly int[] _viewport = new int[4];
    private int _program, _vao, _sampler, _target, _fb, _snap, _snapFb, _height;
    private int _uSource, _uOther, _uMode, _uChannel, _uContrast, _uSplit, _uFlip, _uRange, _uMark;
    private bool _failed;

    public int Selected { get; set; }
    public int Channel { get; set; } = -1; // -1 all, 0..3 R G B A
    public int Contrast { get; set; } = 2;
    public double Split { get; set; } = -1; // < 0: no split, else the divider's place 0..1 (left mine, right vanilla)
    public bool Large { get; set; }
    public string Snapshot { get; private set; } = ""; // the target the vanilla picture shows, "" none
    public double LastMs { get; private set; }
    public double DisplayWidth { get; set; } = Width; // the picture's width on screen: the inspected pixel's ring keeps its size
    public bool Marking { get; set; } // the ring shows while the Inspector tab does (and never in a vanilla snapshot)

    public BufferTarget Shown => Targets[Math.Clamp(Selected, 0, Targets.Length - 1)];

    // Why a target has no texture now ("" it has one): the setting that allocates it
    public static string Missing(BufferTarget target)
    {
        if (Vulkan.VulkanRenderer.GlIdle && target.Kind != BufferKind.Final)
        {
            return "Vulkan-only renderer: OpenGL targets are not drawn";
        }

        return target.Needs switch
        {
            "ShadowMapQuality1" when ClientSettings.ShadowMapQuality < 1 => "Not allocated – Shadow quality is Off",
            "ShadowMapQuality2" when ClientSettings.ShadowMapQuality < 2 => "Not allocated – Shadow quality below 2",
            "SSAOQuality" when ClientSettings.SSAOQuality < 1 => "Not allocated – Ambient occlusion is Off",
            "GodRayQuality" when ClientSettings.GodRayQuality < 1 => "Not drawn – God rays are Off",
            _ => ""
        };
    }

    // The engine's texture of a target now (0 none): resolved every time, a framebuffer rebuild replaces them
    private static int TextureOf(ICoreClientAPI capi, BufferTarget target)
    {
        var list = capi.Render.FrameBuffers;
        var index = (int)target.Fb;
        if (list is null || index < 0 || index >= list.Count || list[index] is not { } fb) return 0;

        if (target.Attachment < 0) return fb.DepthTextureId;

        return fb.ColorTextureIds is { } ids && target.Attachment < ids.Length ? ids[target.Attachment] : 0;
    }

    public static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

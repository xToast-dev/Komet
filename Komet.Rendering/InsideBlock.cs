using Cairo;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// A camera inside an opaque block (flying through the ground, a camera pushed into a wall) sees every cave behind it: the
// inside of solid ground has no faces. The view is covered with the block's own colour, darkened, before the 2D entity
// names and the GUI, as if the eye were in the block.
internal sealed class InsideBlock(ICoreClientAPI api) : IRenderer
{
    private const float Dark = 0.3f;
    private const double Order = 0.1; // before SystemRenderEntities' 2D pass (0.4) and the GUI (1.0)

    private static InsideBlock? _installed;

    private readonly BlockPos _pos = new(0);
    private readonly Vec4f _tint = new();
    private LoadedTexture? _white;

    public static bool Enabled { get; set; } = true;

    public double RenderOrder => Order;
    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Enabled || Opaque() is not { } color) return;

        _white ??= White(api);
        var tint = _tint;
        _ = ColorUtil.ToRGBAVec4f(color, ref tint);
        _ = tint.Set(tint.R * Dark, tint.G * Dark, tint.B * Dark, 1);
        api.Render.Render2DTexture(_white.TextureId, 0, 0, api.Render.FrameWidth, api.Render.FrameHeight, 0, tint);
    }

    public void Dispose() => _white?.Dispose();

    public static void Install(ICoreClientAPI api)
    {
        if (_installed is not null) return;

        _installed = new InsideBlock(api);
        api.Event.RegisterRenderer(_installed, EnumRenderStage.Ortho, "komet-insideblock");
    }

    public static void Uninstall(ICoreClientAPI? api)
    {
        if (_installed is null) return;

        api?.Event.UnregisterRenderer(_installed, EnumRenderStage.Ortho);
        _installed.Dispose();
        _installed = null;
    }

    // The colour of the block the eye is in, when that block hides everything behind each of its sides
    private int? Opaque()
    {
        if (api.World is not ClientMain { MainCamera.CameraEyePos: { } eye } ||
            !Finite(eye.X + eye.Y + eye.Z))
        {
            return null;
        }

        _ = _pos.SetAndCorrectDimension((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
        var block = api.World.BlockAccessor.GetBlock(_pos);
        if (block is not { Id: > 0, AllSidesOpaque: true } || block.IsLiquid()) return null;

        var color = block.GetColorWithoutTint(api, _pos);
        return color == -1 ? unchecked((int)0xFF202020) : color;
    }

    private static LoadedTexture White(ICoreClientAPI api)
    {
        var white = new LoadedTexture(api);
        try
        {
            using var surface = new ImageSurface(Format.Argb32, 4, 4);
            using (var ctx = new Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Paint();
            }

            surface.Flush();
            api.Gui.LoadOrUpdateCairoTexture(surface, false, ref white);
            return white;
        }
        catch
        {
            white.Dispose(); // no texture without its picture
            throw;
        }
    }
}

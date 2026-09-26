namespace Komet.Hud;

// The grid drawn under a drag, by the HUD and lent to its dialogs: one full-screen texture (2560x1440 is a 14.7 MB surface and a
// TexImage2D), built on the first drag and again only when the frame size or the GUI scale changed; the surface goes once uploaded.
// built is told of each build, a frame of its own cost.
internal sealed class SnapGrid(ICoreClientAPI capi, Action built) : IDisposable
{
    private HudCanvas? _canvas;
    private (int Width, int Height, int Scale) _key;

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
    }

    public void Draw()
    {
        var key = (capi.Render.FrameWidth, capi.Render.FrameHeight,
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (!Assert(key.FrameWidth > 0 && key.FrameHeight > 0)) return;
        if (_canvas is null || key != _key)
        {
            _canvas?.Dispose();
            _canvas = HudCanvas.Grid(capi, scaled(HudSettings.SnapGrid));
            _key = key;
            built();
        }

        _canvas.Draw(0, 0);
    }
}

namespace Komet.Hud;

// The overlay's cards (overlay, frames, details) are dragged on the snap grid of the HUD's dialogs while the mouse is
// free (a dialog open), stay where they are dropped and go back to their own place with a right click
internal sealed partial class OverlayCard
{
    private const int Overlay = 0, Frames = 1, DetailCard = 2, Cards = 3;
    private readonly (double X, double Y, double W, double H)[] _shown = new (double, double, double, double)[Cards];
    private (double X, double Y) _grab;
    private int _dragging = -1;

    // Where the player dropped the card, NaN when it takes its own place
    private (double X, double Y) Saved(int card) => card switch
    {
        Overlay => (settings.OverlayX, settings.OverlayY),
        Frames => (settings.FramesX, settings.FramesY),
        _ => (settings.DetailX, settings.DetailY)
    };

    private void Save(int card, double x, double y)
    {
        switch (card)
        {
            case Overlay:
                (settings.OverlayX, settings.OverlayY) = (x, y);
                break;
            case Frames:
                (settings.FramesX, settings.FramesY) = (x, y);
                break;
            default:
                (settings.DetailX, settings.DetailY) = (x, y);
                break;
        }
    }

    private void Show(int card, HudCanvas canvas, (double X, double Y) own, double w, double h)
    {
        var (sx, sy) = Saved(card);
        var (x, y) = double.IsFinite(sx) && double.IsFinite(sy) ? (sx, sy) : own;
        x = Math.Round(Math.Clamp(x, 0, Math.Max(0, w - canvas.Width)));
        y = Math.Round(Math.Clamp(y, 0, Math.Max(0, h - canvas.Height)));
        canvas.Draw(x, y);
        _shown[card] = (x, y, canvas.Width, canvas.Height);
    }

    public void Mouse(IClientEventAPI events, bool on)
    {
        if (on)
        {
            events.MouseDown += Down;
            events.MouseMove += Move;
            events.MouseUp += Up;
            return;
        }

        events.MouseDown -= Down;
        events.MouseMove -= Move;
        events.MouseUp -= Up;
    }

    // The topmost card under the mouse: drawn last, found first
    private int Under(MouseEvent e)
    {
        for (var i = 0; i < Cards; i++)
        {
            var card = Cards - 1 - i;
            var r = _shown[card];
            if (r.W > 0 && e.X >= r.X && e.X < r.X + r.W && e.Y >= r.Y && e.Y < r.Y + r.H) return card;
        }

        return -1;
    }

    private void Down(MouseEvent e)
    {
        if (e.Handled || capi.Input.MouseGrabbed || Under(e) is not (>= 0 and var card)) return;

        e.Handled = true;
        if (e.Button == EnumMouseButton.Right)
        {
            Save(card, double.NaN, double.NaN);
            settings.Save(capi);
            return;
        }

        (_grab, _dragging) = ((e.X - _shown[card].X, e.Y - _shown[card].Y), card);
    }

    private void Move(MouseEvent e)
    {
        if (_dragging < 0) return;

        var r = _shown[_dragging];
        Save(_dragging, HudMotion.Snap(e.X - _grab.X, r.W, capi.Render.FrameWidth),
            HudMotion.Snap(e.Y - _grab.Y, r.H, capi.Render.FrameHeight));
        e.Handled = true;
    }

    private void Up(MouseEvent e)
    {
        if (_dragging < 0) return;

        _dragging = -1;
        e.Handled = true;
        snap.Release();
        settings.Save(capi);
    }
}

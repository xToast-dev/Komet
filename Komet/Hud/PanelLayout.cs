namespace Komet.Hud;

// One panel as the layout reads it, once a frame: shown means visible and drawn at least once (a panel without a texture takes no room)
internal readonly record struct PanelBox(
    bool Shown,
    int Column,
    double Width,
    double Height,
    (double X, double Y)? Pin);

// The screen and the settings the layout depends on, in pixels; Dragging is the panel under the cursor, or -1
internal readonly record struct LayoutFrame(
    double Width,
    double Height,
    HudCorner Corner,
    double Gap,
    double Margin,
    double Slack,
    int Dragging);

// Where each panel draws and in which layer. Pinned panels stay where the player put them. The rest flow down three columns from the
// HUD's corner, a column taller than the screen going on beside itself, and one whose slot would cover a panel already placed moves to
// the nearest free spot. Layers, bottom to top: panels that found no free room, pinned panels, the column panels; a squeezed panel lies
// beneath what it covers, so a log loses its oldest lines rather than hiding another panel's last ones.
internal sealed class PanelLayout
{
    public const int Columns = 3, Layers = 3, Hidden = -1;
    private const int MaxPanels = 32, FrameInputs = 7, PanelInputs = 6;
    private readonly PanelRect[] _drawn; // screen rectangles, kept on screen the way HudCanvas.Draw keeps them
    private readonly double[] _inputs;
    private readonly int[] _layer;
    private readonly PanelPlacer _placer;

    public PanelLayout(int count)
    {
        var n = Assert(count is > 0 and <= MaxPanels) ? count : 1;
        (_placer, _drawn, _layer, _inputs) = (new PanelPlacer(n), new PanelRect[n], new int[n],
            new double[FrameInputs + PanelInputs * n]);
        Array.Fill(_layer, Hidden);
        Array.Fill(_inputs, double.NaN); // the first Update lays out
    }

    public int Layer(int panel)
    {
        return Index(panel, _layer.Length) ? _layer[panel] : Hidden;
    }

    public PanelRect Drawn(int panel)
    {
        return Index(panel, _drawn.Length) && Assert(_layer[panel] != Hidden) ? _drawn[panel] : default;
    }

    // Lays out again only when a panel appeared, resized or moved, or the screen, corner or scale changed; true when it did
    public bool Update(ReadOnlySpan<PanelBox> panels, in LayoutFrame frame)
    {
        if (!Assert(panels.Length == _layer.Length) ||
            !Assert(frame is { Width: > 0, Height: > 0, Gap: >= 0, Margin: >= 0 })) return false;
        if (!Changed(panels, frame)) return false;
        Pinned(panels, frame);
        var x = frame.Margin;
        for (var column = 0; column < Columns; column++) x = Flow(panels, frame, column, x);
        return Assert(x >= frame.Margin);
    }

    // The panel on top at a point, the one a click grabs: top layer first, and within a layer the later drawn
    public int Topmost(double x, double y)
    {
        if (!Finite(x) || !Finite(y)) return Hidden;
        var count = Math.Min(_layer.Length, MaxPanels);
        for (var k = 0; k < Layers; k++)
        {
            for (var j = 0; j < Math.Min(count, MaxPanels); j++)
            {
                var (i, layer) = (count - 1 - j, Layers - 1 - k);
                if (_layer[i] == layer && _drawn[i] is var r && x >= r.X && x < r.Right && y >= r.Y &&
                    y < r.Bottom) return i;
            }
        }

        return Hidden;
    }

    // Grid first; the edges of other panels (a gap away or aligned) and the screen margin win within distance
    public double Snap(double pos, double size, bool horizontal, in LayoutFrame frame, double step, double distance)
    {
        if (!Finite(pos) || !Assert(size > 0) || !Assert(step > 0)) return pos;
        var (best, bestDistance) = (Math.Round(pos / step) * step, distance);
        var screen = horizontal ? frame.Width : frame.Height;
        Consider(frame.Margin, screen - 2 * frame.Margin, pos, size, frame.Gap, ref best, ref bestDistance);
        for (var i = 0; i < Math.Min(_layer.Length, MaxPanels); i++)
        {
            if (i == frame.Dragging || _layer[i] == Hidden) continue;
            var r = _drawn[i];
            Consider(horizontal ? r.X : r.Y, horizontal ? r.Width : r.Height, pos, size, frame.Gap, ref best,
                ref bestDistance);
        }

        return best;
    }

    private static void Consider(double start, double length, double pos, double size, double gap, ref double best,
        ref double distance)
    {
        if (!Finite(start) || !Assert(length >= 0)) return;
        foreach (var candidate in (ReadOnlySpan<double>)
                 [start + length + gap, start - size - gap, start, start + length - size])
        {
            var d = Math.Abs(candidate - pos);
            if (d < distance) (best, distance) = (candidate, d);
        }
    }

    private bool Changed(ReadOnlySpan<PanelBox> panels, in LayoutFrame frame)
    {
        Span<double> inputs = stackalloc double[FrameInputs + PanelInputs * MaxPanels];
        inputs = inputs[.._inputs.Length];
        (inputs[0], inputs[1], inputs[2], inputs[3]) = (frame.Width, frame.Height, (double)frame.Corner, frame.Gap);
        (inputs[4], inputs[5], inputs[6]) = (frame.Margin, frame.Slack, frame.Dragging);
        for (var i = 0; i < Math.Min(panels.Length, MaxPanels); i++)
        {
            var (p, at) = (panels[i], FrameInputs + PanelInputs * i);
            var (w, h) = p.Shown ? (p.Width, p.Height) : (0, 0);
            var (pinned, px, py) = p.Pin is var (x, y) ? (1, x, y) : (0, 0.0, 0.0);
            (inputs[at], inputs[at + 1], inputs[at + 2]) = (p.Shown ? 1 : 0, w, h);
            (inputs[at + 3], inputs[at + 4], inputs[at + 5]) = (pinned, px, py);
        }

        if (inputs.SequenceEqual(_inputs)) return false;
        inputs.CopyTo(_inputs);
        return true;
    }

    // Pinned panels go first, into the placer as well, so the columns make room around them; the one being dragged takes no room yet:
    // under the cursor, the others' new edges would become snap targets
    private void Pinned(ReadOnlySpan<PanelBox> panels, in LayoutFrame frame)
    {
        var (left, top) = (frame.Corner is HudCorner.TopLeft or HudCorner.BottomLeft,
            frame.Corner is HudCorner.TopLeft or HudCorner.TopRight);
        _placer.Clear();
        for (var i = 0; i < Math.Min(panels.Length, MaxPanels); i++)
        {
            _layer[i] = Hidden;
            if (panels[i] is not { Shown: true, Pin: var (px, py) } p) continue;
            var at = OnScreen(new PanelRect(px, py, p.Width, p.Height), frame);
            (_drawn[i], _layer[i]) = (at, 1);
            if (i == frame.Dragging) continue;
            _placer.Take(
                at with { X = left ? at.X : frame.Width - at.Right, Y = top ? at.Y : frame.Height - at.Bottom });
        }

        _ = Assert(_placer.Count <= panels.Length);
    }

    // The column's panels in corner space from the margin down, starting at x; a column taller than the screen goes on beside itself.
    // Returns where the next column starts.
    private double Flow(ReadOnlySpan<PanelBox> panels, in LayoutFrame frame, int column, double x)
    {
        var (left, top) = (frame.Corner is HudCorner.TopLeft or HudCorner.BottomLeft,
            frame.Corner is HudCorner.TopLeft or HudCorner.TopRight);
        var (y, width) = (frame.Margin, ColumnWidth(panels, column));
        if (!Index(column, Columns) || !Finite(x)) return frame.Margin;
        for (var i = 0; i < Math.Min(panels.Length, MaxPanels); i++)
        {
            var p = panels[i];
            if (p.Column != column || p.Pin is not null || !p.Shown) continue;
            if (y > frame.Margin && y + p.Height > frame.Height - frame.Margin)
                (x, y) = (x + width + frame.Gap, frame.Margin);
            var slot = new PanelRect(x, y, p.Width, p.Height);
            var (px, py, free) = _placer.Place(slot, frame.Width, frame.Height, frame.Margin, frame.Gap, frame.Slack);
            var at = slot with { X = px, Y = py };
            _placer.Take(at);
            var screen = at with
            {
                X = left ? at.X : frame.Width - at.Right,
                Y = top ? at.Y : frame.Height - at.Bottom
            };
            (_drawn[i], _layer[i]) = (OnScreen(screen, frame), free ? 2 : 0);
            y += p.Height + frame.Gap;
        }

        return x + width + frame.Gap;
    }

    private static double ColumnWidth(ReadOnlySpan<PanelBox> panels, int column)
    {
        double width = 0;
        for (var i = 0; i < Math.Min(panels.Length, MaxPanels); i++)
            if (panels[i] is { Shown: true, Pin: null } p && p.Column == column)
                width = Math.Max(width, p.Width);
        return Assert(width >= 0) ? width : 0;
    }

    // HudCanvas.Draw keeps a panel on screen: the same clamp, so clicks and snaps see where it is drawn
    private static PanelRect OnScreen(PanelRect r, in LayoutFrame frame)
    {
        return Finite(r.X) && Finite(r.Y)
            ? r with
            {
                X = Math.Clamp(r.X, 0, Math.Max(0, frame.Width - r.Width)),
                Y = Math.Clamp(r.Y, 0, Math.Max(0, frame.Height - r.Height))
            }
            : r with { X = 0, Y = 0 };
    }
}

// A panel's rectangle in corner space: distances from the HUD's corner, so one placement serves all four corners
internal readonly record struct PanelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public double Overlap(PanelRect other)
    {
        var (w, h) = Intersection(other);
        return w > 0 && h > 0 ? w * h : 0;
    }

    // How far the two reach into each other: the thinner side of the shared area
    public double Depth(PanelRect other)
    {
        var (w, h) = Intersection(other);
        return w > 0 && h > 0 ? Math.Min(w, h) : 0;
    }

    private (double W, double H) Intersection(PanelRect other)
    {
        return (Math.Min(Right, other.Right) - Math.Max(X, other.X),
            Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y));
    }
}

// Where an unpinned panel goes when its column slot would cover a panel already on screen. The candidates are the screen's edges and
// every taken panel's edges (beside it, below it, aligned with it); the free one nearest the slot wins, and when the screen has no
// room left, the one that covers the least. An overlap no deeper than the slack (a panel's blank padding) hides no text and does not
// count, or a column that grew by a pixel next to a pinned panel would move away.
internal sealed class PanelPlacer(int capacity)
{
    private const int MaxPanels = 64;
    private const int FixedCandidates = 3; // the margin, the slot and the far edge
    private const double Epsilon = 1e-6; // square pixels: covers equal within rounding tie on distance

    private readonly PanelRect[] _taken =
        new PanelRect[Assert(capacity > 0) && Assert(capacity <= MaxPanels) ? capacity : 1];

    public int Count { get; private set; }

    public void Clear()
    {
        Count = 0;
    }

    public void Take(PanelRect rect)
    {
        if (!Assert(Count < _taken.Length) || !Assert(rect is { Width: >= 0, Height: >= 0 })) return;
        _taken[Count++] = rect;
    }

    // Free: nothing taken lies under it (beyond the slack)
    public (double X, double Y, bool Free) Place(PanelRect slot, double screenWidth, double screenHeight, double margin,
        double gap,
        double slack)
    {
        if (!Assert(slot is { Width: >= 0, Height: >= 0 }) || !Assert(margin >= 0 && gap >= 0 && slack >= 0))
            return (slot.X, slot.Y, true);
        // Where the canvas would draw it: a column past the screen's edge is pushed back over its neighbour
        slot = slot with
        {
            X = Math.Clamp(slot.X, 0, Math.Max(0, screenWidth - slot.Width)),
            Y = Math.Clamp(slot.Y, 0, Math.Max(0, screenHeight - slot.Height))
        };
        if (Covered(slot, slack) <= Epsilon) return (slot.X, slot.Y, true);
        var (best, bestCover, bestDistance) = (slot, double.MaxValue, double.MaxValue);
        var count = FixedCandidates + 2 * Count;
        for (var i = 0; i < Math.Min(count, FixedCandidates + 2 * MaxPanels); i++)
        {
            for (var j = 0; j < Math.Min(count, FixedCandidates + 2 * MaxPanels); j++)
            {
                var at = slot with
                {
                    X = Candidate(i, slot.X, slot.Width, screenWidth, margin, gap, true),
                    Y = Candidate(j, slot.Y, slot.Height, screenHeight, margin, gap, false)
                };
                if (at.X < 0 || at.Y < 0 || at.Right > screenWidth || at.Bottom > screenHeight) continue;
                var (cover, distance) = (Covered(at, slack), Math.Abs(at.X - slot.X) + Math.Abs(at.Y - slot.Y));
                if (cover < bestCover - Epsilon || (cover <= bestCover + Epsilon && distance < bestDistance))
                    (best, bestCover, bestDistance) = (at, cover, distance);
            }
        }

        // A panel larger than the screen keeps its slot, and the canvas keeps it on screen
        return (best.X, best.Y, bestCover <= Epsilon);
    }

    // 0 margin, 1 slot, 2 far edge, then per taken panel its near edge and the spot past its far edge
    private double Candidate(int index, double slot, double size, double screen, double margin, double gap,
        bool horizontal)
    {
        if (!Assert(index >= 0 && index < FixedCandidates + 2 * Count)) return slot;
        if (index < FixedCandidates) return index switch { 0 => margin, 1 => slot, _ => screen - margin - size };
        var rect = _taken[(index - FixedCandidates) / 2];
        var (start, end) = horizontal ? (rect.X, rect.Right) : (rect.Y, rect.Bottom);
        return (index - FixedCandidates) % 2 == 0 ? start : end + gap;
    }

    private double Covered(PanelRect rect, double slack)
    {
        double cover = 0;
        for (var i = 0; i < Math.Min(Count, MaxPanels); i++)
            if (rect.Depth(_taken[i]) > slack)
                cover += rect.Overlap(_taken[i]);
        return cover;
    }
}

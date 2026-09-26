namespace Komet.Hud;

// Panels measured at the interval but not drawn yet. Drawing is the dear half of a refresh: every string through Cairo and the whole
// surface through glTexSubImage2D, 0.1-1.6 ms per panel and 3-6 ms for all of them on one frame. Taken one per frame, round-robin, so
// a panel that keeps coming due cannot starve the others.
internal sealed class PanelQueue(int count)
{
    private const int MaxPanels = 31; // one bit each
    private readonly int _capacity = Assert(count is > 0 and <= MaxPanels) ? count : 1;
    private int _pending, _cursor;

    public void Add(int panel)
    {
        if (Index(panel, _capacity)) _pending |= 1 << panel;
    }

    public void Remove(int panel)
    {
        if (Index(panel, _capacity)) _pending &= ~(1 << panel);
    }

    // The next pending panel after the last one taken, or -1
    public int Next()
    {
        if (_pending == 0 || !Assert(_pending >> _capacity == 0)) return -1;
        for (var i = 0; i < Math.Min(_capacity, MaxPanels); i++)
        {
            var panel = (_cursor + i) % _capacity;
            if ((_pending & (1 << panel)) == 0) continue;
            _pending &= ~(1 << panel);
            _cursor = panel + 1;
            return panel;
        }

        _ = Assert(_pending == 0); // not reached: the loop finds every pending bit below _capacity
        return -1;
    }
}

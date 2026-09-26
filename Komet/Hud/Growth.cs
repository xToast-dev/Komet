namespace Komet.Hud;

// A total's growth since this instance last read it. Each HUD row owns its instances, so a row covers the span since its own last
// read. NaN on the first read, across a pause of counting (Counting.Epoch) and when the total fell (a new world started from zero).
internal sealed class Growth(Func<double> total)
{
    private int _epoch = -1;
    private double _last = double.NaN;

    public double Next()
    {
        var now = total();
        var grown = _epoch == Counting.Epoch && now >= _last ? now - _last : double.NaN;
        (_last, _epoch) = (now, Counting.Epoch);
        return Finite(now) && Assert(_epoch >= 0) ? grown : double.NaN;
    }
}

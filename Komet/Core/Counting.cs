namespace Komet.Core;

// Whether the features keep their counters, totals that only grow (readers take differences). Two owners, so neither can switch the
// other's counting off. Hud: every counter, while the HUD shows them or runs its own bench. Bench: a scripted benchmark records; it
// reads only the tessellation counters (TessAccounting, TessSchedule.NearWaiting), which count while On. The others stay off for it:
// their cost falls on the feature arms alone, never on the engine arm.
internal static class Counting
{
    public static bool On { get; private set; }

    // Raised each time Hud turns true. Before it the HUD's frames and the totals did not run together (the HUD-only totals stood still,
    // or the HUD was hidden while the bench kept the tessellation totals going), so a difference across it is no rate.
    public static int Epoch { get; private set; }

    public static bool Hud
    {
        get;
        set
        {
            if (value && !field) Epoch++;
            field = value;
            Update();
        }
    }

    public static bool Bench
    {
        get;
        set
        {
            field = value;
            Update();
        }
    }

    private static void Update()
    {
        On = Hud || Bench;
        _ = Assert(Epoch >= 0) && Assert(!Hud || Epoch > 0);
    }
}

// Totals that only grow, counted on several threads at once (the tessellation threads) without an interlocked add per count: each
// thread adds to a slot of its own, a cache line apart from the others, and a read sums the slots. Slots are handed out per thread for
// the life of the process; the threads past the first Slots - 1 share the last slot through interlocked adds. A slot has one writer,
// and aligned 64-bit reads and writes are atomic, so a read sees each slot before or after an add and two reads never go backwards.
internal sealed class Tally
{
    private const int MaxWidth = 16;
    private const int Slots = 64, Line = 8; // eight longs: one 64-byte cache line
    private static int _claimed;
    [ThreadStatic] private static int _slot; // this thread's slot + 1; 0 until it first counts
    private readonly long[] _cells;

    private readonly int _width, _stride;

    public Tally(int width)
    {
        _ = Assert(width is > 0 and <= MaxWidth);
        _width = Math.Clamp(width, 1, MaxWidth);
        _stride = (_width + Line - 1) / Line * Line;
        _cells = new long[Slots * _stride];
    }

    public void Add(int counter, long n)
    {
        if (!Index(counter, _width) || !Assert(n >= 0)) return;
        var slot = _slot - 1;
        if (slot < 0) slot = Claim();
        if (slot < Slots - 1) _cells[slot * _stride + counter] += n;
        else _ = Interlocked.Add(ref _cells[slot * _stride + counter], n);
    }

    public long Total(int counter)
    {
        if (!Index(counter, _width)) return 0;
        long sum = 0;
        for (var slot = 0; slot < Slots; slot++) sum += Volatile.Read(ref _cells[slot * _stride + counter]);
        return Assert(sum >= 0) ? sum : 0;
    }

    // Only while no thread counts (a new world before its tessellation starts, or a test)
    public void Clear() => Array.Clear(_cells);

    private static int Claim()
    {
        var slot = Math.Min(Interlocked.Increment(ref _claimed), Slots) - 1;
        _slot = slot + 1;
        return Assert(slot is >= 0 and < Slots) ? slot : Slots - 1;
    }
}

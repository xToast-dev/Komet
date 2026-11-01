namespace Komet.Core;

// Feature counters on: Hud (all, while shown or benching) or Bench (tessellation only, so the engine arm pays nothing).
internal static class Counting
{
    public static bool On { get; private set; }

    // Raised when Hud turns true: a difference across it is no rate.
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
    }
}

// Ever-growing totals without one shared hot cell: each add goes to the cache-line slot of the processor it runs on, a read
// sums them. Threads on one processor (or processors past Slots, which wrap) share a slot, so adds are interlocked; they
// rarely contend. Atomic 64-bit slots: two reads never go backwards.
internal sealed class Tally
{
    private const int MaxWidth = 16;
    private const int Slots = 64, Line = 8; // eight longs: one 64-byte cache line
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
        var slot = Thread.GetCurrentProcessorId() & (Slots - 1);
        _ = Interlocked.Add(ref _cells[slot * _stride + counter], n);
    }

    public long Total(int counter)
    {
        if (!Index(counter, _width)) return 0;
        long sum = 0;
        for (var slot = 0; slot < Slots; slot++) sum += Volatile.Read(ref _cells[slot * _stride + counter]);
        return sum;
    }

    public void Clear()
    {
        Array.Clear(_cells);
    }
}

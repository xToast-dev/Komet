namespace Komet.Vulkan;

// Bottom-of-pipe timestamps: an interval is its earlier stamp's section, a setup's the transfers'. Rest: GL or idle.
// A frame is timed while measuring (VulkanGpuTimes) when the counters run (the HUD shown, a bench), else every
// UntimedEvery-th frame for the log's averages: the HUD hidden, most frames write no timestamps.
// Pipeline statistics of draws posted in the frame's counted section (opaque terrain), every Every-th frame: one query
// per run inside a rendering, ended before any other draw, clear, call or rendering end.
internal sealed unsafe partial class VulkanFrame
{
    private const int QueriesPerSlot = 1024, MaxSections = 128, ReportSections = 14, UntimedEvery = 16;
    private const int SetupEnds = -3, SetupBegins = -2, Closing = -1, Transferring = 0, Unnamed = 1;
    public const int Statistics = 4;
    private const int QueriesPerFrame = 64, Every = 4, ResultBytes = 8 * Statistics, MaxResults = Statistics * QueriesPerFrame;

    private static readonly List<string> SectionNames = ["transfers", "unnamed"];
    private static readonly Dictionary<string, int> SectionIds =
        new(StringComparer.Ordinal) { ["transfers"] = Transferring, ["unnamed"] = Unnamed };

    private readonly double[] _sectionNs = new double[MaxSections], _lastNs = new double[MaxSections];
    private ulong _stamps;
    private int _section = -1;
    private double _busyNs, _spanNs, _lastBusyMs = -1;
    private long _timed;
    private readonly double[] _statisticTotals = new double[Statistics];
    private readonly double[] _lastStatistics = [double.NaN, double.NaN, double.NaN, double.NaN];
    private ulong _statistics;
    private long _counting = -1, _countedFrames; // the query open now, -1 for none
    private double _countedPixels = double.NaN; // the area of the rendering the last counted run drew into

    public static bool Measuring { get; set; } = true;

    // The frame being recorded writes timestamps (Section, TimingBegins, TimingEnds)
    public bool Timing => _slot is { Timed: true };

    // Decided once per frame as Next begins it
    private bool TimedFrame() =>
        Measuring && (Core.Counting.On || Number % UntimedEvery == 0);

    // Last timed frame (Slots frames back), for the HUD: a section's time and Vulkan's busy time; NaN before the first
    public double LastMs(int section) =>
        _lastBusyMs >= 0 && Index(section, MaxSections) ? _lastNs[section] / 1e6 : double.NaN;

    public double LastBusyMs => _lastBusyMs >= 0 ? _lastBusyMs : double.NaN;

    public static int SectionOf(string name)
    {
        if (!NotNull(name)) return Unnamed;
        if (SectionIds.TryGetValue(name, out var known)) return known;
        if (SectionNames.Count >= MaxSections) return Unnamed;
        var id = SectionNames.Count;
        SectionIds[name] = id;
        SectionNames.Add(name);
        return id;
    }

    public void Section(int section)
    {
        if (section == _section || !Index(section, MaxSections)) return;
        _section = section;
        if (_open is not null && Timing) Stamped(section, posted: true);
    }

    private void TimingBegins(Segment segment)
    {
        if (_slot is not { Timed: true } slot || (_stamps == 0 && !MadeStamps())) return;
        if (slot.Stamps.Count == 0)
            VkApi.CmdResetQueryPool(segment.Setup, _stamps, (uint)(slot.Index * QueriesPerSlot), QueriesPerSlot);
        Stamped(SetupBegins, posted: false, segment.Setup);
        Stamped(_section >= 0 ? _section : Unnamed, posted: true);
    }

    private void TimingEnds(Segment segment)
    {
        if (_stamps == 0 || _slot is not { Timed: true, Stamps.Count: > 0 }) return;
        Stamped(Closing, posted: true);
        Stamped(SetupEnds, posted: false, segment.Setup);
    }

    private void Stamped(int section, bool posted, IntPtr setup = 0)
    {
        if (_slot is not { } slot || _stamps == 0 || slot.Stamps.Count >= QueriesPerSlot) return;
        var query = (uint)(slot.Index * QueriesPerSlot + slot.Stamps.Count);
        slot.Stamps.Add(section);
        if (posted) Post(new Op { Kind = OpKind.Timestamp, Pipeline = _stamps, Count = query, First = Vk.StageBottom });
        else VkApi.CmdWriteTimestamp(setup, Vk.StageBottom, _stamps, query);
    }

    private bool MadeStamps()
    {
        if (_device.TimestampPeriod <= 0) return false;
        _stamps = _device.QueryPool(Vk.QueryTimestamp, QueriesPerSlot * Slots);
        return _stamps != 0;
    }

    // Once the slot's fence passed
    private void Timed(Slot slot, bool finished)
    {
        var count = slot.Stamps.Count;
        if (count == 0) return;
        var ticks = stackalloc ulong[count]; // not QueriesPerSlot: stackalloc zeroes it
        var read = finished && _stamps != 0 && VkApi.GetQueryPoolResults(_device.Handle, _stamps,
            (uint)(slot.Index * QueriesPerSlot), (uint)count, (nuint)(8 * count), ticks, 8, Vk.Result64) == Vk.Success;
        if (read) Summed(slot.Stamps, new ReadOnlySpan<ulong>(ticks, count), _device.TimestampPeriod);
        slot.Stamps.Clear();
    }

    private void Summed(List<int> stamps, ReadOnlySpan<ulong> ticks, double period)
    {
        ulong first = ulong.MaxValue, last = 0, setup = 0, previous = 0;
        var busy = 0.0;
        Array.Clear(_lastNs);
        var open = Closing; // the section the last timestamp of a command buffer opened, Closing between them
        for (var i = 0; i < Math.Min(stamps.Count, QueriesPerSlot); i++)
        {
            var t = ticks[i];
            var section = stamps[i];
            first = Math.Min(first, t);
            last = Math.Max(last, t);
            if (section == SetupBegins) setup = t;
            else if (section == SetupEnds) busy += Add(Transferring, t - setup, period);
            else
            {
                if (open >= 0 && t >= previous) busy += Add(open, t - previous, period);
                open = section;
                previous = t;
            }
        }

        if (last < first) return;
        (_busyNs, _spanNs, _lastBusyMs) = (_busyNs + busy, _spanNs + (last - first) * period, busy / 1e6);
        _timed++;
    }

    private double Add(int section, ulong ticks, double period)
    {
        if (!Index(section, MaxSections) || ticks > 1UL << 40) return 0;
        var ns = ticks * period;
        _sectionNs[section] += ns;
        _lastNs[section] += ns;
        return ns;
    }

    public string Times()
    {
        if (_timed == 0) return "";
        var frames = (double)_timed;
        var order = Enumerable.Range(0, SectionNames.Count).Where(s => _sectionNs[s] > 0)
            .OrderByDescending(s => _sectionNs[s]).Take(ReportSections);
        var sections = string.Join(", ", order.Select(s => $"{SectionNames[s]} {_sectionNs[s] / frames / 1e6:0.00}"));
        var line = $"; GPU per frame in ms ({_timed} frames): {sections}; Vulkan busy {_busyNs / frames / 1e6:0.00} of " +
                   $"{_spanNs / frames / 1e6:0.00} ms from the frame's first timestamp to its last" + StatisticsReport();
        Array.Clear(_sectionNs);
        (_busyNs, _spanNs, _timed) = (0, 0, 0);
        return line;
    }

    // The section whose draws are counted, -1 for none
    public int Counted { get; set; } = -1;

    // Last counted frame in Occlusion's order: VS, FS invocations, clipping primitives in, out; NaN before
    public double LastStatistic(int at) => Index(at, Statistics) ? _lastStatistics[at] : double.NaN;

    public double CountedPixels => _countedPixels;

    // Begin: the counted frame's queries reset in the first segment's setup, before any of them is begun
    private void CountingBegins(Segment segment)
    {
        if (_slot is not { Sampled: true, Reset: false } slot) return;
        if (_statistics == 0 && !MadeStatistics()) return;
        VkApi.CmdResetQueryPool(segment.Setup, _statistics, (uint)(slot.Index * QueriesPerFrame), QueriesPerFrame);
        slot.Reset = true;
    }

    // Post: a counted draw opens a query unless one is open; anything but a counted draw or dynamic state closes it
    private void Counting(OpKind kind)
    {
        var counted = kind == OpKind.Draw && Counted >= 0 && _section == Counted;
        if (_counting >= 0 && !counted && kind is not (OpKind.Viewport or OpKind.Scissor or OpKind.LineWidth or
                OpKind.Timestamp))
        {
            Enqueue(new Op { Kind = OpKind.EndQuery, Pipeline = _statistics, Count = (uint)_counting });
            _counting = -1;
        }

        if (!counted || _counting >= 0 || _slot is not { Reset: true } slot || slot.Queries >= QueriesPerFrame ||
            _rendering is not { } target) return;
        _counting = slot.Index * QueriesPerFrame + slot.Queries++;
        _countedPixels = (double)target.Width * target.Height;
        Enqueue(new Op { Kind = OpKind.BeginQuery, Pipeline = _statistics, Count = (uint)_counting });
    }

    private bool MadeStatistics()
    {
        if (!_device.PipelineStatistics) return false;
        _statistics = _device.QueryPool(Vk.QueryPipelineStatistics, QueriesPerFrame * Slots, Vk.CountedStatistics);
        return _statistics != 0;
    }

    // Next, once the slot's fence passed: its queries summed; then whether the frame begun in it is counted
    private void CountedFrame(Slot slot, bool finished)
    {
        var count = slot.Queries;
        (slot.Queries, slot.Reset) = (0, false);
        // only for the HUD (and the report while counting): queries around the opaque pass cost on every fourth frame
        slot.Sampled = Measuring && Core.Counting.Hud && _device.PipelineStatistics && Number % Every == 0;
        if (count == 0 || !finished || _statistics == 0) return;
        var results = stackalloc ulong[MaxResults];
        if (VkApi.GetQueryPoolResults(_device.Handle, _statistics, (uint)(slot.Index * QueriesPerFrame), (uint)count,
                (nuint)(count * ResultBytes), results, ResultBytes, Vk.Result64) != Vk.Success) return;
        Span<double> sums = stackalloc double[Statistics];
        for (var i = 0; i < Math.Min(Statistics * count, MaxResults); i++) sums[i % Statistics] += results[i];
        // written vertex invocations, clipping invocations, clipping primitives, fragment invocations
        (_lastStatistics[0], _lastStatistics[1], _lastStatistics[2], _lastStatistics[3]) = (sums[0], sums[3], sums[1], sums[2]);
        for (var i = 0; i < Statistics; i++) _statisticTotals[i] += _lastStatistics[i];
        _countedFrames++;
    }

    private string StatisticsReport()
    {
        if (_countedFrames == 0) return "";
        var frames = (double)_countedFrames;
        var (vertices, fragments) = (_statisticTotals[0] / frames, _statisticTotals[1] / frames);
        var line = $"; the opaque terrain in the pipeline a frame ({_countedFrames} frames counted): {vertices / 1e6:0.00} M " +
                   $"vertex shader invocations, {_statisticTotals[2] / frames / 1e6:0.00} M primitives, " +
                   $"{_statisticTotals[3] / frames / 1e6:0.00} M after clipping, {fragments / 1e6:0.00} M fragment shader " +
                   $"invocations ({fragments / Math.Max(1, _countedPixels):0.00} a pixel)";
        Array.Clear(_statisticTotals);
        _countedFrames = 0;
        return line;
    }
}

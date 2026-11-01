using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Komet.Core;

namespace Komet.Diagnostics;

// The runtime's own events during a debug capture, in process (no dotnet-trace): AllocationTick (about every 100 KB a
// thread allocates: its type and amount) and lock contention (who waited how long, and on which thread's lock). Threads
// are grouped by name (Linux /proc comm, digits dropped: komet-worker-3 → komet-worker); elsewhere main or other. The
// events arrive on the runtime's dispatch thread, so everything here is behind one lock; on only while a capture runs.
internal sealed partial class RuntimeEvents : EventListener
{
    private const string Runtime = "Microsoft-Windows-DotNETRuntime", Main = "main", Other = "other";
    private const long GcKeyword = 0x1, ContentionKeyword = 0x4000;
    private const int AllocationTick = 10, ContentionStart = 81, ContentionStop = 91;
    private const int MaxTypes = 8192, MaxThreads = 1024, MaxWaits = 1 << 16, MaxShown = 24;

    private static readonly char[] Numbering = "0123456789-_ #".ToCharArray();
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Thread, string Type), long> _bytes = [];
    private readonly Dictionary<string, (double Ms, long Count)> _waits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _mainBy = new(StringComparer.Ordinal);
    private readonly List<(float At, float Ms)> _mainWaits = [];
    private readonly Dictionary<long, long> _owners = [];
    private readonly Dictionary<long, string> _names = [];
    private EventSource? _source;
    private long _main = -1;
    private DateTime _start;
    private volatile bool _on;

    public readonly record struct Snapshot(
        double Seconds, long Sampled, (string Thread, string Type, long Bytes)[] Types, (string Thread, long Bytes)[] Threads,
        (string Thread, double Ms, long Count)[] Waits, (string Owner, double Ms)[] MainWaitsBy, (float At, float Ms)[] MainWaits)
    {
        public static Snapshot Empty { get; } = new(0, 0, [], [], [], [], []);
    }

    // On the main thread: the capture's start
    public static RuntimeEvents Start()
    {
        var events = new RuntimeEvents();
        events.Begin(OsThread());
        return events;
    }

    private void Begin(long main)
    {
        lock (_gate) (_main, _start, _on) = (main, DateTime.UtcNow, true);
        if (_source is { } source) Enable(source);
    }

    // The base constructor reports sources before Begin: enabled there once on
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!NotNull(eventSource) || eventSource.Name != Runtime) return;
        _source = eventSource;
        if (_on) Enable(eventSource);
    }

    private void Enable(EventSource source)
    {
        EnableEvents(source, EventLevel.Verbose, (EventKeywords)(GcKeyword | ContentionKeyword));
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_on || !NotNull(eventData) || eventData.Payload is not { } payload) return;
        lock (_gate)
        {
            switch (eventData.EventId)
            {
                case AllocationTick:
                    Allocated(eventData, payload);
                    break;
                case ContentionStart:
                    if (Value(eventData, payload, "LockOwnerThreadID") is { } owner && _owners.Count < MaxThreads)
                        _owners[eventData.OSThreadId] = Convert.ToInt64(owner, CultureInfo.InvariantCulture);
                    break;
                case ContentionStop:
                    Waited(eventData, payload);
                    break;
            }
        }
    }

    private void Allocated(EventWrittenEventArgs e, IReadOnlyList<object?> payload)
    {
        var amount = Value(e, payload, "AllocationAmount64") ?? Value(e, payload, "AllocationAmount");
        if (amount is null || Value(e, payload, "TypeName") is not string type || !Assert(type.Length < 1 << 12)) return;
        var key = (Group(e.OSThreadId), type);
        ref var bytes = ref CollectionsMarshal.GetValueRefOrNullRef(_bytes, key);
        if (Unsafe.IsNullRef(ref bytes))
            bytes = ref CollectionsMarshal.GetValueRefOrAddDefault(_bytes, _bytes.Count < MaxTypes ? key : (key.Item1, Other), out _);
        bytes += Convert.ToInt64(amount, CultureInfo.InvariantCulture);
    }

    private void Waited(EventWrittenEventArgs e, IReadOnlyList<object?> payload)
    {
        if (Value(e, payload, "DurationNs") is not { } duration) return;
        var ms = Convert.ToDouble(duration, CultureInfo.InvariantCulture) / 1e6;
        if (!Finite(ms) || ms < 0) return;
        var thread = Group(e.OSThreadId);
        ref var wait = ref CollectionsMarshal.GetValueRefOrNullRef(_waits, thread);
        if (!Unsafe.IsNullRef(ref wait)) wait = (wait.Ms + ms, wait.Count + 1);
        else if (_waits.Count < MaxThreads) _waits[thread] = (ms, 1);
        var owner = _owners.Remove(e.OSThreadId, out var held) ? Group(held) : Other;
        if (e.OSThreadId != _main) return;
        CollectionsMarshal.GetValueRefOrAddDefault(_mainBy, owner, out _) += ms;
        if (_mainWaits.Count < MaxWaits) _mainWaits.Add(((float)(e.TimeStamp - _start).TotalSeconds, (float)ms));
    }

    private static object? Value(EventWrittenEventArgs e, IReadOnlyList<object?> payload, string name)
    {
        var at = e.PayloadNames?.IndexOf(name) ?? -1;
        return at >= 0 && Index(at, payload.Count) ? payload[at] : null;
    }

    // A thread's group: main, its name without digits (Linux), or other
    private string Group(long thread)
    {
        if (thread == _main) return Main;
        if (_names.TryGetValue(thread, out var known)) return known;
        var name = Other;
        if (OperatingSystem.IsLinux() && thread > 0)
            try
            {
                var comm = File.ReadAllText($"/proc/self/task/{thread}/comm").Trim();
                name = comm.TrimEnd(Numbering) is { Length: > 0 } bare ? bare : Other;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                name = Other; // the thread ended before its first event arrived
            }

        if (_names.Count < MaxThreads) _names[thread] = name;
        return name;
    }

    // Bytes sampled so far (tests wait for the runtime's dispatch with it)
    internal long Sampled
    {
        get
        {
            lock (_gate) return _bytes.Values.Sum();
        }
    }

    // The capture's end, on the main thread: listening stops, the totals are copied out
    public Snapshot Stop()
    {
        _on = false;
        Snapshot made;
        lock (_gate)
        {
            var seconds = (DateTime.UtcNow - _start).TotalSeconds;
            var sampled = _bytes.Values.Sum();
            var threads = _bytes.GroupBy(static b => b.Key.Thread).Select(static g => (g.Key, g.Sum(static b => b.Value)))
                .OrderByDescending(static t => t.Item2).Take(MaxShown).ToArray();
            var types = _bytes.OrderByDescending(static b => b.Value).Take(MaxShown)
                .Select(static b => (b.Key.Thread, b.Key.Type, b.Value)).ToArray();
            var waits = _waits.OrderByDescending(static w => w.Value.Ms).Take(MaxShown)
                .Select(static w => (w.Key, w.Value.Ms, w.Value.Count)).ToArray();
            var by = _mainBy.OrderByDescending(static w => w.Value).Take(MaxShown).Select(static w => (w.Key, w.Value)).ToArray();
            made = new Snapshot(seconds, sampled, types, threads, waits, by, [.. _mainWaits]);
        }

        Dispose();
        return Assert(made.Seconds >= 0) ? made : Snapshot.Empty;
    }

    // The calling thread's id as the runtime's events carry it; -1 where unknown
    internal static long OsThread()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return ThreadNice.Current() is var tid && Assert(tid > 0) ? tid : -1;
            if (OperatingSystem.IsWindows()) return GetCurrentThreadId() is var id && Assert(id > 0) ? id : -1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }

        return -1; // macOS: the main thread's waits are not told apart
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();
}

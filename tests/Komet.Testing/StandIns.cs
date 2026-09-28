namespace Komet.Testing;

// Answers the members it was given by name and every other with a default, which is null or zero or false
public class Answers : DispatchProxy
{
    private static readonly IRenderAPI NullRender = Of<IRenderAPI>([]);

    private Dictionary<string, System.Func<object?[]?, object?>> _members = [];

    // Stands in for ICoreClientAPI and IRenderAPI so the engine's pool classes run unchanged: every call answers a default, so
    // AllocateEmptyMesh hands back no mesh, UpdateChunkMesh uploads nothing and UseSSBOs is false.
    public static ICoreClientAPI NullClient { get; } = Of<ICoreClientAPI>(new() { ["get_Render"] = _ => NullRender });

    public static T Of<T>(Dictionary<string, System.Func<object?[]?, object?>> members) where T : class
    {
        var proxy = Create<T, Answers>();
        ((Answers)(object)proxy)._members = members;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null && _members.TryGetValue(targetMethod.Name, out var member)) return member(args);
        var type = targetMethod?.ReturnType;
        return type is { IsValueType: true } && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

// A logger for code that logs what a test does not read: shapes warn for every keyframe element a step-parented piece lacks
public sealed class QuietLogger : LoggerBase
{
    protected override void LogImpl(EnumLogType logType, string format, params object[] args)
    {
    }
}

// Every line logged, formatted, from any thread
public sealed class CapturingLogger : LoggerBase
{
    public List<string> Lines { get; } = [];

    protected override void LogImpl(EnumLogType logType, string format, params object[] args)
    {
        lock (Lines)
        {
            Lines.Add(logType + " " + string.Format(CultureInfo.InvariantCulture, format, args));
        }
    }
}

// Frame profiles the way FrameProfilerUtil builds them. It adds every Mark of one code in a frame into one entry and counts the
// calls, so a 23.7 ms esr-tesseleateshape may be one entity or nine.
public static class Profiles
{
    public const string Tesselate = "esr-tesseleateshape";

    public static long Ticks(double ms)
    {
        return (long)(ms * Stopwatch.Frequency / 1000);
    }

    public static ProfileEntryRange Frame(double totalMs, params (string Code, double Ms)[] marks)
    {
        var range = new ProfileEntryRange { Code = "all", ElapsedTicks = Ticks(totalMs), Marks = [] };
        foreach (var (code, ms) in marks) range.Marks[code] = new ProfileEntry { ElapsedTicks = (int)Ticks(ms) };
        return range;
    }

    // A 23.7 ms esr-tesseleateshape of nine calls; rendTransparent is a child range
    public static ProfileEntryRange Tree()
    {
        var root = new ProfileEntryRange { Code = "all", ElapsedTicks = Ticks(40), Marks = [] };
        root.Marks[Tesselate] = new ProfileEntry((int)Ticks(23.7), 9);
        root.Marks["rend3D-ret-op"] = new ProfileEntry((int)Ticks(5), 1);
        var transparent = new ProfileEntryRange
        { Code = "rendTransparent", ElapsedTicks = Ticks(3), CallCount = 2, Marks = [] };
        transparent.Marks["rendtransp-blocks"] = new ProfileEntry((int)Ticks(2), 4);
        root.ChildRanges = new Dictionary<string, ProfileEntryRange> { ["rendTransparent"] = transparent };
        return root;
    }
}

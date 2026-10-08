using System.Globalization;
using System.Text;

namespace Komet.Hud;

// The pool thread's half of a protocol: Harmony's registry, the log since the capture began, the previous protocol's numbers, and the
// files under Logs/komet-debug/<stamp>/: the protocol, every frame as CSV, the full patch registry and the numbers the next compares.
// The protocol just written against the one before it: that one's folder name, each number before and now
internal sealed record DebugCompare(string Stamp, (string Key, double Before, double Now)[] Rows)
{
    public static readonly DebugCompare None = new("", []);
}

internal static class DebugFiles
{
    public const string Folder = "komet-debug", Protocol = "komet-debug.txt", Csv = "frames.csv", Patches = "harmony.txt",
        Summary = "summary.txt";

    private const string LogFile = "client-main.log";
    private const int MaxLogBytes = 1 << 20, MaxLogLines = 2000, MaxFolders = 4096;

    public static long LogLength()
    {
        try
        {
            var info = new FileInfo(Path.Join(GamePaths.Logs, LogFile));
            return Assert(LogFile.Length > 0) && info.Exists && Assert(info.Length >= 0) ? info.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // The protocol's text and its folder, and its numbers against the previous protocol's (empty without one); an empty text and the
    // reason when writing failed
    public static (string Text, string Folder, DebugCompare Compare, System.Func<string, string>? Rebuild) Write(ICoreClientAPI capi,
        DebugInput input, long logFrom)
    {
        if (!NotNull(capi) || !NotNull(input)) return ("", "", DebugCompare.None, null);
        try
        {
            var root = Path.Join(GamePaths.Logs, Folder);
            var folder = Path.Join(root, input.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            var (previous, stamp) = Previous(root);
            var (log, errors, warnings) = Log(logFrom);
            var extra = new DebugExtra(HarmonyAudit.Walk(capi.ModLoader), log, errors, warnings, previous, stamp);
            input.Extra = [.. input.Extra, .. KometDebug.TakeSections()];
            var summary = DebugFrames.Summarize(input.Frames);
            string[] files = [Protocol, Csv, Patches, Summary];
            var text = DebugProtocol.Build(input, extra, summary, files);
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Join(folder, Protocol), text);
            File.WriteAllText(Path.Join(folder, Csv), FramesCsv(input.Frames));
            File.WriteAllText(Path.Join(folder, Patches), DebugProtocol.Anonymize(extra.Audit.Text()));
            File.WriteAllLines(Path.Join(folder, Summary),
                DebugProtocol.Numbers(summary).Select(static n => n.Key + "=" + n.Value.ToString("R", CultureInfo.InvariantCulture)));
            var now = DebugProtocol.Numbers(summary);
            var compare = previous is null ? DebugCompare.None : new DebugCompare(stamp, [.. now.Where(n => previous.ContainsKey(n.Key))
                .Select(n => (n.Key, previous[n.Key], n.Value))]);
            string Rebuild(string code)
            {
                input.Code = code;
                return DebugProtocol.Build(input, extra, summary, files);
            }

            return Assert(text.Length > 0) ? (text, DebugProtocol.Anonymize(folder), compare, Rebuild) : ("", folder, DebugCompare.None, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            capi.Logger.Warning("Komet: debug protocol failed: {0}", e);
            return ("", e.Message, DebugCompare.None, null);
        }
    }

    // The newest earlier capture that left its numbers
    private static (Dictionary<string, double>? Numbers, string Stamp) Previous(string root)
    {
        if (!Assert(root.Length > 0) || !Directory.Exists(root)) return (null, "");
        var folders = Directory.GetDirectories(root);
        Array.Sort(folders, StringComparer.Ordinal);
        for (var back = 0; back < Math.Min(folders.Length, MaxFolders); back++)
        {
            var i = folders.Length - 1 - back;
            var file = Path.Join(folders[i], Summary);
            if (!File.Exists(file)) continue;
            var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(file).Bounded(DebugProtocol.MaxRows))
                if (line.Split('=') is [var key, var value] &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) numbers[key] = number;
            return Assert(numbers.Count <= DebugProtocol.MaxRows) ? (numbers, Path.GetFileName(folders[i])) : (null, "");
        }

        return (null, "");
    }

    // Warnings and errors the game logged since the capture began, from the log's tail when it grew past MaxLogBytes
    private static (string[] Lines, int Errors, int Warnings) Log(long from)
    {
        var path = Path.Join(GamePaths.Logs, LogFile);
        if (!Assert(from >= 0) || !File.Exists(path)) return ([], 0, 0);
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(file);
        var start = Math.Max(from > length ? 0 : from, length - MaxLogBytes);
        var bytes = new byte[length - start];
        var read = RandomAccess.Read(file, bytes, start);
        var (lines, errors, warnings) = (new List<string>(), 0, 0);
        foreach (var line in Encoding.UTF8.GetString(bytes, 0, read).Split('\n').Bounded(MaxLogBytes))
        {
            var error = line.Contains("[Error]", StringComparison.Ordinal) || line.Contains("[Fatal]", StringComparison.Ordinal);
            var warning = line.Contains("[Warning]", StringComparison.Ordinal);
            if (!error && !warning) continue;
            (errors, warnings) = (errors + (error ? 1 : 0), warnings + (warning ? 1 : 0));
            if (lines.Count >= MaxLogLines) lines.RemoveAt(0);
            lines.Add(line.TrimEnd('\r'));
        }

        return Assert(lines.Count <= MaxLogLines) && Assert(read >= 0) ? ([.. lines], errors, warnings) : ([], 0, 0);
    }

    private static string FramesCsv(DebugFrame[] frames)
    {
        var csv = new StringBuilder("at_s,dt_ms,gc_ms,jit_ms,runqueue_ms,outside_ms,alloc_kb,main_alloc_kb,gens,steady\n");
        var c = CultureInfo.InvariantCulture;
        foreach (var f in frames.Bounded(DebugFrames.Capacity))
            _ = csv.Append(f.At.ToString("F3", c)).Append(',').Append(f.DtMs.ToString("F3", c)).Append(',')
                .Append(f.GcMs.ToString("F3", c)).Append(',').Append(f.JitMs.ToString("F3", c)).Append(',')
                .Append(f.RunQueueMs.ToString("F3", c)).Append(',').Append(f.OutsideMs.ToString("F3", c)).Append(',')
                .Append(f.AllocKb).Append(',').Append(f.MainAllocKb).Append(',').Append(f.Gens).Append(',')
                .Append(f.Steady ? '1' : '0').Append('\n');
        return Assert(csv.Length > 0) && NotNull(frames) ? csv.ToString() : "";
    }
}

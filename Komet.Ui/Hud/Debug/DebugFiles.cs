using System.Globalization;
using System.Text;
using Komet.Diagnostics;
using Komet.Core;

namespace Komet.Hud;

// Pool-thread half of a protocol and its files under Logs/komet-debug/<stamp>/ (protocol, frames CSV, patch registry,
// numbers for the next compare); the newest 30 folders are kept.
internal sealed record DebugCompare(string Stamp, (string Key, double Before, double Now)[] Rows, string Unlike = "")
{
    public static readonly DebugCompare None = new("", []);
}

internal static class DebugFiles
{
    public const string Folder = "komet-debug", Protocol = "komet-debug.txt", Csv = "frames.csv", Patches = "harmony.txt",
        Summary = "summary.txt";

    private const string LogFile = "client-main.log";
    private const int MaxLogBytes = 1 << 20, MaxFolders = 4096, KeptFolders = 30;
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public static long LogLength()
    {
        try
        {
            var info = new FileInfo(Path.Join(GamePaths.Logs, LogFile));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Empty text and the reason when writing failed
    public static (string Text, string Folder, DebugCompare Compare, System.Func<string, string>? Rebuild) Write(ICoreClientAPI capi,
        DebugInput input, long logFrom)
    {
        if (!NotNull(capi) || !NotNull(input)) return ("", "", DebugCompare.None, null);
        try
        {
            var root = Path.Join(GamePaths.Logs, Folder);
            var folder = Path.Join(root, input.Started.ToString(StampFormat, CultureInfo.InvariantCulture));
            var stamps = Stamps(root);
            var (previous, stamp) = Previous(stamps);
            Prune(stamps, capi.Logger);
            var (log, errors, warnings) = Log(logFrom);
            var extra = new DebugExtra(HarmonyAudit.Walk(capi.ModLoader), log, errors, warnings, previous, stamp);
            var summary = DebugFrames.Summarize(input.Frames);
            string[] files = [Protocol, Csv, Patches, Summary];
            var text = DebugProtocol.Build(input, extra, summary, files);
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Join(folder, Protocol), text);
            File.WriteAllText(Path.Join(folder, Csv), FramesCsv(input.Frames));
            File.WriteAllText(Path.Join(folder, Patches), DebugProtocol.Anonymize(extra.Audit.Text()));
            var now = DebugProtocol.Numbers(summary);
            File.WriteAllLines(Path.Join(folder, Summary), now.Concat(DebugProtocol.Scene(input, summary))
                .Select(static n => n.Key + "=" + n.Value.ToString("R", CultureInfo.InvariantCulture)));
            var compare = previous is null ? DebugCompare.None : new DebugCompare(stamp, [.. now.Where(n => previous.ContainsKey(n.Key))
                .Select(n => (n.Key, previous[n.Key], n.Value))], DebugProtocol.Unlike(KometText.Translate, previous, input, summary));
            string Rebuild(string code)
            {
                input.Code = code;
                return DebugProtocol.Build(input, extra, summary, files);
            }

            return (text, DebugProtocol.Anonymize(folder), compare, Rebuild);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            capi.Logger.Warning("Komet: debug protocol failed: {0}", e);
            return ("", e.Message, DebugCompare.None, null);
        }
    }

    // The protocol folders, oldest first: named by their stamp, with the numbers of a finished protocol
    private static string[] Stamps(string root)
    {
        if (!Directory.Exists(root)) return [];
        var folders = Directory.GetDirectories(root).Where(static f => DateTime.TryParseExact(Path.GetFileName(f), StampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && File.Exists(Path.Join(f, Summary))).ToArray();
        Array.Sort(folders, StringComparer.Ordinal);
        return folders;
    }

    private static (Dictionary<string, double>? Numbers, string Stamp) Previous(string[] stamps)
    {
        if (stamps.Length == 0) return (null, "");
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Join(stamps[^1], Summary)).Bounded(DebugProtocol.MaxRows))
            if (line.Split('=') is [var key, var value] &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) numbers[key] = number;
        return (numbers, Path.GetFileName(stamps[^1]));
    }

    // Keeps the newest KeptFolders protocols, the one being written included; a folder in use stays for the next time
    private static void Prune(string[] stamps, ILogger logger)
    {
        if (!NotNull(logger)) return;
        for (var i = 0; i < Math.Min(stamps.Length - (KeptFolders - 1), MaxFolders); i++)
        {
            try
            {
                Directory.Delete(stamps[i], true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.Warning("Komet: old debug protocol {0} not deleted ({1})", DebugProtocol.Anonymize(stamps[i]), e.Message);
            }
        }
    }

    private static (string[] Lines, int Errors, int Warnings) Log(long from)
    {
        var path = Path.Join(GamePaths.Logs, LogFile);
        if (!File.Exists(path)) return ([], 0, 0);
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(file);
        var start = Math.Max(from > length ? 0 : from, length - MaxLogBytes);
        var bytes = new byte[length - start];
        var read = RandomAccess.Read(file, bytes, start);
        var (lines, errors, warnings) = (new Queue<string>(DebugProtocol.ShownLog), 0, 0); // the last lines, as many as are shown
        foreach (var line in Encoding.UTF8.GetString(bytes, 0, read).Split('\n').Bounded(MaxLogBytes))
        {
            var error = line.Contains("[Error]", StringComparison.Ordinal) || line.Contains("[Fatal]", StringComparison.Ordinal);
            var warning = line.Contains("[Warning]", StringComparison.Ordinal);
            if (!error && !warning) continue;
            (errors, warnings) = (errors + (error ? 1 : 0), warnings + (warning ? 1 : 0));
            if (lines.Count >= DebugProtocol.ShownLog) _ = lines.Dequeue();
            lines.Enqueue(line.TrimEnd('\r'));
        }

        return ([.. lines], errors, warnings);
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
        return csv.ToString();
    }
}

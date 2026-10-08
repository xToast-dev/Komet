using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Komet.Diagnostics;

internal enum LogLevel { Info, Debug, Warning, Error }

// One entry of the log as the window shows it: its time of day, the mod that wrote it ("komet" from "[komet]"), its level, the text
// with the lines that followed it (a stack trace) joined on
internal readonly record struct LogEntry(string Time, string Source, LogLevel Level, string Text);

// Reads the tail off the main thread
internal sealed class LogStats(string fileName)
{
    public const int KeepEntries = 200;
    private const int MaxEntryChars = 1200;
    private const int TailBytes = 64 * 1024;
    private readonly string _path = Assert(fileName.Length > 0) ? Path.Join(GamePaths.Logs, fileName) : "";

    // Replaced wholesale, so readers never see a half-written snapshot
    private LogEntry[] _entries = [];
    private byte[]? _tail; // TailBytes once shown; SampleSlow reads one log after the other, never two at once
    private (long Length, DateTime Written) _parsed = (-1, default);

    // back entries before the newest; null past the oldest kept
    public LogEntry? Entry(int back)
    {
        var entries = _entries;
        if (!NotNull(entries) || !Assert(entries.Length <= KeepEntries)) return null;
        return Index(back, KeepEntries) && back < entries.Length ? entries[entries.Length - 1 - back] : null;
    }

    // Once a second while shown: a file that kept its length and time since the last parse has the same tail, which is kept
    public void Sample()
    {
        if (!Assert(_path.Length > 0)) return;
        try
        {
            using var file = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var stamp = (Length: RandomAccess.GetLength(file), Written: File.GetLastWriteTimeUtc(file));
            if (stamp == _parsed) return;
            var (size, tail) = ((int)Math.Min(TailBytes, stamp.Length), _tail ??= new byte[TailBytes]);
            if (!Read(file, tail.AsSpan(0, size), stamp.Length - size)) return;
            var raw = Encoding.UTF8.GetString(tail, 0, size)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var entries = new List<LogEntry>();
            // from 1: the first line is likely cut in the middle
            for (var i = 1; i < Math.Min(raw.Length, TailBytes); i++)
                Parse(raw[i], entries);
            (_entries, _parsed) = ([.. entries.TakeLast(KeepEntries)], stamp);
        }
        catch (IOException)
        {
            // rotated or locked, the previous snapshot stays
        }
    }

    // The whole span from offset on; false when the file ended first (it shrank meanwhile)
    private static bool Read(SafeFileHandle file, Span<byte> into, long offset)
    {
        var got = 0;
        for (var i = 0; i < TailBytes && got < into.Length; i++)
        {
            var n = RandomAccess.Read(file, into[got..], offset + got);
            if (n <= 0) return false;
            got += n;
        }

        return Assert(got == into.Length);
    }

    // "5.10.2026 20:20:02 [Notification] [komet] Komet: text" -> 20:20:02, komet, Info, "text"; a line without the date and level
    // belongs to the entry before it
    private static void Parse(string line, List<LogEntry> into)
    {
        if (!Assert(line.Length > 0) || !Assert(into.Count < TailBytes)) return;
        var start = line.IndexOf('[', StringComparison.Ordinal);
        var end = line.IndexOf("] ", StringComparison.Ordinal);
        if (start <= 0 || end < start || !char.IsDigit(line[0]))
        {
            if (into.Count > 0 && into[^1].Text.Length < MaxEntryChars) into[^1] = into[^1] with { Text = Clip(into[^1].Text + "  " + line) };
            return;
        }

        var head = line.AsSpan(0, start).Trim();
        var time = head.LastIndexOf(' ') is var space and >= 0 ? head[(space + 1)..].ToString() : head.ToString();
        var level = line.AsSpan(start + 1, end - start - 1) switch
        {
            "Warning" => LogLevel.Warning,
            "Error" or "Fatal" => LogLevel.Error,
            "Debug" or "VerboseDebug" => LogLevel.Debug,
            _ => LogLevel.Info
        };
        var (rest, source) = (line[(end + 2)..], "");
        if (rest.StartsWith('[') && rest.IndexOf("] ", StringComparison.Ordinal) is var close and > 1)
            (source, rest) = (rest[1..close], rest[(close + 2)..]);
        if (source == "komet" && rest.StartsWith("Komet: ", StringComparison.Ordinal)) rest = rest[7..];
        into.Add(new LogEntry(time, source, level, Clip(rest)));
    }

    private static string Clip(string text)
    {
        if (!NotNull(text) || text.Length <= MaxEntryChars || !Assert(MaxEntryChars > 0)) return text;
        return Assert(text.Length > MaxEntryChars) ? text[..MaxEntryChars] + "…" : text;
    }
}

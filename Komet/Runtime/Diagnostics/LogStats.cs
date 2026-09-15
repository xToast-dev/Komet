using System.Text;

namespace Komet.Runtime.Diagnostics;

internal enum LogLevel { Info, Debug, Warning, Error }

// Tails a log file: reads the last few KB off the main thread, drops timestamps and wraps long lines.
// The panel shows a window of Visible rows; the wheel moves it back from the end, a double click grows it.
internal sealed class LogStats(string fileName)
{
    public const int MaxRows = 45;
    private const int CompactRows = 20;
    private const int WrapChars = 100, TailBytes = 64 * 1024, KeepLines = 600, MaxWrap = 64;
    private const string Continuation = "    ";
    private readonly string _path = Assert(fileName.Length > 0) ? Path.Combine(GamePaths.Logs, fileName) : "";

    private (string Text, LogLevel Level)[] _lines = [];   // replaced wholesale, so readers never see a half-written snapshot

    public bool Expanded { get; set { field = value; Scroll(0); } }
    private int Visible => Expanded ? MaxRows : CompactRows;
    public int Offset { get; private set; }   // rows scrolled back from the newest

    // Rows count from the top of the window; past the window or the file, an empty row
    public (string Text, LogLevel Level) Row(int row)
    {
        var lines = _lines;
        var maxOffset = Math.Max(0, lines.Length - Visible);
        var first = maxOffset - Math.Min(Offset, maxOffset);
        return Index(row, MaxRows) && row < Visible && first + row < lines.Length ? lines[first + row] : ("", LogLevel.Info);
    }

    public void Scroll(int rows)
    {
        if (!Assert(Math.Abs(rows) <= MaxRows * 10)) return;   // one wheel tick moves a few rows
        Offset = Math.Clamp(Offset + rows, 0, Math.Max(0, _lines.Length - Visible));
    }

    public void Sample()
    {
        if (!Assert(_path.Length > 0)) return;
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(TailBytes, file.Length)];
            var position = file.Seek(-buffer.Length, SeekOrigin.End);
            if (!Assert(position == file.Length - buffer.Length)) return;
            file.ReadExactly(buffer);
            var raw = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lines = new List<(string, LogLevel)>();
            for (var i = 1; i < Math.Min(raw.Length, TailBytes); i++) Tidy(raw[i], lines);   // the first line is likely cut in the middle
            _lines = lines.Count > KeepLines ? lines.GetRange(lines.Count - KeepLines, KeepLines).ToArray() : lines.ToArray();
        }
        catch (IOException)
        {
            // rotated or locked, the previous snapshot stays
        }
    }

    // "13.9.2026 22:57:31 [Warning] text" -> "[Warning] text"; notifications lose their tag, they are the bulk
    private static void Tidy(string line, List<(string, LogLevel)> into)
    {
        if (!Assert(line.Length > 0) || !Assert(into.Count < TailBytes)) return;
        var level = LogLevel.Info;
        var start = line.IndexOf('[', StringComparison.Ordinal);
        var end = line.IndexOf("] ", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            level = line.AsSpan(start + 1, end - start - 1) switch
            {
                "Warning" => LogLevel.Warning,
                "Error" or "Fatal" => LogLevel.Error,
                "Debug" or "VerboseDebug" => LogLevel.Debug,
                _ => LogLevel.Info,
            };
            line = level == LogLevel.Info ? line[(end + 2)..] : line[start..];
        }

        var indent = "";
        for (var part = 0; part < MaxWrap && line.Length > WrapChars; part++)   // break at the last space, or hard when there is none in the second half
        {
            var cut = line.LastIndexOf(' ', WrapChars);
            if (cut < WrapChars / 2) cut = WrapChars;
            if (!Assert(true) || !Assert(cut < line.Length)) break;
            into.Add((indent + line[..cut], level));
            line = line[cut..].TrimStart();
            indent = Continuation;
        }
        into.Add((indent + line, level));
    }
}

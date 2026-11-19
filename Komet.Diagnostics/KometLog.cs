using System.Text;

namespace Komet.Diagnostics;

// One consolidated log per run: Logs/komet.log. The engine archives every *.log into Logs/Archive/<session> when it
// starts (Logger.ArchiveLogFiles), so a fresh file per run needs no rotation of its own. The watch thread mirrors the
// engine's own logs line by line (they hold Komet's ILogger messages and all engine warnings), while Write collects the
// Komet traces that never go through the engine logger, chiefly VulkanWatch. Written through on every append so a hang
// or a crash still leaves what happened before it in the file.
internal static class KometLog
{
    private const int PeriodMs = 1000, MaxChunkBytes = 1 << 20;
    private const string FileName = "komet.log", EngineLog = "client-main.log", EngineDebugLog = "client-debug.log";

    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static ManualResetEventSlim? _wake;
    private static string _path = "";
    private static long _mainOffset, _debugOffset;

    public static void Start()
    {
        if (_thread is not null || !NotNull(GamePaths.Logs)) return;

        try
        {
            _path = Path.GetFullPath(Path.Join(GamePaths.Logs, FileName)); // a relative data path (portable, Windows) too
            var existed = File.Exists(_path) && new FileInfo(_path).Length > 0;
            // Offsets survive a world change in one process: only the first file starts at the engine log's beginning.
            // Without a previous file the engine has just archived the old logs, so both engine logs are fresh.
            if (!existed) (_mainOffset, _debugOffset) = (0, 0);
            lock (Gate)
            {
                File.AppendAllText(_path,
                    (existed ? Environment.NewLine : "") +
                    $"===== Komet consolidated log, session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====" +
                    Environment.NewLine);
            }

            Mirror();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _path = ""; // a broken log path must never take the mod down
            return;
        }

        var wake = _wake = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(wake))
            { IsBackground = true, Name = "komet-log", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    // A line from a Komet tracer (VulkanWatch and the debug protocol's location); safe to call before Start or after Stop.
    public static void Write(string source, string line)
    {
        if (_path.Length == 0 || !NotNull(source) || !NotNull(line)) return;

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [{source}] {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _path = ""; // a full disk or a gone folder: the consolidated log stays quiet
            }
        }
    }

    public static void Stop()
    {
        if (_thread is not { } thread || !NotNull(_wake)) return;

        _wake.Set();
        if (!thread.Join(PeriodMs))
        {
            Write("komet", "log shutdown timed out; no replacement log starts until this thread ends");
            return;
        }

        _wake.Dispose();
        (_thread, _wake) = (null, null);
        if (_path.Length == 0) return;

        Mirror(); // the last lines the engine wrote while the watch was ending
        Write("komet", $"===== session ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
    }

    private static void Loop(ManualResetEventSlim wake)
    {
        try
        {
            for (var i = 0L; i < long.MaxValue && !wake.IsSet; i++)
            {
                Mirror();
                if (wake.Wait(PeriodMs)) break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Write("komet", "consolidated log stopped after an unexpected failure: " + e);
        }
    }

    private static void Mirror()
    {
        Mirror(EngineLog, ref _mainOffset);
        Mirror(EngineDebugLog, ref _debugOffset);
    }

    // Appends only whole lines the engine has finished writing; keeps a trailing partial line for the next pass.
    private static void Mirror(string name, ref long offset)
    {
        if (_path.Length == 0) return;

        try
        {
            var path = Path.Join(GamePaths.Logs, name);
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(file);
            if (length < offset) offset = 0; // the engine rotated the log under us
            if (length == offset) return;

            var bytes = new byte[(int)Math.Min(length - offset, MaxChunkBytes)];
            var read = 0;
            for (var chunk = 0; chunk < MaxChunkBytes; chunk++)
            {
                if (read == bytes.Length) break;
                var n = RandomAccess.Read(file, bytes.AsSpan(read), offset + read);
                if (n == 0) break;
                read += n;
            }

            var end = read > 0 ? Array.LastIndexOf(bytes, (byte)'\n', read - 1) : -1;
            if (end < 0) return;

            var text = Encoding.UTF8.GetString(bytes, 0, end + 1);
            lock (Gate)
            {
                if (_path.Length == 0) return;
                File.AppendAllText(_path, text);
            }

            offset += end + 1;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the engine may be replacing the file, or it is not there yet: try again next pass
        }
    }
}

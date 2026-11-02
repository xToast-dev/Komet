namespace Komet.Core;

// How Vulkan-only went on each GPU of this computer, kept across starts in ModConfig/komet-gpu.txt (one "GPU<tab>outcome"
// line each): the renderer suggestion (VulkanRenderer.Suggested) reads it as HardwareProfile.Facts.Vulkan. A world's first
// Vulkan-only frame writes Pending, leaving that world cleanly Ran; a game that ended in between stays Pending (not
// proven). Failed (no device, a failure handing the world to OpenGL) is kept for good: no later run lifts it.
internal static class VulkanRecord
{
    public enum Run
    {
        Unknown,
        Pending,
        Ran,
        Failed
    }

    private const string FileName = "komet-gpu.txt";
    private const int MaxLines = 64, MaxKey = 256;

    private static readonly Lock Gate = new();

    // This start's GPU, set by HardwareProfile.Detect from OpenGL's vendor and renderer; empty: nothing kept
    public static string Gpu { get; private set; } = "";

    // ModConfig's file; tests point it elsewhere
    internal static string? Path { get; set; }

    // OpenGL's vendor and renderer as one key, without the driver's parenthesised details (Mesa adds its LLVM and kernel)
    internal static string Key(string? vendor, string? renderer)
    {
        var name = renderer ?? "";
        var details = name.IndexOf('(', StringComparison.Ordinal);
        if (details > 0) name = name[..details];
        var key = $"{vendor?.Trim()} {name.Trim()}".Trim().Replace('\t', ' ').Replace('\n', ' ');
        return key.Length > MaxKey ? key[..MaxKey] : key;
    }

    public static Run Read(string? vendor, string? renderer)
    {
        Gpu = Key(vendor, renderer);
        return Gpu.Length > 0 && Lines().TryGetValue(Gpu, out var run) ? run : Run.Unknown;
    }

    // A world's first Vulkan-only frame; a failure stays
    public static void Began() => Mark(Run.Pending);

    public static void Ended() => Mark(Run.Ran);

    public static void Failed() => Mark(Run.Failed);

    private static void Mark(Run run)
    {
        if (Gpu.Length == 0 || File() is not { } file) return;
        lock (Gate)
        {
            var lines = Lines();
            if (lines.TryGetValue(Gpu, out var was) && (was == run || was == Run.Failed)) return;
            lines[Gpu] = run;
            try
            {
                System.IO.File.WriteAllLines(file, lines.Take(MaxLines).Select(static l => $"{l.Key}\t{l.Value}"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // unkept: the suggestion stays as it was
            }
        }
    }

    private static Dictionary<string, Run> Lines()
    {
        var lines = new Dictionary<string, Run>(StringComparer.Ordinal);
        try
        {
            if (File() is not { } file || !System.IO.File.Exists(file)) return lines;
            foreach (var line in System.IO.File.ReadLines(file).Bounded(MaxLines))
                if (line.Split('\t') is [var key, var value] && Enum.TryParse<Run>(value, out var run))
                    lines[key] = run;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lines.Clear();
        }

        return lines;
    }

    private static string? File() =>
        Path ?? (GamePaths.ModConfig is { Length: > 0 } config ? System.IO.Path.Join(config, FileName) : null);
}

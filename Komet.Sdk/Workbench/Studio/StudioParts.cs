using System.Text.Json;
using Komet.Hud;

namespace Komet.Sdk;

// The draft's token and icon colours (Pragmatic): folders gold, fragment files violet, other stages blue, JSON gold
internal static class StudioColors
{
    public static readonly Rgba Gold = new(232 / 255.0, 195 / 255.0, 138 / 255.0, 1),
        Fn = new(201 / 255.0, 184 / 255.0, 1, 1),
        Type = new(159 / 255.0, 216 / 255.0, 1, 1),
        Keyword = new(141 / 255.0, 187 / 255.0, 1, 1),
        Pre = new(232 / 255.0, 160 / 255.0, 200 / 255.0, 1),
        Str = new(168 / 255.0, 224 / 255.0, 184 / 255.0, 1),
        Pending = new(242 / 255.0, 201 / 255.0, 122 / 255.0, 1);

    // The frame bar's stage colours
    public static readonly Rgba[] Stages =
    [
        new(91 / 255.0, 107 / 255.0, 134 / 255.0, 1), new(79 / 255.0, 135 / 255.0, 214 / 255.0, 1),
        new(141 / 255.0, 187 / 255.0, 1, 1), new(111 / 255.0, 195 / 255.0, 201 / 255.0, 1),
        new(201 / 255.0, 184 / 255.0, 1, 1), new(232 / 255.0, 195 / 255.0, 138 / 255.0, 1)
    ];

    public static Rgba Opaque(Rgba color) => color with { A = 1 };
}

// The studio's click map: buttons (an action) and regions (mouse down, drag, wheel), the last added on top; an
// overlay's ground blocks what lies under it
internal sealed class StudioHits
{
    private const int Max = 1024;
    private readonly List<(double X, double Y, double W, double H, Action? Click, IIdeRegion? Region)> _list = [];

    public void Clear() => _list.Clear();

    public void Add(double x, double y, double w, double h, Action click)
    {
        if (_list.Count < Max && w > 0 && h > 0) _list.Add((x, y, w, h, click, null));
    }

    public void Region(double x, double y, double w, double h, IIdeRegion region)
    {
        if (_list.Count < Max && w > 0 && h > 0) _list.Add((x, y, w, h, null, region));
    }

    // Swallows clicks over a rectangle (a popup's ground)
    public void Block(double x, double y, double w, double h)
    {
        if (_list.Count < Max && w > 0 && h > 0) _list.Add((x, y, w, h, null, null));
    }

    public bool At(double x, double y, out Action? click, out IIdeRegion? region)
    {
        (click, region) = (null, null);
        for (var back = 0; back < Math.Min(_list.Count, Max); back++)
        {
            var hit = _list[_list.Count - 1 - back];
            if (x >= hit.X && x < hit.X + hit.W && y >= hit.Y && y < hit.Y + hit.H)
            {
                (click, region) = (hit.Click, hit.Region);
                return true;
            }
        }

        return false;
    }

    // The region the wheel turns: the topmost region under the point, through the buttons lying on it (a row's
    // buttons cover its scroller), stopped by a popup's ground
    public IIdeRegion? WheelAt(double x, double y)
    {
        for (var back = 0; back < Math.Min(_list.Count, Max); back++)
        {
            var hit = _list[_list.Count - 1 - back];
            if (x < hit.X || x >= hit.X + hit.W || y < hit.Y || y >= hit.Y + hit.H || hit.Click is not null) continue;

            return hit.Region;
        }

        return null;
    }
}

// The wheel's turns as whole lines: what a turn leaves under one line is carried to the next (a touchpad's and a
// high-resolution wheel's small steps add up instead of being dropped)
internal struct WheelSteps
{
    private double _rest;

    public int Take(double notches, int perNotch, int max)
    {
        if (!double.IsFinite(notches)) return 0;

        _rest = Math.Clamp(_rest + notches * perNotch, -max, max);
        var lines = (int)Math.Truncate(_rest + Math.Sign(_rest) * 1e-9); // ten tenths are one, not 0.9999
        _rest -= lines;
        return lines;
    }
}

// Says when something under a folder changed (the projects: saved from another editor, a file added or gone), so the
// tree and the problem list are read again only then instead of every few seconds. Without a working watcher
// (no folder yet, no inotify handle left) Working is false and the studio reads on its timer as before.
internal sealed class StudioWatch : IDisposable
{
    private FileSystemWatcher? _watcher;
    private int _changed = 1;

    public bool Working => _watcher is not null;

    public void Start(string folder)
    {
        if (_watcher is not null) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                               NotifyFilters.Size
            };
            watcher.Changed += Raised;
            watcher.Created += Raised;
            watcher.Deleted += Raised;
            watcher.Renamed += Raised;
            watcher.Error += Failed;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            _watcher = null;
        }

        _ = Interlocked.Exchange(ref _changed, 1);
    }

    // True once after a change (a watcher thread raises it)
    public bool Take() => Interlocked.Exchange(ref _changed, 0) != 0;

    private void Raised(object sender, FileSystemEventArgs e) => _ = Interlocked.Exchange(ref _changed, 1);

    private void Failed(object sender, ErrorEventArgs e)
    {
        _ = Interlocked.Exchange(ref _changed, 1); // events were lost: read again
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

// What the studio keeps between sessions (ModConfig/komet/studio.json): Auto, the open folders, the right column's
// sections and width, the bottom panel's height and whether it shows, the active project, the open tabs
internal sealed class StudioPrefs
{
    public bool Auto { get; set; } = true;
    public List<string> Open { get; set; } = [];
    public List<string> Sections { get; init; } = ["view", "set", "shader", "frame"];
    public double Right { get; set; } = 330;
    public double Panel { get; set; } = 200;
    public bool PanelShown { get; set; } = true;
    public LeftView Left { get; set; } // the left column's view, picked on the activity strip
    public bool LeftShown { get; set; } = true;
    public string Project { get; set; } = "";
    public List<string> Tabs { get; set; } = [];
    public List<string> Off { get; set; } = [];
    public bool AllFiles { get; set; } // the explorer's "All files" view; false: "Shaders"

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string File => Path.Join(StudioLink.KometFolder, "studio.json");

    public static StudioPrefs Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<StudioPrefs>(System.IO.File.ReadAllText(File)) ?? new StudioPrefs()
                : new StudioPrefs();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StudioPrefs();
        }
    }

    public void Save()
    {
        (Open, Tabs, Off) = ([.. Open.Take(256)], [.. Tabs.Take(8)], [.. Off.Take(64)]);
        _ = StudioLink.Write(File, JsonSerializer.Serialize(this, Indented));
    }
}

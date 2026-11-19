using Komet.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Sdk;

// Modifier keys held at a mouse event
internal readonly record struct IdeKeys(bool Ctrl, bool Shift);

// What the editor needs from the studio around it: the shown file, its problems, the status and the cross-part actions
internal interface IIdeHost
{
    ICoreClientAPI Api { get; }
    IdeStatus Status { get; }

    // The file the editor shows (the original in the Original view)
    ShaderDocument? Active { get; }
    ShaderDiagnostics Diagnostics { get; }

    // Where includes resolve besides the file's own folder
    string Root { get; }

    Rgba InactiveTint => new(0, 0, 0, 0);

    // Opens a file in the editor, its caret on a line (-1: where it was)
    void Open(string path, int line = -1);
    void Run(string command);

    // A word under the mouse explained (a uniform's type, value and origin); null: nothing to say
    string? Describe(string word) => null;

    // A line in an #if branch the engine's defines drop (drawn greyed)
    bool Inactive(ShaderDocument document, int line) => false;

    // Ctrl+click on a uniform's declaration: the engine code that sets it; false when there is none to show
    bool JumpToEngine(string uniform) => false;
}

// A screen region's mouse input in IDE content coordinates (the canvas's minus the view's top)
internal interface IIdeRegion
{
    // True: taken; drags and the release follow to this region
    bool Down(double x, double y, EnumMouseButton button, IdeKeys keys) => false;

    void Drag(double x, double y)
    {
    }

    void Up()
    {
    }

    void Hover(double x, double y)
    {
    }

    bool Wheel(double x, int lines) => false;

    // Right click: the region's entries for the context menu; true: the active file's entries follow
    bool Context(double x, double y, ContextMenu menu) => false;
}

// Clickable rows a panel drew this frame: y spans to an index, so a list takes no closure per row
internal sealed class IdeRows
{
    private const int Max = 256;
    private readonly List<(double Y, double H, int Index)> _rows = [];
    private double _x, _w;

    public void Clear(double x, double w)
    {
        _rows.Clear();
        (_x, _w) = (x, w);
    }

    public void Add(double y, double h, int index)
    {
        if (_rows.Count < Max) _rows.Add((y, h, index));
    }

    public int At(double x, double y)
    {
        if (x < _x || x >= _x + _w) return -1;

        for (var i = 0; i < Math.Min(_rows.Count, Max); i++)
        {
            if (y >= _rows[i].Y && y < _rows[i].Y + _rows[i].H) return _rows[i].Index;
        }

        return -1;
    }
}

// The status line: the last message, whether it is an error, and whether the IDE should redraw
internal sealed class IdeStatus
{
    private const int MaxText = 300;

    public string Text { get; private set; } = "";
    private bool Error { get; set; }
    private bool Redraw { get; set; }

    public void Set(string text, bool error = false)
    {
        var next = text.Length <= MaxText ? text : text[..MaxText];
        Redraw |= Text != next || Error != error;
        (Text, Error) = (next, error);
    }

    public bool Take()
    {
        var redraw = Redraw;
        Redraw = false;
        return redraw;
    }
}

// The IDE's texts (lang keys "settings-workbench-*") and their spaced headings
internal static class IdeText
{
    private const int MaxHeadings = 64;
    private static readonly Dictionary<string, string> Upper = new(StringComparer.Ordinal);

    public static string L(string key) => T("workbench-" + key);

    // A text with its placeholders filled ({0}, {1}, ...)
    public static string L(string key, params object[] args) => T("workbench-" + key, args);

    // A heading's capitals, kept so a redraw takes no new string
    public static string Caps(string text)
    {
        if (Upper.TryGetValue(text, out var caps)) return caps;

        if (Upper.Count >= MaxHeadings) Upper.Clear();

        caps = text.ToUpperInvariant();
        Upper[text] = caps;
        return caps;
    }
}

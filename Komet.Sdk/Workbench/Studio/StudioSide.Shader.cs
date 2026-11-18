using Komet.Hud;
using Komet.Options;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// THIS SHADER: the open file's uniforms (type, live value from its program, origin, matrices fold open), the engine's
// defines for its stage with their values, its inputs and outputs with their locations. A click on a name jumps to its
// declaration. Live values are read at most every quarter second and only
// while the section is open.
internal sealed partial class StudioSide
{
    private const long LiveEveryMs = 250;

    private readonly HashSet<string> _unfolded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]?> _live = new(StringComparer.Ordinal);
    private List<ShaderItem> _items = [];
    private List<(string Name, string Value)> _defines = [];
    private string? _itemsOf;
    private long _liveAt;
    private double _liveMs; // the last refresh's cost (glGetUniform of every uniform), shown in the head

    // The open file's declarations again when its text changed, the live values when due
    private void ShaderRefresh(ShaderDocument document)
    {
        var key = document.Path + "\n" + document.Text.Length + ":" + document.Text.GetHashCode(StringComparison.Ordinal);
        if (key != _itemsOf)
        {
            _itemsOf = key;
            _items = ShaderInfo.Declared(document.Text, document.Path.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase));
            _liveAt = 0;
        }

        var now = Environment.TickCount64;
        if (now - _liveAt < LiveEveryMs) return;

        _liveAt = now;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _defines = ShaderInfo.Defines(document.Path);
        _live.Clear();
        if (ShaderInfo.ProgramOf(document.Path) is { } program)
        {
            foreach (var item in _items.Bounded(ShaderInfo.MaxItems))
            {
                if (item.Kind == ShaderItemKind.Uniform)
                {
                    _live[item.Name] = ShaderInfo.Live(program, item.Name, item.Type);
                }
            }
        }

        _liveMs = clock.Elapsed.TotalMilliseconds;
    }

    private double ThisShader(HudUi ui, MenuControls menu, double y)
    {
        _ = menu;
        if (host.Active is not { } document || !ShaderPaths.IsStage(document.Path))
        {
            return Note(ui, y, L("this-shader-none"));
        }

        ShaderRefresh(document);
        y = SubHead(ui, y, L("this-uniforms") + "  ·  " + L("buffer-cost", BufferView.Format((float)_liveMs)) + " / 250 ms");
        foreach (var item in _items.Bounded(ShaderInfo.MaxItems))
        {
            y = item.Kind == ShaderItemKind.Uniform ? UniformRow(ui, y, item, document) : y;
        }

        y = SubHead(ui, y, L("this-defines"));
        foreach (var (name, value) in _defines.Bounded(ShaderInfo.MaxItems))
        {
            y = Line(ui, y, name, value, L("origin-game"), () => JumpTo(document, "#define " + name));
        }

        y = _defines.Count == 0 ? Note(ui, y, L("this-defines-unknown")) : y;
        y = SubHead(ui, y, L("this-io"));
        foreach (var item in _items.Bounded(ShaderInfo.MaxItems))
        {
            if (item.Kind is ShaderItemKind.Input or ShaderItemKind.Output)
            {
                var offset = item.Offset;
                y = Line(ui, y, (item.Kind == ShaderItemKind.Input ? "in  " : "out ") + item.Name, item.Type, item.Detail,
                    () => Jump(document, offset));
            }
        }

        return y + ui.Px(6);
    }

    private double UniformRow(HudUi ui, double y, ShaderItem item, ShaderDocument document)
    {
        var value = _live.GetValueOrDefault(item.Name);
        var origin = host.Origin(document.Path, item.Name).Origin;
        var count = _unfolded.Contains(item.Name) ? 16 : 4;
        var shown = value is null ? L("this-not-active") : ShaderInfo.Text(value, count);
        var offset = item.Offset;
        var x = _x + ui.Px(14);
        ui.Text(x, y, ui.F.BodyRow, ui.F.Mono, ui.Fit(ui.F.Mono, item.Name, _w * 0.5), HudUi.Soft);
        Click(_x, y, _w * 0.55, ui.F.BodyRow, () => Jump(document, offset));
        ui.TextRight(_x + _w - ui.Px(64), y, ui.F.BodyRow, ui.F.Small, item.Type + " · " + origin, HudUi.Faint);
        y += ui.F.BodyRow;
        ui.Text(x, y, ui.F.BodyRow, ui.F.Mono, ui.Fit(ui.F.Mono, shown, _w - ui.Px(28)), HudUi.Faint);
        var name = item.Name;
        Click(_x, y, _w, ui.F.BodyRow, () => _ = _unfolded.Remove(name) || _unfolded.Add(name)); // matrices fold open
        return y + ui.F.BodyRow;
    }

    private double SubHead(HudUi ui, double y, string text)
    {
        _ = ui.Spaced(_x + ui.Px(14), y + ui.Px(4), ui.F.SmallRow, ui.F.Small, Caps(text), ui.Px(2), HudUi.Faint);
        return y + ui.F.SmallRow + ui.Px(6);
    }

    private double Note(HudUi ui, double y, string text)
    {
        ui.Text(_x + ui.Px(14), y, ui.F.BodyRow, ui.F.Small, ui.Fit(ui.F.Small, text, _w - ui.Px(28)), HudUi.Faint);
        return y + ui.F.BodyRow + ui.Px(4);
    }

    private double Line(HudUi ui, double y, string name, string value, string note, Action click)
    {
        var h = ui.F.BodyRow;
        if (host.Hot(_x, y, _w, h) && y >= _top)
        {
            ui.Fill(_x + ui.Px(6), y, _w - ui.Px(12), h, HudUi.Track with { A = 0.06 });
        }

        ui.Text(_x + ui.Px(14), y, h, ui.F.Mono, ui.Fit(ui.F.Mono, name, _w * 0.5), HudUi.Soft);
        ui.TextRight(_x + _w - ui.Px(14), y, h, ui.F.Small, ui.Fit(ui.F.Small, (value + "  " + note).Trim(), _w * 0.45), HudUi.Faint);
        Click(_x, y, _w, h, click);
        return y + h;
    }

    private void Jump(ShaderDocument document, int offset)
    {
        if (offset < 0 || offset > document.Text.Length) return;

        document.Move(offset, false);
        document.FirstLine = Math.Max(0, document.LineAt(offset) - 3);
        host.Focus = StudioFocus.Editor;
    }

    private void JumpTo(ShaderDocument document, string text) =>
        Jump(document, document.Text.IndexOf(text, StringComparison.Ordinal));
}

using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The studio's input: the mouse goes to the topmost button or region under it (a press captures a region until the
// release), right click asks the region for its context menu, the wheel goes to the region under the mouse. Keys go to
// the open popup, then the shortcuts (Strg P, S, R, J, B, W, F, H, Umschalt F, Umschalt E, Z), Escape steps back, then the focused
// field, the tree or the editor.
internal sealed partial class ShaderIde
{
    private IIdeRegion? _capture, _wheelOn;
    private WheelSteps _wheel;
    private double _mouseX, _mouseY;
    private Action _openPalette = null!, _buildNow = null!, _toggleAuto = null!;

    private IdeKeys Modifiers => new(
        _capi.Input.KeyboardKeyState[(int)GlKeys.LControl] || _capi.Input.KeyboardKeyState[(int)GlKeys.RControl],
        _capi.Input.KeyboardKeyState[(int)GlKeys.LShift] || _capi.Input.KeyboardKeyState[(int)GlKeys.RShift]);

    public double MouseX => _mouseX;
    public double MouseY => _mouseY;

    private void InitInput() => (_openPalette, _buildNow, _toggleAuto) = (Palette.Show, BuildNow, ToggleAuto);

    public bool MouseDown(double x, double y, EnumMouseButton button)
    {
        (_mouseX, _mouseY, _capture) = (x, y, null);
        if (Model3D.Transforming != TransformMode.None)
        {
            if (button == EnumMouseButton.Right) Model3D.Cancel();
            else Model3D.Confirm();

            Redraw();
            return true;
        }

        Tree.EndRename(true);
        if (Context.IsOpen)
        {
            _ = Context.Down(x, y, button, Modifiers);
            return true;
        }

        _ = Hits.At(x, y, out var click, out var region);
        if (button == EnumMouseButton.Right)
        {
            Context.Begin(x, y);
            _ = region?.Context(x, y, Context);
            _ = Context.End();
            return true;
        }

        if (click is not null && button == EnumMouseButton.Left)
        {
            click();
        }
        else if (region?.Down(x, y, button, Modifiers) == true)
        {
            _capture = region;
            if (ReferenceEquals(region, Editor)) Focus = StudioFocus.Editor;
        }

        Redraw();
        return true;
    }

    public bool MouseMove(double x, double y)
    {
        (_mouseX, _mouseY) = (x, y);
        if (Model3D.Transforming != TransformMode.None)
        {
            Model3D.Move(x, y);
            Redraw();
            return true;
        }

        if (_capture is not null)
        {
            _capture.Drag(x, y);
            return true;
        }

        _ = Hits.At(x, y, out _, out var region);
        if (!ReferenceEquals(region, Editor)) Editor.Leave();

        if (Context.IsOpen) Context.Hover(x, y);

        region?.Hover(x, y);
        return true;
    }

    public void MouseUp()
    {
        _capture?.Up();
        _capture = null;
        Peeking = false;
        Redraw();
    }

    public bool Wheel(double notches, double x)
    {
        if (Context.IsOpen)
        {
            Context.Close();
            return true;
        }

        var region = Hits.WheelAt(x, _mouseY);
        if (!ReferenceEquals(region, _wheelOn))
        {
            (_wheelOn, _wheel) = (region, default); // another scroller: the last one's rest is not its
        }

        var lines = _wheel.Take(notches, 3, ShaderPaths.MaxSource);
        if (lines == 0 || region is null) return true;

        _ = region.Wheel(x, lines);
        Redraw();
        return true;
    }

    public bool Key(KeyEvent args)
    {
        var (key, ctrl, shift) = ((GlKeys)args.KeyCode, args.CtrlPressed, args.ShiftPressed);
        Redraw();
        if (!ctrl && ModelKey(key))
        {
            _swallowChar = true; // the key's character is no text
            return true;
        }

        if (Buffers.Large)
        {
            Buffers.Large = key != GlKeys.Escape; // the enlarged preview takes every key; Esc closes it
            return true;
        }

        if (Modal.Open) return Modal.Key(key, ctrl);

        if (Palette.Open) return Palette.Key(key, ctrl);

        if (Context.IsOpen) return Context.Key(key);

        if (ctrl && Shortcut(key, shift)) return true;

        if (key == GlKeys.Escape) return Escape();

        return Focused(args, key, ctrl, shift);
    }

    private bool Focused(KeyEvent args, GlKeys key, bool ctrl, bool shift)
    {
        switch (Focus)
        {
            case StudioFocus.Filter or StudioFocus.Rename or StudioFocus.Tree: return Tree.Key(key, ctrl);
            case StudioFocus.Find or StudioFocus.Replace: return Find.Key(key, ctrl, shift);
            case StudioFocus.RenameSymbol: return Rename.Key(key, ctrl);
            case StudioFocus.Search: return Left.Key(key, ctrl);
            case StudioFocus.Values: return Side.Key(key, ctrl);
        }

        if (Diff.Open || Active is null)
        {
            _ = Diff.Key(key);
            return true;
        }

        _ = Editor.Key(args, key);
        return true;
    }

    private bool _swallowChar;

    // Blender's keys for the 3D view's object while the view is enlarged or under the mouse: grab, rotate and scale,
    // then an axis; Enter keeps the change, Escape puts it back, Home resets the object
    private bool ModelKey(GlKeys key)
    {
        if (!Model3D.On || LiveModel) return false;

        var moving = Model3D.Transforming != TransformMode.None;
        if (!moving && !Buffers.Large && !(Hits.At(_mouseX, _mouseY, out _, out var region) && region is PreviewRegion))
        {
            return false;
        }

        switch (key)
        {
            case GlKeys.G: Model3D.Begin(TransformMode.Grab, _mouseX, _mouseY); return true;
            case GlKeys.R: Model3D.Begin(TransformMode.Rotate, _mouseX, _mouseY); return true;
            case GlKeys.S: Model3D.Begin(TransformMode.Scale, _mouseX, _mouseY); return true;
            case GlKeys.X when moving: Model3D.HoldAxis(0, _mouseX, _mouseY); return true;
            case GlKeys.Y when moving: Model3D.HoldAxis(1, _mouseX, _mouseY); return true;
            case GlKeys.Z when moving: Model3D.HoldAxis(2, _mouseX, _mouseY); return true;
            case GlKeys.Enter or GlKeys.KeypadEnter when moving: Model3D.Confirm(); return true;
            case GlKeys.Escape when moving: Model3D.Cancel(); return true;
            case GlKeys.Home: Model3D.ResetObject(); return true;
            default: return moving; // while transforming no other key acts
        }
    }

    private bool Shortcut(GlKeys key, bool shift)
    {
        switch (key)
        {
            case GlKeys.P: Palette.Show(); return true;
            case GlKeys.S or GlKeys.R: BuildNow(); return true;
            case GlKeys.J: RunCommand("panel"); return true;
            case GlKeys.W when _current is { } current: CloseTab(current); return true;
            case GlKeys.F when shift: RunCommand("search"); return true;
            case GlKeys.E when shift: ShowLeft(LeftView.Files); return true;
            case GlKeys.B: Prefs.LeftShown = !Prefs.LeftShown; return true;
            case GlKeys.F or GlKeys.H: Find.Begin(key == GlKeys.H); return true;
            case GlKeys.Z when !shift && Focus is StudioFocus.Tree && _undo is not null: _ = UndoDelete(); return true;
            default: return false;
        }
    }

    // Escape: the history, the find widget, the comparison, the rename, a field; false when nothing was open (the
    // screen then closes)
    private bool Escape()
    {
        if (Buffers.Large)
        {
            Buffers.Large = false; // the enlarged preview closes; consumed only because something closed
            return true;
        }

        if (Editor.Completion.Open)
        {
            Editor.Completion.Close();
            return true;
        }

        if (_historyOpen)
        {
            _historyOpen = false;
            return true;
        }

        if (Rename.Open)
        {
            Rename.Close();
            return true;
        }

        if (Find.Open)
        {
            Find.Close();
            return true;
        }

        if (Diff.Open || Mode != EditorMode.Mine)
        {
            Diff.Close();
            Mode = EditorMode.Mine;
            return true;
        }

        if (Focus is StudioFocus.Rename or StudioFocus.Filter or StudioFocus.Search or StudioFocus.Values)
        {
            Tree.EndRename(false);
            Focus = StudioFocus.Editor;
            return true;
        }

        return false;
    }

    public bool Char(char c)
    {
        if (Modifiers.Ctrl || char.IsControl(c)) return false;

        if (_swallowChar)
        {
            _swallowChar = false;
            return true;
        }

        Redraw();
        if (Buffers.Large)
        {
            return true; // nothing types into the editor under the enlarged preview
        }

        if (Modal.Open)
        {
            Modal.Type(c);
            return true;
        }

        if (Palette.Open)
        {
            Palette.Type(c);
            return true;
        }

        switch (Focus)
        {
            case StudioFocus.Filter or StudioFocus.Rename: Tree.Type(c); break;
            case StudioFocus.Find or StudioFocus.Replace: Find.Type(c); break;
            case StudioFocus.RenameSymbol: Rename.Type(c); break;
            case StudioFocus.Search: Left.Type(c); break;
            case StudioFocus.Values: Side.Type(c); break;
            case StudioFocus.Editor when !Diff.Open: Editor.Insert(c.ToString()); break;
        }

        return true;
    }

    // The palette's and the menus' commands
    private void RunCommand(string command)
    {
        switch (command)
        {
            case "test": BuildNow(); break;
            case "find": Find.Begin(false); break;
            case "replace": Find.Begin(true); break;
            case "search": Search(null); break;
            case "rename-symbol": Rename.Begin(); break;
            case "search-word": Search(Editor.SymbolAtCaret()); break;
            case "panel": Panel.Toggle(); break;
            case "suggest" when Active is { } document: Focus = StudioFocus.Editor; Editor.Completion.Suggest(document, true); break;
            case "undo-delete" when !UndoDelete(): Toast(L("nothing-to-undo")); break;
            case "close-tab" when _current is { } current: CloseTab(current); break;
            case "auto": ToggleAuto(); break;
            case "restore-good": RestoreGood(); break;
            case "history": OpenHistory(); break;
            case "compare": SetMode(EditorMode.Diff); break;
            case "export" when Project is { } project: Modal.Export(project); break;
            case "game": _gamePreview(); break;
            case "export-vanilla": ExportVanilla(); break;
        }
    }
}

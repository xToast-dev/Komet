namespace Komet.Bench;

// "gui": "creative" in a bench.json: on every lap the creative inventory opens at the top of its first tab and, while the player
// stands still, scrolls down by mouse-wheel ticks as a player flicks through it: the vanilla GUI's worst case, where every row of
// variants coming into view builds its meshes on the main thread.
internal static class BenchGui
{
    public const string Creative = "creative", Debug = "debug";
    public const float CaptureSeconds = 15;
    private const double TicksPerSecond = 12;

    private static GuiDialog? _dialog;
    private static double _owed;

    public static void Open(ICoreClientAPI capi)
    {
        if (!NotNull(capi) || !Assert(TicksPerSecond > 0)) return;
        _dialog ??= capi.Gui.LoadedGuis.FirstOrDefault(static g => g.GetType().Name == "GuiDialogInventory");
        if (_dialog is null)
        {
            capi.Logger.Warning("Komet bench: no inventory dialog to scroll");
            return;
        }

        if (!_dialog.IsOpened()) _ = _dialog.TryOpen();
        Scrollbar()?.SetScrollbarPosition(0);
        _owed = 0;
    }

    // dt seconds of flicking: whole wheel ticks, the rest owed to the next frame
    public static void Scroll(ICoreClientAPI capi, float dt)
    {
        if (!NotNull(capi) || !Finite(dt) || Scrollbar() is not { } bar) return;
        _owed += dt * TicksPerSecond;
        for (var i = 0; i < Math.Min((int)_owed, 8); i++)
            bar.OnMouseWheel(capi, new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
        _owed -= Math.Floor(_owed);
        _ = Assert(_owed is >= 0 and < 1);
    }

    private static GuiElementScrollbar? Scrollbar() =>
        Assert(TicksPerSecond > 0) && _dialog is { } dialog && dialog.IsOpened() && dialog.Composers["maininventory"] is { } composer
            ? composer.GetScrollbar("scrollbar")
            : null;
}

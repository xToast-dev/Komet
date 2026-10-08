namespace Komet.Hud;

// What the views share: the mods' time, the audit of Harmony's registry and the capture armed from a hint
internal sealed partial class HudOverlay
{
    private const double ModHintMs = 2, GcHintPercent = 5, LowHintFps = 30, VramHintPercent = 90;

    private HarmonyAudit? _audit;
    private Task? _auditing;

    // The time of every mod but the game itself, per frame
    private double ModsTotal() => NotNull(_timings) ? _timings.TotalExcept("game") : double.NaN;

    // A capture armed for the next spike, shown in the debug window
    private void SpikeCapture()
    {
        if (!NotNull(_debugWindow) || !Assert(!_disposed)) return;
        Capture("spike", 0);
        _debugWindow.Toggle(true);
    }

    // Harmony's registry walked on a pool thread, once per opening of the overview or the Mods tab
    private void RequestAudit()
    {
        if (_auditing is { IsCompleted: false } || !NotNull(_capi.ModLoader)) return;
        var loader = _capi.ModLoader;
        _auditing = Task.Run(() => Volatile.Write(ref _audit, HarmonyAudit.Walk(loader)));
        _ = Assert(_auditing is not null);
    }
}

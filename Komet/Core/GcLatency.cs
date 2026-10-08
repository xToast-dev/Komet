using System.Runtime;

namespace Komet.Core;

// The collector's latency mode, the one GC setting a running game can change (the others are read from the launch environment):
// SustainedLowLatency keeps full collections in the background. Under the game's Interactive mode a gen2 that judged the heap
// fragmented compacted it with every thread stopped: 2.75 s with 5.2 GB live at view distance 1536 while flying. The runtime still
// blocks when the machine runs short of memory, so this only removes the optional stop. Off restores the mode found.
internal static class GcLatency
{
    private static GCLatencyMode? _before;
    private static bool _installed;

    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            Apply();
        }
    } = true;

    public static void Install()
    {
        _installed = true;
        Apply();
        _ = Assert(_installed) && Assert(Enum.IsDefined(GCSettings.LatencyMode));
    }

    public static void Stop()
    {
        _installed = false;
        Apply();
        _ = Assert(_before is null) && Assert(!_installed);
    }

    private static void Apply()
    {
        if (_installed && Enabled)
        {
            if (_before is null && GCSettings.LatencyMode != GCLatencyMode.SustainedLowLatency) _before = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        else if (_before is { } before)
        {
            GCSettings.LatencyMode = before;
            _before = null;
        }

        _ = Assert(Enum.IsDefined(GCSettings.LatencyMode)) && Assert(!_installed || !Enabled || _before is not null ||
                                                                      GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency);
    }
}

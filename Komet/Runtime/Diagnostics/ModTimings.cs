using System.Diagnostics;
using Komet.Features;
using Komet.Runtime.UI.Overlay;

namespace Komet.Runtime.Diagnostics;

// Ranks the profiler's entries by mod and, inside each mod, by cost. Smoothed and reordered like the passes so rows don't jump.
internal sealed class ModTimings
{
    public const int MaxMods = 8, DetailCount = 3;
    private const double Smoothing = 0.25;
    private const int ReorderEvery = 8;

    private readonly (string? Mod, double Ms)[] _mods = new (string?, double)[MaxMods];
    private readonly ModProfiler.Entry?[][] _top = Array.ConvertAll(new int[MaxMods], _ => new ModProfiler.Entry?[DetailCount]);
    private readonly Dictionary<string, double> _byMod = [];
    private readonly List<(string? Mod, double Ms)> _ranked = [];
    private int _updates;

    public string ModName(int place) => Index(place, MaxMods) ? _mods[place].Mod ?? "" : "";
    public double ModMs(int place) => Index(place, MaxMods) && _mods[place].Mod != null ? _mods[place].Ms : double.NaN;
    public string DetailName(int place, int rank) => Index(place, MaxMods) && Index(rank, DetailCount) && _top[place][rank] is { } e ? $"{e.Name} ({HudSettings.Format(e.CallsPerSecond, "F1")}/s)" : "";
    public double DetailMs(int place, int rank) => Index(place, MaxMods) && Index(rank, DetailCount) ? _top[place][rank]?.SmoothMs ?? double.NaN : double.NaN;

    public void Update(int frames, float seconds)
    {
        if (!Assert(frames > 0) || !Assert(seconds > 0) || !Assert(ModProfiler.Entries.Count <= ModProfiler.MaxEntries)) return;
        _byMod.Clear();
        foreach (var entry in ModProfiler.Entries.Values.Bounded(ModProfiler.MaxEntries))
        {
            if (!Assert(entry.Ticks >= 0) || !Assert(entry.Calls >= 0)) continue;
            entry.SmoothMs += (entry.Ticks * 1000.0 / Stopwatch.Frequency / frames - entry.SmoothMs) * Smoothing;
            entry.CallsPerSecond += (entry.Calls / seconds - entry.CallsPerSecond) * Smoothing;
            (entry.Ticks, entry.Calls) = (0, 0);
            _byMod[entry.Mod] = _byMod.GetValueOrDefault(entry.Mod) + entry.SmoothMs;
        }

        if (_updates++ % ReorderEvery != 0)
        {
            for (var i = 0; i < MaxMods; i++)
                if (_mods[i].Mod is { } mod) _mods[i].Ms = _byMod.GetValueOrDefault(mod);
            return;
        }

        _ranked.Clear();
        foreach (var (mod, ms) in _byMod.Bounded(ModStats.MaxLoadedMods)) _ranked.Add((mod, ms));
        _ranked.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        Array.Clear(_mods);
        for (var i = 0; i < Math.Min(MaxMods, _ranked.Count); i++)
        {
            _mods[i] = _ranked[i];
            var top = _top[i];
            Array.Clear(top);
            foreach (var entry in ModProfiler.Entries.Values.Bounded(ModProfiler.MaxEntries))   // insertion into the top-N slots, sorted descending
            {
                if (entry.Mod != _mods[i].Mod) continue;
                var slot = 0;
                for (; slot < DetailCount; slot++) if (top[slot] is null || top[slot]!.SmoothMs < entry.SmoothMs) break;
                if (slot == DetailCount) continue;
                Array.Copy(top, slot, top, slot + 1, DetailCount - slot - 1);
                top[slot] = entry;
            }
        }
    }
}

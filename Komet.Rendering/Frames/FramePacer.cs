using System.Diagnostics;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Komet.Diagnostics;

namespace Komet.Rendering;

// The engine's fps cap (no VSync) sleeps (int)(1000 / MaxFps - elapsed ms): truncation makes frames 0..1 ms short, so
// at 144 fps they alternate ~6/~7 ms and overshoot the cap. Here the wait runs to the exact interval (whole ms asleep,
// never longer than the engine would, the rest yielding), so the engine's sleep computes 0. Off, it sleeps as the
// engine, so FrameClock's later prefix times from the same point. Vulkan's GPU wait (VulkanRenderer.Paced) comes first.
internal static class FramePacer
{
    private const double SleepMarginMs = 0.5, StepMarginMs = 1.1; // timer slack and wake-up: the rest is yielded away
    private const int MaxYields = 1 << 20, MaxSteps = 16;

    private static AccessTools.FieldRef<ClientPlatformWindows, Stopwatch>? _watch;

    public static bool Enabled { get; set; } = true;

    // No cap and no VSync: frames run as fast as each can, 3 to 9 ms apart at a long view distance, and turning looks
    // uneven although the rate is high. Smoothing paces each frame to a little above the recent median instead (the 75th
    // percentile of the last frames' own time): the short ones wait, the rate stays near what it was, longer frames
    // pass as they are.
    public static bool Smoothing { get; set; } = true;

    private const int Window = 64, Percentile = Window * 3 / 4;
    private static readonly double[] Recent = new double[Window], Sorted = new double[Window];
    private static int _recent, _filled;

    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        var watch = AccessTools.FieldRefAccess<ClientPlatformWindows, Stopwatch>("frameStopWatch");
        Patched = false;
        if (!NotNull(harmony) || !NotNull(frame) || !NotNull(watch)) return;
        _watch = watch;
        Patched = harmony.Patch(frame, new HarmonyMethod(Pace) { priority = Priority.First }) is not null;
    }

    // Engine's condition, then its interval on its own stopwatch
    private static void Pace(ClientPlatformWindows __instance)
    {
        Links.Paced();
        var fps = __instance.MaxFps;
        if (FrameSettings.VsyncMode == 1 || _watch is not { } field || field(__instance) is not { IsRunning: true } watch)
        {
            GcIdle.FrameStart(double.NaN); // VSync: the display's interval
            return;
        }

        if (!(fps > 10f && fps < 241f))
        {
            GcIdle.FrameStart(double.NaN); // no cap
            if (Enabled && Smoothing) Smooth(watch);
            return;
        }

        // The cap's wait, paced or not; a collection moved into it shortens the wait that follows
        var left = (Stopwatch.Frequency / (double)fps - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
        GcIdle.FrameStart(Finite(left) ? left : double.NaN);
        if (Enabled)
        {
            Wait(watch, fps);
            return;
        }

        var engine = (int)(1000f / fps - 1000f * watch.ElapsedTicks / Stopwatch.Frequency); // the engine's own, as float
        if (engine > 0) Thread.Sleep(engine);
    }

    // The frame's own time joins the window; then the wait up to the window's 75th percentile
    private static void Smooth(Stopwatch watch)
    {
        var ms = watch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (!Finite(ms) || ms <= 0 || ms > 100) return; // a pause or a hitch says nothing about the pace
        Recent[_recent] = ms;
        (_recent, _filled) = ((_recent + 1) % Window, Math.Min(_filled + 1, Window));
        if (_filled < Window) return;
        Array.Copy(Recent, Sorted, Window);
        Array.Sort(Sorted);
        var target = Sorted[Percentile];
        if (target > ms) Wait(watch, (float)(1000.0 / target));
    }

    internal static void Wait(Stopwatch watch, float fps)
    {
        if (!NotNull(watch) || !Assert(fps > 0f)) return;
        var target = (long)(Stopwatch.Frequency / (double)fps);
        var leftMs = (target - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
        if (leftMs > SleepMarginMs + 1) Thread.Sleep((int)(leftMs - SleepMarginMs));
        // Linux/macOS sleep 1 ms within a few dozen µs (fewer yields burn the core); Windows' timer may sleep 15 ms
        for (var i = 0; i < MaxSteps && !OperatingSystem.IsWindows() &&
                        (target - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency > StepMarginMs; i++)
            Thread.Sleep(1);
        for (var i = 0; i < MaxYields && watch.ElapsedTicks < target; i++)
            if (!Thread.Yield())
                Thread.SpinWait(32);
    }
}

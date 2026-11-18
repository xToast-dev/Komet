using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Komet.Vulkan;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Hud;

// Main-thread half of a protocol, copied into a DebugInput when the capture ends
internal sealed partial class DebugCapture
{
    private const int MaxEntityKinds = 4096, MaxEntities = 1 << 18;

    private DebugInput Snapshot(DebugFrames debug)
    {
        var (stats, gpu, resources) = (_sampler.Frames, _sampler.Gpu, _sampler.Resources);
        var frames = debug.Since(_debugMode == "spike" ? debug.Now - SpikeWindow - PostRoll : 0);
        var features = FeatureHub.Snapshot().ToArray();
        var (entities, total) = Entities();
        return new DebugInput
        {
            Code = _settings.DebugLanguage, Mode = DebugModeText(), Trigger = _debugTrigger, Started = _debugStarted,
            Komet = _build.Label,
            Frames = frames,
            Spikes = debug.Spikes(), Causes = Causes(), ModTimes = DebugModTimes(), Scopes = DebugTimings.TakeTimings(),
            HudMeans = _sampler.Means(), System = SystemRows(), Runtime = RuntimeRows(), World = WorldRows(),
            Render = RenderRows(), Client = ClientRows(), Knobs = KnobRows(), Features = features, Mods = ModRows(),
            Entities = entities, EntityTotal = total,
            VramPercent = DebugProtocol.Share(gpu.VramUsedMb, gpu.VramTotalMb),
            RamFreePercent = DebugProtocol.Share(resources.AvailableRamMb, resources.TotalRamMb),
            TessBacklog = RuntimeStats.chunksAwaitingTesselation, Gc = GC.GetGCMemoryInfo(),
            Scene =
            [
                ("s-vd", ClientSettings.ViewDistance),
                ("s-pixels", (double)_capi.Render.FrameWidth * _capi.Render.FrameHeight),
                ("s-renderer", VulkanMode.RendererCode()), ("s-tris", stats.RenderedTriangles)
            ]
        };
    }

    private string DebugModeText() =>
        _debugMode switch
        {
            "spike" => $"spike >= {DebugText.Num(_debugSpikeMs, "F0")} ms",
            "now" => "now",
            _ => "record"
        };

    private (string Name, int Count, double MaxMs, double AverageMs)[] Causes()
    {
        var causes = new List<(string, int, double, double)>(SpikeLedger.Shown);
        for (var i = 0; i < SpikeLedger.Shown; i++)
        {
            if (_sampler.Spikes.Ranked(i) is { Count: > 0 } cause) causes.Add(cause);
        }

        return [.. causes];
    }

    private (string Mod, double Ms, (string Name, double Ms)[] Top)[] DebugModTimes() =>
        [
            .. _debugMods.Select(m => (m.Key, m.Value.Sum / Math.Max(1, m.Value.Intervals),
                    _debugModTop.GetValueOrDefault(m.Key, [])))
                .OrderByDescending(static m => m.Item2)
        ];

    private (string, string)[] SystemRows()
    {
        var resources = _sampler.Resources;
        var process = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        return
        [
            ("hud-dbg-os", RuntimeInformation.OSDescription),
            ("hud-dbg-runtime", $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}"),
            ("hud-dbg-cpu", $"{process} logical, {DebugText.Num(resources.CpuPercent, "F0")} % " +
                            $"(system {DebugText.Num(resources.SystemCpuPercent, "F0")} %)"),
            ("hud-dbg-ram", $"{resources.TotalRamMb} MB, {resources.AvailableRamMb} MB free"),
            ("hud-dbg-process",
                $"{resources.WorkingSetMb} MB, managed {resources.ManagedUsedMb}/{resources.ManagedCommittedMb} MB, " +
                $"native {resources.NativeMb} MB"),
            ("hud-dbg-gpu", (ScreenManager.Platform?.GetGraphicCardInfos() ?? "").Replace('\n', ' ').Trim()),
            ("hud-dbg-window", $"{_capi.Render.FrameWidth} x {_capi.Render.FrameHeight}"),
            ("hud-dbg-build",
                typeof(DebugCapture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "")
        ];
    }

    private static (string, string)[] RuntimeRows()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var gc = HudPanels.GcConfig(DebugText.Key);
        using var process = Process.GetCurrentProcess();
        return
        [
            ("hud-dbg-gc-config", $"{gc.Mode}; {gc.Budget}"),
            ("hud-dbg-jit-methods", $"{JitInfo.GetCompiledMethodCount()}, " +
                                    $"{DebugText.Num(JitInfo.GetCompilationTime().TotalMilliseconds, "F0")} ms"),
            ("hud-dbg-threadpool",
                $"{ThreadPool.ThreadCount} (min {minWorkers}), {ThreadPool.PendingWorkItemCount} pending, " +
                $"{ThreadPool.CompletedWorkItemCount} done"),
            ("hud-dbg-locks", DebugText.Int(Monitor.LockContentionCount)),
            ("hud-dbg-timers", DebugText.Int(Timer.ActiveCount)),
            ("hud-dbg-process-threads", DebugText.Int(process.Threads.Count)),
            ("hud-dbg-driver-threads", DriverThreads())
        ];
    }

    private static string DriverThreads()
    {
        if (!RenderCost.DriverThreads) return "";

        var text = new StringBuilder();
        for (var i = 0; i < RenderCost.Kinds; i++)
        {
            if (RenderCost.Seen(i))
            {
                _ = text.Append(RenderCost.KindKey(i)).Append(' ')
                    .Append(DebugText.Num(RenderCost.ThreadNs(i) / 1e6, "F1")).Append(" ms  ");
            }
        }

        return text.ToString().TrimEnd();
    }

    private (string, string)[] WorldRows()
    {
        var loaded = RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded;
        var session = _capi.IsSinglePlayer ? "singleplayer" : "multiplayer";
        return
        [
            ("hud-dbg-chunks",
                $"{loaded} loaded, {RuntimeStats.chunksReceived} received, {RuntimeStats.chunksUnloaded} unloaded"),
            ("hud-dbg-tess-queue",
                $"{RuntimeStats.chunksAwaitingTesselation} tessellation, {RuntimeStats.chunksAwaitingPooling} upload"),
            ("hud-dbg-tess-total", DebugText.Int(RuntimeStats.chunksTesselatedTotal)),
            ("hud-dbg-chunks-packed", Packed()),
            ("hud-dbg-view-distance", DebugText.Int(ClientSettings.ViewDistance)),
            ("hud-dbg-session", session),
            ("hud-dbg-world-time", $"{DebugText.Num(_capi.World.Calendar?.TotalDays ?? double.NaN, "F2")} days")
        ];
    }

    private string Packed()
    {
        if (_capi.World is not ClientMain game) return "-";

        var (packed, total) = TerrainRebuild.Census(game);
        if (total <= 0) return "-";

        return Assert(packed <= total) ? $"{packed} of {total} ({DebugText.Num(100.0 * packed / total, "F0")} %)" : "-";
    }

    private (string, string)[] RenderRows()
    {
        var (frames, gpu) = (_sampler.Frames, _sampler.Gpu);
        var calls = frames.DrawCallsPerFrame;
        return
        [
            ("hud-dbg-renderer", DebugText.Key(VulkanMode.RendererKey())),
            ("hud-dbg-draw-calls", DebugText.Int(calls)),
            ("hud-dbg-triangles", $"{frames.RenderedTriangles} rendered, {frames.AvailableTriangles} allocated"),
            ("hud-dbg-gpu-time", $"{DebugText.Num(RenderCost.FrameGpuMs(gpu.Ms), "F2")} ms"),
            ("hud-dbg-vram", $"{DebugText.Num(gpu.VramUsedMb, "F0")} / {DebugText.Num(gpu.VramTotalMb, "F0")} MB, " +
                             $"{DebugText.Num(gpu.EvictionsPerSec, "F1")} evictions/s"),
            ("hud-dbg-entities-rendered", DebugText.Int(RuntimeStats.renderedEntities)),
            ("hud-dbg-vram-clients", VramClients())
        ];
    }

    private string VramClients()
    {
        var clients = DrmMemory.Read();
        if (clients.Length == 0) return "-";

        var own = DrmMemory.TotalVramMb(clients);
        var ledger = VulkanRenderer.Snapshot().MemoryMb;
        var rest = _sampler.Gpu.VramUsedMb - own;
        var tail = double.IsFinite(rest) ? $"; rest of the device {DebugText.Num(Math.Max(0, rest), "F0")} MB" : "";
        return $"{own} MB: {DrmMemory.Text(clients, ledger)}; Komet's Vulkan ledger {ledger} MB" + tail;
    }

    // Never the whole file, which holds the session key
    private static (string, string)[] ClientRows()
    {
        var rows = new (string Name, double Value)[]
        {
            ("viewDistance", ClientSettings.ViewDistance), ("maxFps", ClientSettings.MaxFPS),
            ("vsyncMode", ClientSettings.VsyncMode),
            ("gameWindowMode", ClientSettings.GameWindowMode), ("ssaa", ClientSettings.SSAA),
            ("shadowMapQuality", ClientSettings.ShadowMapQuality), ("ssaoQuality", ClientSettings.SSAOQuality),
            ("godRayQuality", ClientSettings.GodRayQuality), ("bloom", ClientSettings.Bloom ? 1 : 0),
            ("fieldOfView", ClientSettings.FieldOfView), ("particleLevel", ClientSettings.ParticleLevel),
            ("chunkVerticesUploadRateLimiter", ClientSettings.ChunkVerticesUploadRateLimiter),
            ("optimizeRamMode", ClientSettings.OptimizeRamMode), ("guiScale", ClientSettings.GUIScale)
        };
        return Array.ConvertAll(rows, static r => (r.Name, r.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static (string, int, int)[] KnobRows()
    {
        var count = Math.Min(Knobs.Count, Knobs.MaxKnobs);
        var rows = new (string, int, int)[count];
        for (var i = 0; i < Math.Min(count, Knobs.MaxKnobs); i++)
        {
            rows[i] = (Knobs.At(i).Key, Knobs.At(i).Get(), Knobs.At(i).Engine);
        }

        return rows;
    }

    private (string, string, string, string, string)[] ModRows()
    {
        var mods = new List<(string, string, string, string, string)>();
        foreach (var mod in _capi.ModLoader.Mods.Bounded(HarmonyAudit.MaxLoadedMods))
        {
            if (mod.Info is { } info)
            {
                mods.Add((info.ModID, info.Name, info.Version, info.Type.ToString(), mod.FileName ?? ""));
            }
        }

        mods.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return [.. mods];
    }

    private ((string, int)[], int) Entities()
    {
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var entity in _capi.World.LoadedEntities.Values.Bounded(MaxEntities))
        {
            var code = entity.Code?.ToShortString() ?? entity.GetType().Name;
            if (byCode.Count < MaxEntityKinds)
            {
                CollectionsMarshal.GetValueRefOrAddDefault(byCode, code, out _)++;
            }
            else if (byCode.TryGetValue(code, out var count))
            {
                byCode[code] = count + 1;
            }

            total++;
        }

        var ranked = byCode.OrderByDescending(static e => e.Value).Select(static e => (e.Key, e.Value)).ToArray();
        return (ranked, total);
    }
}

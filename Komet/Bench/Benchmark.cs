using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Bench;

// The automated benchmark (scripts/bench.sh). Nothing here runs or allocates unless the environment variable KOMET_BENCH names a
// bench.json, and it runs once per process: a second world in the same session plays normally.
internal static class Benchmark
{
    public const string Variable = "KOMET_BENCH";

    // The frame capacity budget; frames past it are counted as dropped, never grown into
    private const int FramesPerSecond = 300;

    private const int MaxPolls = 1200, PollMs = 100; // two minutes for the server to save and stop
    private const double ClimbAllowance = 120;
    private static bool _ran;

    public static void Install(ICoreClientAPI capi)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(path) || _ran || !NotNull(capi)) return;
        _ran = true;
        capi.Logger.Notification("Komet bench: {0} is set, reading {1}", Variable, path);
        BenchConfig config;
        BenchSegment[] segments;
        try
        {
            config = BenchConfig.Parse(File.ReadAllText(path));
            segments = BenchScenario.Expand(config);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException) // Path.GetFullPath refuses a path with a NUL in it
        {
            Refuse(capi, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "result.json"), e.Message,
                false);
            return;
        }

        var refusal = Refusal(capi, config);
        if (refusal is not null)
        {
            Refuse(capi, config.Output, refusal, true);
            return;
        }

        var run = new BenchRun(config, segments);
        var driver = new BenchDriver(capi, run,
            new BenchRecorder(Capacity(segments), segments.Length, BenchDriver.Spikes));
        capi.Event.RegisterRenderer(driver, EnumRenderStage.Before, "komet-bench");
        run.Status("loaded");
    }

    // Every segment at its full length, at FramesPerSecond: an upper bound, never more than BenchRecorder.MaxFrames
    private static int Capacity(ReadOnlySpan<BenchSegment> segments)
    {
        var seconds = ClimbAllowance;
        if (!Assert(segments.Length is > 0 and <= BenchScenario.MaxSegments) || !Finite(seconds)) return 1;
        for (var i = 0; i < Math.Min(segments.Length, BenchScenario.MaxSegments); i++) seconds += segments[i].Seconds;
        return (int)Math.Clamp(seconds * FramesPerSecond, 1, BenchRecorder.MaxFrames);
    }

    // Three silent failures: the installed release benchmarked instead of the build under test, a Debug build (its timings run
    // about 2.6 times high), and a multiplayer session, where fly mode and the server commands are not ours
    private static string? Refusal(ICoreClientAPI capi, BenchConfig config)
    {
        if (!NotNull(capi) || !NotNull(config)) return "no client api";
        if (!capi.IsSinglePlayer) return "a benchmark runs only in singleplayer";
        var assembly = typeof(Benchmark).Assembly;
        if (!Under(assembly.Location, config.ModDir))
            return $"Komet was loaded from {assembly.Location}, not from {config.ModDir}";
        var mods = capi.ModLoader.Mods.Bounded(ModStats.MaxLoadedMods).Where(mod => mod.Info?.ModID == KometModSystem.ModId)
            .ToList();
        if (mods.Count != 1)
            return $"{mods.Count} enabled mods have the id komet, the build under test must be the only one";
        if (!Under(mods[0].SourcePath, config.ModDir))
            return $"the komet mod comes from {mods[0].SourcePath}, not from {config.ModDir}";
        return JitOptimizerDisabled(assembly) ? "a Debug build of Komet is loaded; build Release (./build.sh)" : null;
    }

    private static bool Under(string? path, string directory)
    {
        if (!Assert(directory.Length > 0) || string.IsNullOrEmpty(path)) return false;
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Assert(root.Length > 1) && full.StartsWith(root, StringComparison.Ordinal);
    }

    private static bool JitOptimizerDisabled(Assembly assembly)
    {
        return NotNull(assembly) && Assert(assembly.GetName().Name == "Komet") &&
               assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true;
    }

    // What a result states about the build, the runtime (GCSettings: what the runtime applied of the script's environment), the
    // world, and the client settings that change frame times
    public static void Collect(ICoreClientAPI capi, BenchRun run)
    {
        var (assembly, mod) = (typeof(Benchmark).Assembly, capi.ModLoader.GetMod(KometModSystem.ModId));
        if (!NotNull(mod) || !NotNull(run) || !NotNull(capi.Render)) return;
        run.AddInfo("komet.version", mod.Info.Version);
        run.AddInfo("komet.configuration",
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);
        run.AddInfo("komet.assembly", assembly.Location);
        run.AddInfo("engine.version", GameVersion.OverallVersion);
        run.AddInfo("gl", ScreenManager.Platform?.GetGraphicCardInfos());
        run.AddInfo("dotnet.version", Environment.Version.ToString());
        run.AddInfo("os", RuntimeInformation.OSDescription);
        run.AddInfo("processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        run.AddInfo("gc.server", GCSettings.IsServerGC ? "true" : "false");
        run.AddInfo("gc.latency", GCSettings.LatencyMode.ToString());
        run.AddInfo("gc.concurrent", AppContext.GetData("System.GC.Concurrent")?.ToString() ?? "default");
        run.AddInfo("world.savegameId", capi.World.SavegameIdentifier);
        run.AddInfo("world.seed", capi.World.Seed.ToString(CultureInfo.InvariantCulture));
        Client(run, "viewDistance", ClientSettings.ViewDistance);
        Client(run, "vsyncMode", ClientSettings.VsyncMode);
        Client(run, "maxFps", ClientSettings.MaxFPS);
        Client(run, "frameWidth", capi.Render.FrameWidth);
        Client(run, "frameHeight", capi.Render.FrameHeight);
        Client(run, "gameWindowMode", ClientSettings.GameWindowMode);
        Client(run, "ssaa", ClientSettings.SSAA);
        Client(run, "shadowMapQuality", ClientSettings.ShadowMapQuality);
        Client(run, "ssaoQuality", ClientSettings.SSAOQuality);
        Client(run, "godRayQuality", ClientSettings.GodRayQuality);
        Client(run, "bloom", ClientSettings.Bloom ? 1 : 0);
        Client(run, "fieldOfView", ClientSettings.FieldOfView);
        Client(run, "particleLevel", ClientSettings.ParticleLevel);
        Client(run, "chunkVerticesUploadRateLimiter", ClientSettings.ChunkVerticesUploadRateLimiter);
        Client(run, "optimizeRamMode", ClientSettings.OptimizeRamMode);
    }

    private static void Client(BenchRun run, string key, double value)
    {
        if (!Assert(key.Length > 0) || !Finite(value)) return;
        run.AddInfo("client." + key, value.ToString(CultureInfo.InvariantCulture));
    }

    // A run that cannot start still leaves a result saying why. With a readable config the unattended singleplayer run also ends
    // once the world is up; without one nothing says the run was unattended, and the script's watchdog ends it instead.
    private static void Refuse(ICoreClientAPI capi, string output, string reason, bool exit)
    {
        if (!NotNull(capi) || !Assert(reason.Length > 0)) return;
        capi.Logger.Error("Komet bench refused: {0}", reason);
        try
        {
            using var stream = File.Create(output);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteString("schema", BenchReport.Schema);
            json.WriteBoolean("complete", false);
            json.WriteString("error", reason);
            json.WriteEndObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger.Error("Komet bench: {0} not written: {1}", output, e.Message);
        }

        if (!exit || !capi.IsSinglePlayer || capi.World is not ClientMain game || !NotNull(capi.Event)) return;
        capi.Event.LevelFinalize += () => Exit(game, "komet bench refused");
    }

    // Ends the process the way a player would, in two steps, both outside any game frame. NativeWindow.Close raises OnClosing
    // synchronously, and GuiScreenRunningGame.OnWindowClosed disposes ClientMain and nulls runningGame on the spot: called from a
    // renderer or a ClientMain task, the rest of RenderToPrimary would read the null. ScreenManager's own queue is drained at the
    // top of OnNewFrame, before Render, which is the one safe place. Step one is GuiDialogEscapeMenu.OnLeaveWorld (the server saves
    // and stops on its thread), step two closes the window once ScreenManager.Platform.IsServerRunning drops. capi is disposed
    // after step one, so the poller touches only ScreenManager statics.
    public static void Exit(ClientMain game, string reason)
    {
        if (!NotNull(game) || !Assert(reason.Length > 0)) return;
        ScreenManager.EnqueueMainThreadTask(() => Leave(game, reason));
        _ = Task.Run(() => CloseWhenServerStopped(reason));
    }

    private static void Leave(ClientMain game, string reason)
    {
        if (!NotNull(game) || !Assert(reason.Length > 0) || game.disposed) return;
        game.SendLeave(0);
        game.exitReason = reason;
        game.DestroyGameSession(false, EnumExitMode.SoftExit);
    }

    private static async Task CloseWhenServerStopped(string reason)
    {
        if (!Assert(reason.Length > 0) || !NotNull(ScreenManager.Platform)) return;
        for (var i = 0; i < MaxPolls; i++)
        {
            await Task.Delay(PollMs).ConfigureAwait(false);
            if (!ScreenManager.Platform.IsServerRunning) break;
        }

        ScreenManager.EnqueueMainThreadTask(() => ScreenManager.Platform.WindowExit(reason, EnumExitMode.SoftExit));
    }
}

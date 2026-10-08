using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Server;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Host;

// Runs a singleplayer world's server as a child process - the game's own VintagestoryServer, on 127.0.0.1 - and joins it as a
// multiplayer client, so the client's garbage collections scan only the client's heap and the server's allocations never trigger them.
// Komet's code first runs when the in-process server loads its mods (HostSystem): the first world after the game start is handed over
// from there, before any chunk is loaded; the prefix on ConnectToSingleplayer, installed then and never removed, takes every later
// start. A new world, little memory, no server binary or a child that does not come up leave the engine's own path.
internal static class HostLaunch
{
    internal const string ParentVariable = "KOMET_HOST_PARENT", ControlVariable = "KOMET_HOST_CONTROL";
    internal const byte Paused = (byte)'P', Resumed = (byte)'R';
    private const string ConfigFile = "serverconfig.json", Knob = "ServerProcess";
    private const long MinMemory = 15L << 30; // 16 GB machines report a little less
    private const int MaxLines = 4096;

    private static Harmony? _harmony;
    private static bool _bypass;
    private static byte[]? _config;
    private static volatile bool _ready;
    private static volatile string _line = "";
    private static int _lines;

    // The child connects back on this loopback port at its StartPre: a pause it reads off the main thread, which a paused server
    // stops, and a link whose end tells it the game is gone
    private static TcpListener? _control;
    private static Socket? _link;
    private static bool _released; // /stop is written once per child

    // The knob's view of the switch; what decides is the saved file (Wanted), which exists before Komet's client side does
    public static bool Enabled { get; set; }

    public static Process? Child { get; private set; }
    public static int Port { get; private set; }
    public static string Password { get; private set; } = "";
    public static bool Ready => _ready;

    // This client plays a singleplayer world in a child: the player holds the highest role, as in the game's own singleplayer
    public static bool Hosting => Child is { HasExited: false };
    public static string Line => _line;

    private static string Binary =>
        Path.Combine(GamePaths.Binaries, OperatingSystem.IsWindows() ? "VintagestoryServer.exe" : "VintagestoryServer");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "runningGame")]
    private static extern ref ClientMain RunningGame(GuiScreenRunningGame screen);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CurrentScreen")]
    private static extern ref GuiScreen Current(ScreenManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Systems")]
    private static extern ref ServerSystem[] Systems(ServerMain server);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "gameDatabase")]
    private static extern ref GameDatabase Database(ChunkServerThread thread);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RepairMode")]
    private static extern ref bool RepairMode(StartServerArgs args);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "StartMainMenu")]
    public static extern void MainMenu(ScreenManager manager);

    public static bool Wanted() => Saved(Knob) == true;

    // A switch as komet-hud.json holds it, for code that runs before Komet's client side or without one; null when not saved
    public static bool? Saved(string key)
    {
        try
        {
            if (!NotNull(GamePaths.ModConfig) || !Assert(key.Length > 0)) return null;
            var path = Path.Combine(GamePaths.ModConfig, "komet-hud.json");
            return File.Exists(path) && JObject.Parse(File.ReadAllText(path))[key] is { Type: JTokenType.Boolean } on
                ? on.Value<bool>()
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Eligible(StartServerArgs? args) =>
        NotNull(args) && Wanted() && !RepairMode(args) &&
        File.Exists(args.SaveFileLocation) && File.Exists(Binary) && GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >= MinMemory;

    public static void Install()
    {
        if (_harmony is not null) return;
        var target = AccessTools.Method(typeof(ScreenManager), nameof(ScreenManager.ConnectToSingleplayer), [typeof(StartServerArgs)]);
        var pause = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.PauseGame), [typeof(bool)]);
        var leave = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.DestroyGameSession), [typeof(bool), typeof(EnumExitMode)]);
        if (!NotNull(target) || !NotNull(pause) || !NotNull(leave)) return;
        _harmony = new Harmony("komet.host"); // never unpatched: Komet's own Harmony leaves with the world, this outlives it
        _ = Assert(_harmony.Patch(target, prefix: new HarmonyMethod(typeof(HostLaunch), nameof(Connect))) is not null);
        _ = Assert(_harmony.Patch(pause, prefix: new HarmonyMethod(typeof(HostLaunch), nameof(PauseFirst)),
            finalizer: new HarmonyMethod(typeof(HostLaunch), nameof(PauseLast))) is not null);
        _ = Assert(_harmony.Patch(leave, prefix: new HarmonyMethod(typeof(HostLaunch), nameof(Leave))) is not null);
    }

    // The engine pauses a singleplayer game only (the escape menu, the handbook); for the time of the call this one counts as one,
    // and the child pauses with it
    private static void PauseFirst(ClientMain __instance, out bool __state)
    {
        __state = NotNull(__instance) && !__instance.IsSingleplayer && Hosting;
        if (__state) __instance.IsSingleplayer = true;
    }

    private static Exception? PauseLast(ClientMain __instance, bool __state, Exception? __exception)
    {
        if (!__state || !NotNull(__instance)) return __exception;
        __instance.IsSingleplayer = false;
        if (!__instance.IsPaused) __instance.LastReceivedMilliseconds = __instance.ElapsedMilliseconds; // a pause is no silence
        Send(__instance.IsPaused ? Paused : Resumed);
        return __exception;
    }

    private static void Send(byte message)
    {
        if (_link is not { } link || !Assert(message is Paused or Resumed)) return;
        try
        {
            _ = link.Send([message]);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            ScreenManager.Platform.Logger.Warning("Komet: the server process did not get the pause: {0}", e.Message);
        }
    }

    // Once the child accepts players: it connected back at its StartPre, long before
    public static void Accept()
    {
        if (_control is not { } control) return;
        _link?.Dispose();
        _link = control.Pending() ? control.AcceptSocket() : null;
        control.Stop();
        _control = null;
        _ = Assert(_link is not null);
    }

    private static bool Connect(ScreenManager __instance, StartServerArgs serverargs)
    {
        if (_bypass || !NotNull(__instance) || !NotNull(serverargs) || !Eligible(serverargs)) return true;
        __instance.LoadScreen(new HostScreen(__instance, serverargs, null));
        return false;
    }

    // The engine's singleplayer start, past the prefix
    public static void Fallback(ScreenManager manager, StartServerArgs args)
    {
        if (!NotNull(manager) || !NotNull(args)) return;
        _bypass = true;
        try
        {
            manager.ConnectToSingleplayer(args);
        }
        finally
        {
            _bypass = false;
        }
    }

    // On the in-process server's thread, from StartPre: the world is open but nothing of it loaded. HardExit makes Launch return after
    // this phase without saving; the main thread then ends the client's session as the loading screen's Cancel does and takes over.
    public static void Handover(ICoreServerAPI api)
    {
        if (!NotNull(api) || !Wanted()) return;
        Install();
        var manager = ClientProgram.screenManager;
        if (!NotNull(manager) || Current(manager)?.ParentScreen is not GuiScreenRunningGame running) return;
        var (args, server) = (running.serverargs, api.World as ServerMain);
        if (!Eligible(args) || !NotNull(server) || !NotNull(Owner(server))) return;
        ScreenManager.Platform.ExitSinglePlayerServer(EnumExitMode.HardExit);
        ScreenManager.EnqueueMainThreadTask(() =>
        {
            RunningGame(running)?.DestroyGameSession(gotDisconnected: false, EnumExitMode.HardExit);
            manager.LoadScreen(new HostScreen(manager, args, server));
        });
    }

    // Once the stopped in-process server is gone: it opened the savegame in its configuration phase and only closes it on a chunk
    // thread that never started
    public static void Close(ServerMain server)
    {
        if (!NotNull(server) || Owner(server) is not { } thread) return;
        using var database = Database(thread);
        _ = Assert(database is not null);
    }

    // The chunk thread that holds the savegame connection
    private static ChunkServerThread? Owner(ServerMain server)
    {
        if (!NotNull(server) || !NotNull(Systems(server))) return null;
        foreach (var system in Systems(server).Bounded(64))
            if (system?.GetType().Name == "ServerSystemLoadAndSaveGame" &&
                AccessTools.Field(system.GetType(), "chunkthread")?.GetValue(system) is ChunkServerThread thread)
                return thread;
        return null;
    }

    // The child on a free loopback port. The config file as it was is kept: --withconfig writes its values into it, Restore puts it back.
    public static bool Start(StartServerArgs args)
    {
        if (!NotNull(args) || !Assert(Child is null or { HasExited: true })) return false;
        try
        {
            var path = Path.Combine(GamePaths.Config, ConfigFile);
            _config = File.Exists(path) ? File.ReadAllBytes(path) : [];
            _ = Assert(_config is not null && NotNull(GamePaths.DataPath));
            (Port, Password, _ready, _line, _lines) = (FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), false, "", 0);
            _released = false;
            _control?.Stop();
            _control = new TcpListener(IPAddress.Loopback, 0);
            _control.Start();
            Process? child = new() { StartInfo = Info(args), EnableRaisingEvents = true };
            try
            {
                child.OutputDataReceived += static (_, e) => Read(e.Data);
                child.ErrorDataReceived += static (_, e) => Read(e.Data);
                if (!child.Start()) return false;
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                Child?.Dispose();
                (Child, child) = (child, null);
                return true;
            }
            finally
            {
                child?.Dispose();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ScreenManager.Platform.Logger.Warning("Komet: the server process did not start: {0}", e.Message);
            Restore();
            return false;
        }
    }

    private static ProcessStartInfo Info(StartServerArgs args)
    {
        _ = NotNull(args);
        var config = new JObject
        {
            [nameof(ServerConfig.WorldConfig)] = JObject.FromObject(args),
            [nameof(ServerConfig.VerifyPlayerAuth)] = false,
            [nameof(ServerConfig.Password)] = Password,
            [nameof(ServerConfig.AdvertiseServer)] = false,
            [nameof(ServerConfig.Upnp)] = false,
            [nameof(ServerConfig.MaxChunkRadius)] = Math.Max(1, ClientSettings.ViewDistance / 32),
            [nameof(ServerConfig.ModPaths)] = new JArray(ClientSettings.ModPaths) // the singleplayer start passes them, WorldConfig does not
        };
        var info = new ProcessStartInfo(Binary)
        {
            WorkingDirectory = GamePaths.Binaries, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in (string[])
                 [
                     "--dataPath=" + GamePaths.DataPath, "--logPath=" + Path.Combine(GamePaths.Logs, "komet-server"),
                     "--ip=127.0.0.1", "--port=" + Port.ToString(CultureInfo.InvariantCulture), "--maxclients=1",
                     "--withconfig=" + config.ToString(Formatting.None)
                 ])
            info.ArgumentList.Add(arg);
        info.Environment[ParentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (_control?.LocalEndpoint is IPEndPoint control)
            info.Environment[ControlVariable] = control.Port.ToString(CultureInfo.InvariantCulture);
        info.Environment["DOTNET_gcServer"] = "1";
        info.Environment["DOTNET_GCDynamicAdaptationMode"] = "1";
        _ = Assert(info.ArgumentList.Count == 6) && Assert(Port > 0) && Assert(Password.Length == 32);
        return info;
    }

    // stdout must be drained or the child blocks on a full pipe; the dedicated server logs "<name> now running on Port ..." once it
    // accepts players
    private static void Read(string? line)
    {
        if (line is null || ++_lines > MaxLines) return;
        _line = line.Length > 160 ? line[..160] : line;
        _ = Assert(_line.Length <= 160);
        if (line.Contains("now running", StringComparison.Ordinal)) _ready = true;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = Assert(listener.LocalEndpoint is IPEndPoint) ? ((IPEndPoint)listener.LocalEndpoint).Port : 0;
        listener.Stop();
        return Assert(port > 0) ? port : 42420;
    }

    public static void Restore()
    {
        if (_config is null) return;
        var path = Path.Combine(GamePaths.Config, ConfigFile);
        _ = Assert(NotNull(GamePaths.Config));
        try
        {
            if (_config.Length > 0) File.WriteAllBytes(path, _config);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ScreenManager.Platform.Logger.Warning("Komet: serverconfig.json not restored: {0}", e.Message);
        }

        _config = null;
    }

    // The child saves and ends: the link's end resumes a paused child and stops it, /stop is the second way in. Never waited for and
    // never killed here: a world that was played is saved however long that takes (the next start waits, HostScreen).
    public static void Release()
    {
        _link?.Dispose();
        _control?.Stop();
        (_link, _control) = (null, null);
        if (_released || Child is not { HasExited: false } child || !Assert(child.StartInfo.RedirectStandardInput)) return;
        _released = true;
        try
        {
            child.StandardInput.WriteLine("/stop");
            child.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            ScreenManager.Platform.Logger.Warning("Komet: stopping the server process: {0}", e.Message);
        }
    }

    // A child that never had a player - it did not come up, or the start was cancelled: nothing played is lost when it is killed,
    // and the engine's start that may follow needs the savegame free
    public static void Abort()
    {
        Release();
        if (Child is not { } child || !Assert(!child.StartInfo.UseShellExecute)) return;
        try
        {
            if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
            _ = Assert(child.WaitForExit(10_000));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ScreenManager.Platform.Logger.Warning("Komet: ending the server process: {0}", e.Message);
        }
    }

    // Leaving the world, also from the escape menu while the child is paused: it saves and stops at once
    private static void Leave()
    {
        if (Hosting) Release();
    }
}

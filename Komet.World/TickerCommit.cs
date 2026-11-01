using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// The client scans the 74³ blocks around the player for particle and ambient-sound tickers on its own thread, then commits
// the result on the main thread: a dictionary of every ticker (tens of thousands in a meadow) and the merged ambient
// sounds, built inside the frame (0.8 ms a frame on average, as single spikes). Here the scan thread builds both before it
// queues the commit, and the main thread only swaps them in. Nothing else writes what the build reads while the commit
// waits: the next scan starts only once the commit has run.
internal static class TickerCommit
{
    private const int MaxInstructions = 4096;

    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, List<TickingBlockData>> Current =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, List<TickingBlockData>>("currentTickers");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, Dictionary<int, TickerMetaData>> Committed =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, Dictionary<int, TickerMetaData>>("committedTickers");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, object> CommittedLock =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, object>("committedTickersLock");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, object> ScanLock =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, object>("shouldStartScanningLock");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, Vec3i> PlayerPos =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, Vec3i>("currentPlayerPosDiv8");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, Vec3i> CommittedPos =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, Vec3i>("commitedPlayerPosDiv8");
    private static readonly AccessTools.FieldRef<SystemClientTickingBlocks, bool> Frozen =
        AccessTools.FieldRefAccess<SystemClientTickingBlocks, bool>("freezeCtBlocks");
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Game =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly System.Func<SystemClientTickingBlocks, List<AmbientSound>>? Merge =
        AccessTools.MethodDelegate<System.Func<SystemClientTickingBlocks, List<AmbientSound>>>(
            AccessTools.Method(typeof(SystemClientTickingBlocks), "MergeEqualAmbientSounds"));

    private sealed record Built(SystemClientTickingBlocks System, Dictionary<int, TickerMetaData> Tickers, Vec3i At,
        List<AmbientSound> Sounds);

    private static volatile Built? _built;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }
    public static long Prepared { get; private set; }

    internal static MethodBase? Target() =>
        AccessTools.DeclaredMethod(typeof(SystemClientTickingBlocks), nameof(SystemClientTickingBlocks.OnSeperateThreadGameTick));

    public static void Install(Harmony harmony)
    {
        (Rewritten, _built) = (false, null);
        var commit = AccessTools.DeclaredMethod(typeof(SystemClientTickingBlocks), nameof(SystemClientTickingBlocks.CommitScan));
        if (!NotNull(harmony) || Merge is null || commit is null || Target() is not MethodInfo tick) return;
        _ = harmony.Patch(tick, transpiler: new HarmonyMethod(AccessTools.Method(typeof(TickerCommit), nameof(Rewrite))));
        if (Rewritten)
            _ = harmony.Patch(commit, new HarmonyMethod(AccessTools.Method(typeof(TickerCommit), nameof(Committing))));
    }

    public static void Forget() => _built = null;

    // The one game.EnqueueMainThreadTask(commit, "commitscan") becomes Queued(game, commit, "commitscan")
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var enqueue = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.EnqueueMainThreadTask), [typeof(Action), typeof(string)]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(enqueue)) return code;
        Rewritten = Il.SubstituteSingle(code, c => c.Calls(enqueue), AccessTools.Method(typeof(TickerCommit), nameof(Queued)));
        return code;
    }

    // Scan thread: the scan is done, the commit is built here before the main thread is asked to take it
    internal static void Queued(ClientMain game, Action commit, string code)
    {
        _built = Enabled && commit?.Target is SystemClientTickingBlocks system ? Build(system) : null;
        game.EnqueueMainThreadTask(commit, code);
    }

    private static Built? Build(SystemClientTickingBlocks system)
    {
        if (Frozen(system) || Merge is null) return null;
        Vec3i at;
        lock (ScanLock(system)) at = PlayerPos(system);
        var committedAt = CommittedPos(system);
        var (dx, dy, dz) = ((at.X - committedAt.X) * 8, (at.Y - committedAt.Y) * 8, (at.Z - committedAt.Z) * 8);
        var current = Current(system);
        var tickers = new Dictionary<int, TickerMetaData>(current.Count);
        var now = Game(system).ElapsedMilliseconds;
        lock (CommittedLock(system))
        {
            var committed = Committed(system);
            foreach (var ticker in current.Bounded(1 << 19))
            {
                var (x, y, z) = (ticker.DeltaIndex3d & 0x3FF, (ticker.DeltaIndex3d >> 10) & 0x3FF, (ticker.DeltaIndex3d >> 20) & 0x3FF);
                var key = (x + dx) | ((y + dy) << 10) | ((z + dz) << 20);
                tickers[ticker.DeltaIndex3d] = new TickerMetaData
                {
                    TickingSinceMs = committed.TryGetValue(key, out var was) ? was.TickingSinceMs : now,
                    IsWindAffected = ticker.IsWindAffected,
                    WindAffectedNess = ticker.WindAffectedNess
                };
            }
        }

        Prepared++;
        return new Built(system, tickers, at, Merge(system));
    }

    // CommitScan's prefix, main thread: what the scan thread built is swapped in; without it (switched off, frozen, another
    // system) the engine commits as before
    private static bool Committing(SystemClientTickingBlocks __instance)
    {
        var built = Interlocked.Exchange(ref _built, null);
        if (built is null || built.System != __instance || Frozen(__instance)) return true;
        lock (ScanLock(__instance))
        {
            CommittedPos(__instance) = built.At;
            Current(__instance).Clear();
            lock (CommittedLock(__instance)) Committed(__instance) = built.Tickers;
        }

        Game(__instance).eventManager?.OnAmbientSoundsScanComplete(built.Sounds);
        return false;
    }
}

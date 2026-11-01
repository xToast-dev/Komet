using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// SystemCompressChunks.OnFinalizeFrame (main thread, every 4 s) held chunksLock while it committed every chunk packed
// since (ChunkPacking packs up to 200 a second: hundreds at once, each freeing its arrays) and while it walked all loaded
// chunks for the next ones; tessellation, chunk visibility and every locked lookup waited (a join: ~1 s in 30 s). The
// same steps here without chunksLock: only the main thread changes the chunk dictionary (loadChunkMT, OverloadChunkMT,
// HandleChunkUnload), so the main thread reads it unlocked, while other threads only read it. Committing takes each
// chunk's own lock, and a chunk read within its time is never committed (TryCommitPackAndFree), as in the engine.
internal static class PackCommit
{
    private const int ScanMs = 4000, MaxCommits = 1 << 16, MaxChunks = 1 << 20;
    private const string System = "Vintagestory.Client.NoObf.SystemCompressChunks";

    private static AccessTools.FieldRef<object, long>? _scanned;
    private static AccessTools.FieldRef<object, float>? _ratio;
    private static AccessTools.FieldRef<object, Queue<long>>? _compactable;
    private static AccessTools.FieldRef<object, int>? _compressed;
    private static AccessTools.FieldRef<object, int[]>? _ttls;

    public static bool Installed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain? Game(ClientSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "compactSyncLock")]
    private static extern ref object SyncLock(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Centre(ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Edge(ClientChunk chunk);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var type = AccessTools.TypeByName(System);
        var frame = type is null ? null : AccessTools.DeclaredMethod(type, "OnFinalizeFrame", [typeof(float)]);
        if (!NotNull(harmony) || type is null || frame is null ||
            Array.Exists(["chunkCompressScanTimer", "compressionRatio", "compactableClientChunks", "compressed", "ttlsByRamMode"],
                f => AccessTools.DeclaredField(type, f) is null)) return;
        _scanned = AccessTools.FieldRefAccess<object, long>(AccessTools.DeclaredField(type, "chunkCompressScanTimer"));
        _ratio = AccessTools.FieldRefAccess<object, float>(AccessTools.DeclaredField(type, "compressionRatio"));
        _compactable = AccessTools.FieldRefAccess<object, Queue<long>>(AccessTools.DeclaredField(type, "compactableClientChunks"));
        _compressed = AccessTools.FieldRefAccess<object, int>(AccessTools.DeclaredField(type, "compressed"));
        _ttls = AccessTools.FieldRefAccess<object, int[]>(AccessTools.DeclaredField(type, "ttlsByRamMode"));
        Installed = harmony.Patch(frame, new HarmonyMethod(typeof(PackCommit), nameof(Finalizing))) is not null;
    }

    // Prefix replacing the engine's body; false: done here
    private static bool Finalizing(ClientSystem __instance)
    {
        if (!Installed || Game(__instance) is not { WorldMap: { } map } game || game.EntityPlayer?.Pos is not { } at ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return true;
        Shown(__instance, game);
        var now = game.Platform.EllapsedMs;
        if (now - _scanned!(__instance) < ScanMs) return false;
        _scanned(__instance) = now;
        var ttl = _ttls!(__instance)[Math.Clamp(ClientSettings.OptimizeRamMode, 0, 2)];
        var chunks = ChunkLookup.Chunks(map);
        lock (SyncLock(game))
        {
            for (var i = 0; i < MaxCommits && game.compactedClientChunks.Count > 0; i++)
                if (chunks.TryGetValue(game.compactedClientChunks.Dequeue(), out var packed)) _ = packed?.TryCommitPackAndFree();
            if (_compactable!(__instance).Count == 0) Scan(__instance, map, chunks, ((int)at.X, (int)at.Y, (int)at.Z), ttl);
            _ratio!(__instance) = chunks.Count > 0 ? (float)_compressed!(__instance) / chunks.Count : 0;
        }

        return false;
    }

    // The engine's walk: packed ones counted, the player's own column kept fresh, the rest past their time queued
    private static void Scan(object system, ClientWorldMap map, Dictionary<long, ClientChunk> chunks, (int X, int Y, int Z) at, int ttl)
    {
        var (queue, compressed) = (_compactable!(system), 0);
        var (own, pos) = (new Vec3i(at.X, at.Y, at.Z) / map.ClientChunkSize, new Vec3i());
        foreach (var (key, chunk) in chunks.Bounded(MaxChunks))
        {
            if (chunk.IsPacked())
            {
                compressed++;
                continue;
            }

            if (Environment.TickCount - chunk.lastReadOrWrite <= ttl || Centre(chunk) is null || Edge(chunk) is null) continue;
            MapUtil.PosInt3d(key, map.index3dMulX, map.index3dMulZ, pos);
            if (Math.Abs(own.X - pos.X) < 2 && Math.Abs(own.Z - pos.Z) < 2 && !chunk.Empty) chunk.MarkFresh();
            else queue.Enqueue(key);
        }

        _compressed!(system) = compressed;
    }

    private static void Shown(object system, ClientMain game)
    {
        var debug = game.extendedDebugInfo; // every frame: one string each, as the engine's
        game.DebugScreenInfo["compactqueuesize"] = debug ? "Client Chunks in compact queue: " + _compactable!(system).Count : "";
        game.DebugScreenInfo["compactratio"] = debug
            ? "Client chunk compression ratio: " + (_ratio!(system) * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "";
    }
}

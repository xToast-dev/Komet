using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Chunks;

// Singleplayer chunk packets take the network path: the client has no direct handler for chunks (10) and map chunks
// (17), so the built-in server serializes each packet, clones it into a new array (often on the large object heap) and
// queues it on the in-memory connection, and the client's network thread reads it back into new arrays for every chunk:
// ~70 MB/s of a load's garbage on both sides, and the client held its own copy of every chunk's packed bytes. Here the
// server queues a one-byte token in the same queue instead, under its lock, so packets keep their order; the client's
// read takes the packet the token stands for and handles it as the engine does in the background (both ids always are).
// The packet's arrays are the server's (its packed chunk bytes are never changed in place: a pack makes new ones), the
// chunk list is copied (the server reuses its array). Block exchanges (58) and single set blocks (7) take the same way:
// leaf, grass and snow updates send one packet a block, thousands a second in an old world, each serialized and read back;
// their packets are made fresh for each send, and the client's handler runs on the main thread as the engine's read
// queues it. Any other packet, or another connection, goes the engine's way.
internal static class DirectChunks
{
    private const int ChunksId = 10, MapChunkId = 17, SetBlockId = 7, ExchangeBlockId = 58;

    // The engine's task codes, made once
    private const string SetBlockCode = "readpacket7", ExchangeBlockCode = "readpacket58";

    private static readonly ConditionalWeakTable<byte[], Packet_Server> Handed = [];

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static long Sent => Interlocked.Read(ref _sent);

    private static long _sent;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "network")]
    private static extern ref DummyNetwork? Network(DummyNetConnection connection);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ClientReceiveBuffer")]
    private static extern ref Queue<object>? Buffer(DummyNetwork network);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ClientReceiveBufferLock")]
    private static extern ref MonitorObject? BufferLock(DummyNetwork network);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain? Game(ClientSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ProcessInBackground")]
    private static extern bool Background(SystemNetworkProcess process, Packet_Server packet);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain? TaskGame(ProcessPacketTask task);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packet")]
    private static extern ref Packet_Server? TaskPacket(ProcessPacketTask task);

    internal static System.Reflection.MethodInfo? Send() =>
        AccessTools.DeclaredMethod(typeof(ServerMain), nameof(ServerMain.SendPacket), [typeof(int), typeof(Packet_Server)]);

    internal static System.Reflection.MethodInfo? Read() =>
        AccessTools.DeclaredMethod(typeof(SystemNetworkProcess), nameof(SystemNetworkProcess.TryReadPacket), [typeof(byte[]), typeof(int)]);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var (send, read) = (Send(), Read());
        if (!NotNull(harmony) || send is null || read is null || !Present()) return;
        _ = harmony.Patch(read, new HarmonyMethod(typeof(DirectChunks), nameof(Reading)));
        _ = harmony.Patch(send, new HarmonyMethod(typeof(DirectChunks), nameof(Sending)));
        Installed = true;
    }

    // Every member the accessors expect, typed: a missing one would throw at its first use on a game thread
    private static bool Present() =>
        AccessTools.DeclaredField(typeof(DummyNetConnection), "network")?.FieldType == typeof(DummyNetwork) &&
        AccessTools.DeclaredField(typeof(DummyNetwork), "ClientReceiveBuffer")?.FieldType == typeof(Queue<object>) &&
        AccessTools.DeclaredField(typeof(DummyNetwork), "ClientReceiveBufferLock")?.FieldType == typeof(MonitorObject) &&
        AccessTools.DeclaredField(typeof(ClientSystem), "game")?.FieldType == typeof(ClientMain) &&
        AccessTools.DeclaredMethod(typeof(SystemNetworkProcess), "ProcessInBackground", [typeof(Packet_Server)])?.ReturnType == typeof(bool) &&
        AccessTools.DeclaredField(typeof(ProcessPacketTask), "game")?.FieldType == typeof(ClientMain) &&
        AccessTools.DeclaredField(typeof(ProcessPacketTask), "packet")?.FieldType == typeof(Packet_Server);

    // ServerMain.SendPacket(int, Packet_Server) prefix: false when the token went in its place
    private static bool Sending(ServerMain __instance, int clientId, Packet_Server packet)
    {
        if (!Enabled || !Installed || packet is not { Id: ChunksId or MapChunkId or SetBlockId or ExchangeBlockId } || __instance.Clients is not { } clients ||
            !clients.TryGetValue(clientId, out var client) || client is not { IsSinglePlayerClient: true, Socket: DummyNetConnection dummy } ||
            Network(dummy) is not { } network || Buffer(network) is not { } queue || BufferLock(network) is not { } gate) return true;
        if (packet.Id == ChunksId && !Copied(packet)) return true;
        var token = new byte[1];
        Handed.AddOrUpdate(token, packet);
        lock (gate) queue.Enqueue(new DummyNetworkPacket { Data = token, Length = token.Length });
        _ = Interlocked.Increment(ref _sent);
        return false;
    }

    // The chunk list in an array of its own: ServerSystemSendChunks fills the same array every tick
    private static bool Copied(Packet_Server packet)
    {
        if (packet.Chunks is not { Chunks: { } chunks } list || list.ChunksCount < 0 || list.ChunksCount > chunks.Length) return false;
        var own = new Packet_ServerChunk[list.ChunksCount];
        Array.Copy(chunks, own, own.Length);
        list.SetChunks(own, own.Length, own.Length);
        return true;
    }

    // SystemNetworkProcess.TryReadPacket prefix: a token's packet handled as the engine's read does (chunks in the
    // background, blocks queued for the main thread), else the engine
    private static bool Reading(SystemNetworkProcess __instance, byte[] data, int dataLength)
    {
        if (dataLength != 1 || data is null || !Handed.TryGetValue(data, out var packet)) return true;
        _ = Handed.Remove(data);
        if (Game(__instance) is not { disposed: false } game) return false;
        if (packet.Id is SetBlockId or ExchangeBlockId) Queued(game, packet);
        else _ = Background(__instance, packet); // true for chunks and map chunks: nothing for the main thread
        game.LastReceivedMilliseconds = game.Platform.EllapsedMs;
        return false;
    }

    private static void Queued(ClientMain game, Packet_Server packet)
    {
        var task = new ProcessPacketTask();
        (TaskGame(task), TaskPacket(task)) = (game, packet);
        game.EnqueueMainThreadTask(task.Run, packet.Id == SetBlockId ? SetBlockCode : ExchangeBlockCode);
    }
}

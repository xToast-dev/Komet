using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

namespace Komet.World;

// BlockEntity.MarkDirty(redrawOnClient: true) also queues the block in ServerMain.DirtyBlocks, and the server sends one
// exchange packet per queued entry (every 100 ms, the block id read at send time). A berry bush catching up on load runs
// one growth step per ~3.5 game hours and marks itself on nearly every step: up to ~2500 entries for one block after a
// year away. In an old world that was tens of thousands of identical packets a second while chunks streamed, each one a
// main-thread block exchange, a chunk marked for tessellation and a room-registry event on the client. Before the
// engine's loop, the queue keeps the first entry of each block and skip-player: the packets the duplicates made carried
// the same block id. Entries queued while this runs go behind, as they would.
internal static class DirtyBlocks
{
    private const int MaxDrain = 1 << 20, KeptCapacity = 1 << 14;

    private static readonly HashSet<(int X, int Y, int Z, int W)> Seen = [];
    private static readonly List<Vec4i> Kept = [];

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Duplicates left out, a total (server thread)
    public static long Dropped { get; private set; }

    public static void Install(Harmony harmony)
    {
        (Installed, Dropped) = (false, 0); // a total per world, as EventBusSweep.Swept
        if (!NotNull(harmony) || Target() is not { } target ||
            AccessTools.Field(typeof(ServerSystemBlockSimulation), "server")?.FieldType != typeof(ServerMain)) return;
        _ = harmony.Patch(target, new HarmonyMethod(Handling));
        Installed = true;
    }

    internal static System.Reflection.MethodInfo? Target() =>
        AccessTools.DeclaredMethod(typeof(ServerSystemBlockSimulation), "HandleDirtyAndUpdatedBlocks", []);

    // HandleDirtyAndUpdatedBlocks prefix (server thread)
    private static void Handling(ServerMain ___server)
    {
        if (!Enabled || ___server?.DirtyBlocks is not { IsEmpty: false } queue) return;
        Dropped += Squeeze(queue);
    }

    // The queue's entries now, first of each block and skip-player kept in order; the number left out
    internal static int Squeeze(System.Collections.Concurrent.ConcurrentQueue<Vec4i> queue)
    {
        var count = Math.Min(queue.Count, MaxDrain);
        if (count < 2) return 0;
        Seen.Clear();
        Kept.Clear();
        var taken = 0;
        for (var i = 0; i < Math.Min(count, MaxDrain); i++)
        {
            if (!queue.TryDequeue(out var entry)) break;
            taken++;
            if (entry is not null && Seen.Add((entry.X, entry.Y, entry.Z, entry.W))) Kept.Add(entry);
        }

        var dropped = Math.Max(0, taken - Kept.Count);
        foreach (var entry in Kept.Bounded(MaxDrain)) queue.Enqueue(entry);
        Kept.Clear();
        Seen.Clear();
        // A catch-up burst (up to 2^20 entries, tens of MB) is not kept for the session once it has passed
        if (taken < KeptCapacity && Kept.Capacity > 4 * KeptCapacity)
        {
            Seen.TrimExcess();
            Kept.TrimExcess();
        }

        return dropped;
    }
}

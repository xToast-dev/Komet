using HarmonyLib;

namespace Komet.Diagnostics;

// One walk's result. Never changed after it is published, so the panel reads it on the main thread while the next walk runs.
internal sealed class ModSnapshot
{
    public static readonly ModSnapshot Empty = new();
    public int Mods { get; init; }
    public int PatchedMethods { get; init; }
    public int Owners { get; init; }
    public int Conflicts { get; init; }
    public string?[] ModNames { get; } = new string[ModStats.MaxMods];
    public string?[] ConflictNames { get; } = new string[ModStats.MaxConflicts];
    public (string? Name, int Methods)[] OwnerList { get; } = new (string?, int)[ModStats.MaxOwners];
}

// Walks the mod loader and Harmony's patch registry on a pool thread: on the main thread the first walk paid System.Text.Json's
// warm-up inside HarmonySharedState.GetPatchInfo (PatchInfoSerialization.Deserialize), 16-26 ms in one frame. GetPatchInfo takes
// Harmony's own lock, and ModLoader.Mods no longer changes once the game runs.
internal sealed class ModStats
{
    public const int MaxMods = 24, MaxOwners = 12, MaxConflicts = 12, MaxLoadedMods = 512;
    private const int MaxPatched = 4096, MaxOwnersPerMethod = 64;
    private ModSnapshot _snapshot = ModSnapshot.Empty;
    private Task? _walk;

    public ModSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool Walking => _walk is { IsCompleted: false };
    public bool Walked => !ReferenceEquals(Snapshot, ModSnapshot.Empty);

    // At most one walk at a time; a request while one runs is already answered by it. A failed walk is logged when it fails: the
    // next request may be a whole showing of the panel away.
    public void Request(ICoreClientAPI capi, string ownId)
    {
        if (!Assert(ownId.Length > 0) || !NotNull(capi.ModLoader) || Walking) return;
        _walk = Task.Run(() =>
        {
            if (Walk(capi.ModLoader, ownId) is { } snapshot) Volatile.Write(ref _snapshot, snapshot);
        }).ContinueWith(
            failed => capi.Logger.Warning("Komet HUD: mod walk failed ({0})",
                failed.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    internal static ModSnapshot? Walk(IModLoader loader, string ownId)
    {
        if (!Assert(ownId.Length > 0) || !NotNull(loader)) return null;
        var names = new string?[MaxMods];
        var mods = 0;
        foreach (var info in loader.Mods.Select(mod => mod.Info).Bounded(MaxLoadedMods))
        {
            if (mods < MaxMods) names[mods] = info.Name + " " + info.Version;
            mods++;
        }

        if (!Assert(mods > 0)) return null; // this mod is loaded at the very least

        var byOwner = new Dictionary<string, int>();
        var conflicts = new List<(bool Own, string Text)>();
        var patched = 0;
        foreach (var method in Harmony.GetAllPatchedMethods().Bounded(MaxPatched))
        {
            var patches = Harmony.GetPatchInfo(method);
            if (!NotNull(patches) || patches.Owners.Count == 0) continue;
            patched++;
            foreach (var owner in patches.Owners.Bounded(MaxOwnersPerMethod))
                byOwner[owner] = byOwner.GetValueOrDefault(owner) + 1;
            if (patches.Owners.Count > 1)
                conflicts.Add((patches.Owners.Contains(ownId),
                    $"{method.DeclaringType?.Name}.{method.Name}: {string.Join(", ", patches.Owners)}"));
        }

        if (!Assert(byOwner.ContainsKey(ownId))) return null; // our own patches are registered

        var ranked = byOwner.OrderByDescending(owner => owner.Value).Take(MaxOwners).ToArray();
        conflicts.Sort((a, b) => b.Own.CompareTo(a.Own));
        var snapshot = new ModSnapshot
        { Mods = mods, PatchedMethods = patched, Owners = byOwner.Count, Conflicts = conflicts.Count };
        names.CopyTo(snapshot.ModNames, 0);
        for (var i = 0; i < Math.Min(MaxOwners, ranked.Length); i++)
            snapshot.OwnerList[i] = (ranked[i].Key, ranked[i].Value);
        for (var i = 0; i < Math.Min(MaxConflicts, conflicts.Count); i++) snapshot.ConflictNames[i] = conflicts[i].Text;
        return snapshot;
    }
}

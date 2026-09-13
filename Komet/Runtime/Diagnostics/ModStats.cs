using HarmonyLib;

namespace Komet.Runtime.Diagnostics;

// Walks the mod loader and Harmony's patch registry; only sampled while the mods panel is visible
internal sealed class ModStats
{
    public const int MaxMods = 24, MaxOwners = 12, MaxConflicts = 12, MaxLoadedMods = 512, MaxPatched = 4096, MaxOwnersPerMethod = 64;

    public int Mods { get; private set; }
    public int PatchedMethods { get; private set; }
    public int Owners { get; private set; }
    public int Conflicts { get; private set; }
    public string?[] ModNames { get; } = new string[MaxMods];
    public string?[] ConflictNames { get; } = new string[MaxConflicts];
    public (string? Name, int Methods)[] OwnerList { get; } = new (string?, int)[MaxOwners];

    public void Sample(ICoreClientAPI capi, string ownId)
    {
        if (!Assert(ownId.Length > 0) || !NotNull(capi.ModLoader)) return;
        Array.Clear(ModNames);
        Array.Clear(OwnerList);
        Array.Clear(ConflictNames);
        Mods = 0;
        foreach (var info in capi.ModLoader.Mods.Select(mod => mod.Info).Bounded(MaxLoadedMods))
        {
            if (Mods < MaxMods) ModNames[Mods] = info.Name + " " + info.Version;
            Mods++;
        }
        if (!Assert(Mods > 0)) return;   // this mod is loaded at the very least

        var byOwner = new Dictionary<string, int>();
        var conflicts = new List<(bool Own, string Text)>();
        PatchedMethods = 0;
        foreach (var method in Harmony.GetAllPatchedMethods().Bounded(MaxPatched))
        {
            var patches = Harmony.GetPatchInfo(method);
            if (!NotNull(patches) || patches.Owners.Count == 0) continue;
            PatchedMethods++;
            foreach (var owner in patches.Owners.Bounded(MaxOwnersPerMethod)) byOwner[owner] = byOwner.GetValueOrDefault(owner) + 1;
            if (patches.Owners.Count > 1)
                conflicts.Add((patches.Owners.Contains(ownId), $"{method.DeclaringType?.Name}.{method.Name}: {string.Join(", ", patches.Owners)}"));
        }
        if (!Assert(byOwner.ContainsKey(ownId))) return;   // our own patches are registered

        var ranked = new List<(string Name, int Methods)>(byOwner.Count);
        foreach (var (name, methods) in byOwner.Bounded(MaxPatched)) ranked.Add((name, methods));
        ranked.Sort((a, b) => b.Methods.CompareTo(a.Methods));
        Owners = ranked.Count;
        for (var i = 0; i < Math.Min(MaxOwners, ranked.Count); i++) OwnerList[i] = ranked[i];

        conflicts.Sort((a, b) => b.Own.CompareTo(a.Own));
        Conflicts = conflicts.Count;
        for (var i = 0; i < Math.Min(MaxConflicts, conflicts.Count); i++) ConflictNames[i] = conflicts[i].Text;
    }
}

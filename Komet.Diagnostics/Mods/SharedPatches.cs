namespace Komet.Diagnostics;

// Once a world is in, off the main thread: the client log names every engine method Komet patches together with another
// mod, the other mods and the risk (HarmonyAudit.Rate), so a player's log shows a clash without the debug protocol
internal static class SharedPatches
{
    private const int MaxLogged = 24;

    public static void Log(IModLoader? loader, ILogger? logger, string own)
    {
        if (loader is null || logger is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                var lines = Lines(HarmonyAudit.Walk(loader), own);
                if (lines.Count == 0) return;

                logger.Notification("Komet: {0} methods are patched by Komet and other mods too:\n  {1}", lines.Count,
                    string.Join("\n  ", lines.Take(MaxLogged)) +
                    (lines.Count > MaxLogged ? "\n  … (all in .komet debug → harmony.txt)" : ""));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                logger.Warning("Komet: could not list the methods shared with other mods: {0}", e.Message);
            }
        });
    }

    // Riskiest first (Walk's order); owners whose id begins with own's are Komet's (its bench, mounted features)
    internal static List<string> Lines(HarmonyAudit audit, string own)
    {
        var lines = new List<string>();
        foreach (var method in audit.Methods.Bounded(HarmonyAudit.MaxMethods))
        {
            var owners = method.Chain.Select(static l => l.Owner).Distinct().ToArray();
            var others = owners.Where(o => !o.StartsWith(own, StringComparison.Ordinal)).ToArray();
            if (others.Length == 0 || others.Length == owners.Length) continue;

            lines.Add($"{method.Name} [{method.Risk}] with {string.Join(", ", others.Select(o => Named(audit, o)))}");
        }

        return lines;
    }

    private static string Named(HarmonyAudit audit, string owner) =>
        audit.Owners.FirstOrDefault(o => o.Owner == owner).Mod is { Length: > 0 } mod
            ? $"{owner} ({mod})"
            : owner;
}

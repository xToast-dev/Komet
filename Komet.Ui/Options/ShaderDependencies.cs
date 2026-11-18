namespace Komet.Options;

internal static class ShaderDependencies
{
    internal const int MaxMods = 512;
    private const int MaxDepth = 8;

    // Validate the complete graph before any mod's state changes; dependencies precede their users.
    internal static bool Plan(string root, System.Func<string, string[]> dependencies, out string[] order,
        out string why)
    {
        order = [];
        why = "";
        var active = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var pending = new Stack<(string Id, int Depth, bool Ready)>();
        pending.Push((root, 0, false));
        for (var i = 0; i < MaxMods * (MaxMods + 2) && pending.Count > 0; i++)
        {
            var (id, depth, ready) = pending.Pop();
            if (ready)
            {
                _ = active.Remove(id);
                _ = done.Add(id);
                result.Add(id);
                continue;
            }

            if (done.Contains(id)) continue;

            if (!active.Add(id)) return Refused("dependency cycle at " + id, out why);

            if (active.Count + done.Count > MaxMods || depth > MaxDepth)
            {
                return Refused("dependency limit reached at " + id, out why);
            }

            var required = dependencies(id);
            if (required.Length > MaxMods) return Refused("too many dependencies for " + id, out why);

            pending.Push((id, depth, true));
            for (var n = 0; n < Math.Min(required.Length, MaxMods); n++)
            {
                pending.Push((required[required.Length - 1 - n], depth + 1, false));
            }
        }

        if (pending.Count > 0) return Refused("dependency traversal limit reached", out why);

        order = [.. result];
        return true;
    }

    private static bool Refused(string reason, out string why)
    {
        why = reason;
        return false;
    }
}

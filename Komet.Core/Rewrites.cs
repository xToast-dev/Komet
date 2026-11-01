using System.Reflection;

namespace Komet.Core;

// The engine methods a feature's transpiler rewrote, one entry per method: Harmony runs a transpiler again whenever
// another patch lands on the same method, so a plain counter grew past the number of methods
internal sealed class Rewrites
{
    private readonly HashSet<MethodBase> _done = [];

    public int Count
    {
        get
        {
            lock (_done) return _done.Count;
        }
    }

    public void Clear()
    {
        lock (_done) _done.Clear();
    }

    // A re-run that finds no site takes the method out again
    public void Mark(MethodBase? original, bool rewritten)
    {
        if (original is null) return;
        lock (_done)
        {
            if (rewritten) _ = _done.Add(original);
            else _ = _done.Remove(original);
        }
    }
}

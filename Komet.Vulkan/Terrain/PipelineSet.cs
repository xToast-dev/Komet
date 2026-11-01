namespace Komet.Vulkan;

// Pipelines by key, built on a builder (TerrainPipeline.Started) and awaited by the draw that asks first: a draw left out
// would be missing from the picture. Builds begun ahead (Start, Warm) count from the next frame's first ask (Poll), so a
// frame's draws of one key never change pipeline mid-way. Forgotten ones go on their frames' fences (VulkanFrame.Done).
// Engine's thread only; builders hand over through Build.
internal sealed class PipelineSet<TKey>(VulkanDevice device, VulkanFrame frame, ILogger? logger, int max) : IDisposable
    where TKey : notnull
{
    private const int MaxRetired = 1 << 16;

    private readonly Dictionary<TKey, TerrainPipeline?> _made = [];
    private readonly Queue<TKey> _order = []; // _made's keys as they came, each once: Forget drops its keys (Compact)
    private readonly Dictionary<TKey, (Build<TerrainPipeline> Build, string Name)> _building = [];
    private readonly Dictionary<TKey, System.Func<GlslPort.Ported, Build<TerrainPipeline>>> _recipes = [];
    private readonly List<(TerrainPipeline Pipeline, long Frame)> _retired = [];
    private long _polled = -1;

    public int Count => _made.Count;
    public int Building => _building.Count;
    public int Retired => _retired.Count;
    public long Started { get; private set; } // builds begun ahead of any draw (Start)

    // No recipe lambda: one per draw was garbage on every pool draw
    public bool TryMade(TKey key, out TerrainPipeline? made) => _made.TryGetValue(key, out made);

    // null: cannot be made, or still building after the longest wait (waiting). start: built for a port, kept so Warm can
    // redo it for another
    public TerrainPipeline? Get(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start, out bool waiting)
    {
        waiting = false;
        if (Asked(key, program, start, out var made, out var building)) return made;
        if (building.Build.Awaited(Hitches.Kind.Pipeline)) return Taken(key, building);
        waiting = true;
        return null;
    }

    // Made (made null: refused), or started and left building: never waits, so a check ahead of the draw never stalls
    public bool Ready(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start, out TerrainPipeline? made) =>
        Asked(key, program, start, out made, out _);

    // True with made (null: cannot be made) when there is an answer; false with the build still running
    private bool Asked(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start, out TerrainPipeline? made,
        out (Build<TerrainPipeline> Build, string Name) building)
    {
        building = default;
        if (_made.TryGetValue(key, out made)) return true;
        if (!NotNull(start)) return true;
        Poll();
        if (_made.TryGetValue(key, out made)) return true;
        if (_building.TryGetValue(key, out building)) return false;
        _building[key] = building = (start(program.Ported), program.Name);
        if (_recipes.Count < 2 * max) _recipes[key] = start;
        if (!Builds.Waited || !building.Build.Done) return false;
        made = Taken(key, building); // made in place: no draw waited for it
        return true;
    }

    // Made, refused or building
    public bool Knows(TKey key) => _made.ContainsKey(key) || _building.ContainsKey(key);

    // Begins a pipeline an earlier start used before any draw asks; a full set starts none.
    public void Start(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start)
    {
        if (!NotNull(start) || Knows(key) || _building.Count + _made.Count >= max) return;
        var building = _building[key] = (start(program.Ported), program.Name);
        if (_recipes.Count < 2 * max) _recipes[key] = start;
        Started++;
        if (Builds.Waited && building.Build.Done) _ = Taken(key, building);
    }

    // Builds done by now count as made, all at once, at the frame's first ask
    private void Poll()
    {
        if (_polled == frame.Number) return;
        _polled = frame.Number;
        _ = Assert(_building.Count <= max); // Asked and Warm do not cap it: returning here would never drain it nor Collect
        Collect();
        if (_building.Count == 0 || !AnyDone()) return; // most frames while builds run: no copy of the set
        foreach (var (key, building) in _building.Where(b => b.Value.Build.Done).ToArray().Bounded(max))
            _ = Taken(key, building);
        if (_building.Count == 0) device.KeepCache(); // what was asked for is made: on disk before a crash can lose it
    }

    // Without a copy or boxed enumerator: asked each frame while pipelines build
    private bool AnyDone()
    {
        foreach (var (build, _) in _building.Values.Bounded(max))
            if (build.Done)
                return true;
        return false;
    }

    private TerrainPipeline? Taken(TKey key, (Build<TerrainPipeline> Build, string Name) building)
    {
        _ = _building.Remove(key);
        var made = building.Build.Take(out var why);
        if (made is null) logger?.Warning("Komet: no Vulkan pipeline for {0}: {1}", building.Name, why);
        if (_made.Count >= max) Evict();
        if (_order.Count >= 4 * max) Compact();
        _made[key] = made;
        _order.Enqueue(key);
        return made;
    }

    // Full: the oldest goes, rather than unkept keys rebuilding every frame
    private void Evict()
    {
        var count = _order.Count;
        for (var i = 0; i < Math.Min(count, MaxRetired); i++)
        {
            if (!_made.Remove(_order.Dequeue(), out var made)) continue; // forgotten
            if (made is not null) _retired.Add((made, frame.Number)); // the open frame may have recorded it
            return;
        }
    }

    // Drops forgotten keys from the order; made ones stay, oldest first
    private void Compact()
    {
        var kept = _order.Where(_made.ContainsKey).Distinct().ToArray();
        _order.Clear();
        foreach (var key in kept.Bounded(MaxRetired)) _order.Enqueue(key);
    }

    // Rebuilds keys matching which for another port under again's keys, so the program switches with all made. Returns
    // the keys started.
    public TKey[] Warm(System.Func<TKey, bool> which, System.Func<TKey, TKey> again,
        (string Name, GlslPort.Ported Ported) program)
    {
        if (!NotNull(which) || !NotNull(again)) return [];
        var started = new List<TKey>();
        foreach (var (key, recipe) in _recipes.Where(r => which(r.Key)).ToArray().Bounded(2 * max))
        {
            var next = again(key);
            if (_made.ContainsKey(next) || _building.ContainsKey(next) || !Assert(!which(next))) continue;
            var building = _building[next] = (recipe(program.Ported), program.Name);
            if (_recipes.Count < 2 * max) _recipes[next] = recipe;
            if (Builds.Waited && building.Build.Done) _ = Taken(next, building); // made in place
            started.Add(next);
        }

        return [.. started];
    }

    // All made (or refused), as this frame counts them
    public bool Made(TKey[] keys)
    {
        if (!NotNull(keys)) return false;
        Poll();
        foreach (var key in keys.Bounded(2 * max))
            if (_building.ContainsKey(key))
                return false;
        return Assert(_building.Count <= max);
    }

    // Pipelines go once no frame in flight may use them; builds dropped
    public void Forget(System.Func<TKey, bool> which)
    {
        if (!NotNull(which)) return;
        var forgotten = _made.Where(p => which(p.Key)).ToArray();
        foreach (var (key, made) in forgotten.Bounded(max))
        {
            _ = _made.Remove(key);
            if (made is not null) _retired.Add((made, frame.Number));
        }

        // A key made again would queue twice, and Evict would take the new pipeline for the stale entry
        if (forgotten.Length > 0) Compact();

        foreach (var (key, (build, _)) in _building.Where(p => which(p.Key)).ToArray().Bounded(max))
        {
            build.Drop();
            _ = _building.Remove(key);
        }

        foreach (var key in _recipes.Keys.Where(which).ToArray().Bounded(2 * max)) _ = _recipes.Remove(key);
        Collect();
    }

    private void Collect()
    {
        var done = frame.Done;
        var kept = 0;
        var count = Math.Min(_retired.Count, MaxRetired);
        for (var i = 0; i < Math.Min(count, MaxRetired); i++)
            if (_retired[i].Frame <= done) _retired[i].Pipeline.Dispose();
            else _retired[kept++] = _retired[i];
        _retired.RemoveRange(kept, count - kept);
    }

    // After GPU idle; running builds destroy what they make
    public void Dispose()
    {
        foreach (var (build, _) in _building.Values.ToArray().Bounded(max)) build.Drop();
        foreach (var made in _made.Values.ToArray().Bounded(max)) made?.Dispose();
        foreach (var (retired, _) in _retired.Bounded(MaxRetired)) retired.Dispose();
        _building.Clear();
        _made.Clear();
        _order.Clear();
        _recipes.Clear();
        _retired.Clear();
    }
}

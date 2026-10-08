namespace Komet.Vulkan;

// A renderer's pipelines by key. One a draw asks for first is made on a builder (TerrainPipeline.Started): until it is done
// the draw is told to wait and stays OpenGL's, as any draw Vulkan does not take, except where it must not (wait: the draw
// would be lost), which waits for it. A finished build counts as made only from the next frame's first ask on (Poll): a
// frame never turns from OpenGL's draws of a pipeline to Vulkan's halfway, which could differ where one draw tests its
// depth against another's. Forgotten pipelines go once the frames that may have recorded them are done (the frame slot's
// fence, VulkanFrame.Done), not after the GPU went idle. The engine's thread only; the builders hand their results over
// through Build.
internal sealed class PipelineSet<TKey>(VulkanDevice device, VulkanFrame frame, ILogger? logger, int max) : IDisposable
    where TKey : notnull
{
    private const int MaxRetired = 1 << 16, LongestSeconds = 30; // a driver that takes longer is hung

    private readonly Dictionary<TKey, TerrainPipeline?> _made = [];
    private readonly Dictionary<TKey, (Build<TerrainPipeline> Build, string Name)> _building = [];
    private readonly Dictionary<TKey, System.Func<GlslPort.Ported, Build<TerrainPipeline>>> _recipes = [];
    private readonly List<(TerrainPipeline Pipeline, long Frame)> _retired = [];
    private long _polled = -1;

    public int Count => _made.Count;
    public int Building => _building.Count;
    public int Retired => _retired.Count;

    // Draws told to wait for their pipeline
    public long Waits { get; private set; }

    // The pipeline (null: none can be made), or null and waiting while it is being made. start makes it for a port: kept,
    // so Warm makes it again for another
    // The made one, without the recipe a miss needs: a lambda per draw was garbage on every pool's draw
    public bool TryMade(TKey key, out TerrainPipeline? made) => _made.TryGetValue(key, out made) && Assert(_made.Count <= max);

    public TerrainPipeline? Get(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start, bool wait, out bool waiting)
    {
        waiting = false;
        if (_made.TryGetValue(key, out var made)) return made;
        if (!NotNull(start) || !Assert(_made.Count <= max)) return null;
        Poll();
        if (_made.TryGetValue(key, out made)) return made;
        if (!_building.TryGetValue(key, out var building))
        {
            _building[key] = building = (start(program.Ported), program.Name);
            if (_recipes.Count < 2 * max) _recipes[key] = start;
            if (Builds.Waited && building.Build.Done) return Taken(key, building); // made in place: no draw waited for it
        }

        if (wait)
        {
            var at = Hitches.Now;
            _ = building.Build.Wait(TimeSpan.FromSeconds(LongestSeconds));
            Hitches.Since(Hitches.Kind.Pipeline, at);
            if (building.Build.Done) return Taken(key, building);
        }

        (waiting, Waits) = (true, Waits + 1);
        return null;
    }

    // The builds done by now count as made, all at once, at the frame's first ask
    private void Poll()
    {
        if (_polled == frame.Number || !Assert(_building.Count <= max) || !Assert(frame.Number > _polled)) return;
        _polled = frame.Number;
        Collect();
        if (_building.Count == 0) return;
        foreach (var (key, building) in _building.Where(b => b.Value.Build.Done).ToArray().Bounded(max))
            _ = Taken(key, building);
        if (_building.Count == 0) device.KeepCache(); // what was asked for is made: on disk before a crash can lose it
    }

    private TerrainPipeline? Taken(TKey key, (Build<TerrainPipeline> Build, string Name) building)
    {
        _ = _building.Remove(key) && Assert(building.Build.Done) && Assert(!_made.ContainsKey(key));
        var made = building.Build.Take(out var why);
        if (made is null) logger?.Warning("Komet: no Vulkan pipeline for {0}: {1}", building.Name, why);
        if (_made.Count < max) _made[key] = made;
        else if (made is not null) _retired.Add((made, frame.Number)); // used by this draw, kept by none
        return made;
    }

    // The pipelines of the keys which picks, made again for another port under the keys again gives them: a program
    // switches over to that port with every pipeline it drew with made. The keys started.
    public TKey[] Warm(System.Func<TKey, bool> which, System.Func<TKey, TKey> again,
        (string Name, GlslPort.Ported Ported) program)
    {
        if (!NotNull(which) || !NotNull(again) || !Assert(_recipes.Count <= 2 * max)) return [];
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

    // Every one of the keys made (or refused), as this frame counts them
    public bool Made(TKey[] keys)
    {
        if (!NotNull(keys)) return false;
        Poll();
        foreach (var key in keys.Bounded(2 * max))
            if (_building.ContainsKey(key))
                return false;
        return Assert(_building.Count <= max);
    }

    // The keys' pipelines go once no frame in flight may use them; their builds are dropped (one not begun never is)
    public void Forget(System.Func<TKey, bool> which)
    {
        if (!NotNull(which) || !Assert(_made.Count <= max)) return;
        foreach (var (key, made) in _made.Where(p => which(p.Key)).ToArray().Bounded(max))
        {
            _ = _made.Remove(key);
            if (made is not null) _retired.Add((made, frame.Number)); // the open frame may have recorded it
        }

        foreach (var (key, (build, _)) in _building.Where(p => which(p.Key)).ToArray().Bounded(max))
        {
            build.Drop();
            _ = _building.Remove(key);
        }

        foreach (var key in _recipes.Keys.Where(which).ToArray().Bounded(2 * max)) _ = _recipes.Remove(key);
        Collect();
    }

    // The retired pipelines of frames the GPU is done with
    private void Collect()
    {
        var done = frame.Done;
        var kept = 0;
        var count = Math.Min(_retired.Count, MaxRetired);
        for (var i = 0; i < Math.Min(count, MaxRetired); i++)
            if (_retired[i].Frame <= done) _retired[i].Pipeline.Dispose();
            else _retired[kept++] = _retired[i];
        _retired.RemoveRange(kept, count - kept);
        _ = Assert(_retired.Count <= MaxRetired) && Assert(kept <= count);
    }

    // After the GPU went idle; builds still running destroy what they make
    public void Dispose()
    {
        _ = Assert(_made.Count <= max) && Assert(_retired.Count <= MaxRetired);
        foreach (var (build, _) in _building.Values.ToArray().Bounded(max)) build.Drop();
        foreach (var made in _made.Values.ToArray().Bounded(max)) made?.Dispose();
        foreach (var (retired, _) in _retired.Bounded(MaxRetired)) retired.Dispose();
        _building.Clear();
        _made.Clear();
        _recipes.Clear();
        _retired.Clear();
    }
}

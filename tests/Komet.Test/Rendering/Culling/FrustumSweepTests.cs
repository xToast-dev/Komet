using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// Golden test: the engine's ModelDataPoolLocation.IsVisible decides, Komet's sweep has to agree on every location, in order
public sealed class FrustumSweepTests
{
    private const int Locations = 777, Seeds = 25; // not a multiple of any vector width
    private const string Sweep = "Komet.Rendering.FrustumSweep, Komet.Rendering";

    private static FrustumCulling Culler(Random r)
    {
        double[] eye = [500 + r.NextDouble() * 100, 100 + r.NextDouble() * 50, 500 + r.NextDouble() * 100];
        return Culler(eye,
            [eye[0] + r.NextDouble() - 0.5, eye[1] + (r.NextDouble() - 0.5) / 4, eye[2] + r.NextDouble() - 0.5]);
    }

    private static FrustumCulling Culler(double[] eye, double[] center)
    {
        var culler = new FrustumCulling
            { lod0BiasSq = 200f * 200f, lod2BiasSq = 300.0 * 300.0, shadowRangeX = 150, shadowRangeZ = 120 };
        culler.UpdateViewDistance(512);
        var projection = Mat4d.Perspective(Mat4d.Create(), 1.2, 16.0 / 9, 0.1, 1000);
        var view = Mat4d.LookAt(Mat4d.Create(), eye, center, [0.0, 1.0, 0.0]);
        culler.CalcFrustumEquations(new BlockPos((int)eye[0], (int)eye[1], (int)eye[2]), projection, view);
        return culler;
    }

    private static ModelDataPoolLocation Location(Random r, int index) =>
        new()
        {
            FrustumCullSphere = new Sphere(r.Next(200, 900) + r.NextSingle(), r.Next(0, 250) + r.NextSingle(),
                r.Next(200, 900) + r.NextSingle(), r.Next(1, 33), r.Next(1, 33), r.Next(1, 33)),
            IndicesStart = index * 600,
            IndicesEnd = index * 600 + r.Next(3, 600),
            LodLevel = r.Next(0, 5),
            Hide = r.Next(10) == 0,
            CullVisible = new Bools(r.Next(4) != 0, r.Next(4) != 0)
        };

    // A pool is only sorted into the grid once it has gone several culls without a change, so the golden test has to cull often
    // enough to reach both layouts: the first pass runs on the engine's order, the last on the grid.
    private static void AssertSameAsEngine(FrustumCulling culler, EnumFrustumCullMode mode,
        List<ModelDataPoolLocation> list, int slot = 0, int passes = 12, string? message = null,
        System.Func<ModelDataPoolLocation, bool>? casts = null)
    {
        int[] starts = new int[2 * list.Count], sizes = new int[list.Count];
        int groups = 0, rendered = 0, allocated = 0;
        for (var pass = 0; pass < passes; pass++)
        {
            groups = FrustumSweep.Cull(culler, mode, list, slot, starts, sizes, out rendered, out allocated);
        }

        var expected = list.Where(l => l.IsVisible(mode, culler) && (casts?.Invoke(l) ?? true)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(groups, Is.EqualTo(expected.Count), message);
            Assert.That(starts.Where((_, i) => i % 2 == 0).Take(Math.Max(0, groups)),
                Is.EqualTo(expected.Select(l => l.IndicesStart * 4)));
            Assert.That(sizes.Take(Math.Max(0, groups)),
                Is.EqualTo(expected.Select(l => l.IndicesEnd - l.IndicesStart)));
            Assert.That(rendered, Is.EqualTo(expected.Sum(l => (l.IndicesEnd - l.IndicesStart) / 3)));
            Assert.That(allocated, Is.EqualTo(list.Sum(l => (l.IndicesEnd - l.IndicesStart) / 3)));
        });
    }

    [TearDown]
    public void Restore()
    {
        Counting.Hud = false;
        ModelDataPoolLocation.VisibleBufIndex = 0;
    }

    private static Counters Count()
    {
        Counting.Hud = true;
        FrustumSweep.ResetPeaks();
        return Counters.Now();
    }

    [TestCase(EnumFrustumCullMode.CullNormal)]
    public void MatchesEngine(EnumFrustumCullMode mode)
    {
        var start = Count();
        for (var seed = 0; seed < Seeds; seed++)
        {
            var r = new Random(seed);
            var culler = Culler(r);
            var list = Enumerable.Range(0, Locations).Select(i => Location(r, i)).ToList();
            ModelDataPoolLocation.VisibleBufIndex = seed % 2;
            AssertSameAsEngine(culler, mode, list);

            list.RemoveAt(r.Next(list.Count)); // same count, other content: the mirror has to notice
            list.Insert(r.Next(list.Count), Location(r, Locations + seed));
            AssertSameAsEngine(culler, mode, list);
        }

        // Without this the grid could quietly stop being built and every case above would pass on the engine's order. The diff keeps
        // the grid, so the second list is checked on the sorted layout with the new row behind it.
        var grown = start.Since();
        Assert.Multiple(() =>
        {
            Assert.That(grown.Settles, Is.EqualTo(Seeds));
            Assert.That(grown.Diffed, Is.EqualTo(Seeds));
            Assert.That(grown.Rebuilds, Is.EqualTo(2 * Seeds));
        });
    }

    // Install turns stage batches on with the frame hooks; these tests end frames by hand
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_staging")]
    private static extern ref bool Staging([UnsafeAccessorType(Sweep)] object? sweep);

    // The same walk with every Render call culled on Komet's worker pool first, as MeshDataPoolManager.Render's prefix does in game:
    // call by call; or a stage at a time, each stage's first call handing every manager the stage called last frame to the workers at
    // once and each call taking its own pools out of that batch; or with stages whose managers come in another order each frame,
    // some left out, so that a call the batch does not hold ends it and culls alone and the next frame plans from what came. The
    // engine has to agree on every pool, and the workers (or the batches) must have culled more than `least` calls.
    [TestCase(9, 7, 600, false, false, 1000, TestName = "TheWorkersMatchTheEngineWhileStreaming")]
    [Category("Slow")]
    public void TheWorkersMatchTheEngine(int seed, int radius, int frames, bool staging, bool shuffled, int least)
    {
        FrustumSweep.Clear();

        long Calls() => staging ? FrustumSweep.StagedCalls : FrustumSweep.ParallelCalls;

        var (threshold, calls) = (FrustumSweep.ParallelRows, Calls());
        (FrustumSweep.ParallelRows, Staging(null), Counting.Hud) = (0, staging, staging);
        WorkerPool.Resize(null, 4);
        try
        {
            var failure = new Walk(seed, radius) { Parallel = true, Shuffled = shuffled }.Run(0, frames);
            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Null);
                Assert.That(Calls() - calls, Is.GreaterThan(least), "the workers culled");
            });
        }
        finally
        {
            FrustumSweep.EndFrame();
            (FrustumSweep.ParallelRows, Staging(null)) = (threshold, false);
            _ = WorkerPool.Stop();
        }
    }

    // Diff against changes the engine never makes: rows moved, the list reordered, whole runs replaced. What it cannot follow it reads
    // again or hands to a full rebuild. Without the OnFrame postfix the relayout budget is spent once and never refills.
    [TestCase(true)]
    public void MatchesEngineAfterAnyChange(bool ticks)
    {
        FrustumSweep.Clear();
        var start = Count();
        var r = new Random(12);
        var list = Enumerable.Range(0, Locations).Select(i => Location(r, i)).ToList();
        var next = Locations;
        var modes = Enum.GetValues<EnumFrustumCullMode>().Where(m => m != EnumFrustumCullMode.NoCull).ToArray();
        for (var step = 0; step < 400; step++)
        {
            switch (r.Next(6))
            {
                case 0: // a strip removed
                    list.RemoveRange(r.Next(list.Count / 2), r.Next(1, 40));
                    break;
                case 1: // squeezed in, one here and there
                    for (var k = r.Next(1, 6); k > 0; k--)
                    {
                        list.Insert(r.Next(list.Count + 1), Location(r, next++));
                    }

                    break;
                case 2: // moved
                    var at = r.Next(list.Count);
                    var moved = list[at];
                    list.RemoveAt(at);
                    list.Insert(r.Next(list.Count + 1), moved);
                    break;
                case 3: // appended
                    list.AddRange(Enumerable.Range(0, r.Next(1, 30)).Select(_ => Location(r, next++)));
                    break;
                case 4: // mostly new
                    if (r.Next(8) == 0)
                    {
                        list =
                        [
                            .. list.Take(r.Next(list.Count / 4))
                                .Concat(Enumerable.Range(next, Locations).Select(id => Location(r, id)))
                        ];
                        next += Locations;
                    }

                    break;
            }

            if (list.Count > 1500) list.RemoveRange(0, list.Count - Locations);

            if (ticks && step % 7 == 0) FrustumSweep.NextFrame();

            var culler = Culler(r);
            ModelDataPoolLocation.VisibleBufIndex = r.Next(2);
            // sometimes long enough to settle into the grid
            for (var cull = r.Next(1, 12); cull > 0; cull--)
            {
                var mode = modes[r.Next(modes.Length)];
                AssertSameAsEngine(culler, mode, list, 7, 1, $"step {step}, {mode}");
            }
        }

        var grown = start.Since();
        TestContext.Out.WriteLine(
            $"{grown.Rebuilds} rebuilds, {grown.Diffed} diffs, {grown.Relayouts} relayouts, {grown.Settles} settles");
        Assert.That(grown.Diffed, Is.InRange(1, grown.Rebuilds - 1), "both paths taken");
        if (!ticks) Assert.That(grown.Relayouts, Is.InRange(1, 4), "the budget of the one frame there was");
    }

    // The sweep's totals only grow: a test takes where they stood when it began counting, and the growth since
    private readonly record struct Counters(long Rebuilds, long Diffed, long Relayouts, long Settles, long Skipped)
    {
        public static Counters Now() =>
            new(FrustumSweep.Rebuilds, FrustumSweep.Diffed, SweepRelayouts(),
                FrustumSweep.Settles, FrustumSweep.Skipped);

        public Counters Since() =>
            new(FrustumSweep.Rebuilds - Rebuilds, FrustumSweep.Diffed - Diffed,
                SweepRelayouts() - Relayouts, FrustumSweep.Settles - Settles, FrustumSweep.Skipped - Skipped);

        private static long SweepRelayouts() => Interlocked.Read(ref RelayoutCount(null));

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_relayouts")]
        private static extern ref long RelayoutCount([UnsafeAccessorType(Sweep)] object? sweep);
    }

    // A player walking through chunk columns, on the engine's pools. A section is tesselated into a mesh per pass and LOD, added through
    // MeshDataPoolManager.AddModel with LodLevel and CullVisible set right after, as TesselatedChunkPart does; a neighbour arriving
    // re-tesselates the edge part of the section beside it, and a column leaving the radius goes through RemoveDataPoolLocations.
    private sealed class Walk
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
        private static extern ref List<ModelDataPoolLocation> PoolLocations(MeshDataPool pool);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
        private static extern ref List<MeshDataPool> Pools(MeshDataPoolManager manager);

        private const int Chunk = 32, Passes = 3, MaxParts = 600, Height = 3;
        private static readonly double[] Chance = [1.0, 0.5, 0.25];
        private readonly Dictionary<(int X, int Z), Section[]> _columns = [];
        private readonly FrustumCulling _culler = new();
        private readonly MeshDataPoolManager[] _managers = new MeshDataPoolManager[Passes];

        private readonly MeshDataPoolMasterManager _master =
            new(Answers.NullClient) { DelayedPoolLocationRemoval = true };

        private readonly MeshData _mesh = new(false) { xyz = new float[3 * 4 * 160], Indices = new int[6 * 160] };
        private readonly Queue<Section> _pending = new();
        private readonly Random _r;
        private readonly int _radius;
        private readonly int[] _starts = new int[2 * MaxParts], _sizes = new int[MaxParts];

        private readonly Occlusion.Range[] _swept = new Occlusion.Range[MaxParts],
            _walked = new Occlusion.Range[MaxParts];

        private (int X, int Z) _at;
        private double _x, _z, _heading;

        public Walk(int seed, int radius)
        {
            (_r, _radius) = (new Random(seed), radius);
            for (var p = 0; p < Passes; p++)
            {
                _managers[p] = new MeshDataPoolManager(_master, _culler, Answers.NullClient, 120000, 180000, MaxParts);
            }

            _culler.UpdateViewDistance(radius * Chunk);
            (_x, _z, _heading) = (512000.5, 512000.5, _r.NextDouble() * 2 * Math.PI);
            _at = ((int)Math.Floor(_x / Chunk), (int)Math.Floor(_z / Chunk));
            Reach();
            while (_pending.Count > 0)
            {
                Tesselate(_pending.Dequeue(), false);
            }
        }

        public int Unloads { get; private set; }

        // the OnFrame postfix, which the game installs and a test calls by hand
        public bool Ticks { get; init; } = true;

        public bool Parallel { get; init; } // cull through FrustumSweep.Precull, per manager, before comparing

        // and each stage calls its managers in another order, now and then leaving one out
        public bool Shuffled { get; init; }
        public int PoolCount => _managers.Sum(m => Pools(m).Count);
        public int Rows => _managers.Sum(m => Pools(m).Sum(p => PoolLocations(p).Count));

        private void Step(double speed, int tesselations)
        {
            if (_r.Next(250) == 0) _heading = _r.NextDouble() * 2 * Math.PI;

            (_x, _z) = (_x + Math.Cos(_heading) * speed, _z + Math.Sin(_heading) * speed);
            var at = ((int)Math.Floor(_x / Chunk), (int)Math.Floor(_z / Chunk));
            if (at != _at)
            {
                _at = at;
                Unloads++;
                Reach();
            }

            for (var k = 0; k < tesselations && _pending.Count > 0; k++)
            {
                var section = _pending.Dequeue();
                if (!section.Loaded) continue;

                Tesselate(section, false);
                var next = (section.Column.X + _r.Next(-1, 2), section.Column.Z + _r.Next(-1, 2));
                if (_columns.TryGetValue(next, out var column)) Tesselate(column[_r.Next(column.Length)], true);
            }
        }

        // Crosses one chunk boundary without tesselating what comes into range: the columns left behind unload at once
        public int UnloadStrip()
        {
            var before = _columns.Keys.ToList();
            _at = (_at.X + 1, _at.Z);
            Reach();
            return before.Count(key => !_columns.ContainsKey(key));
        }

        public string? Run(int from, int end, bool moving = true)
        {
            for (var f = from; f < end; f++)
            {
                if (moving) Step(1.3, 8);

                if (Render(f) is { } failure) return failure;
            }

            return null;
        }

        // One frame in the engine's order
        private string? Render(int frame)
        {
            FrustumSweep.EndFrame(); // MainRenderLoop's prefix: no batch lives into the next frame
            Occlusion(frame);
            Shadow(true);
            var failure = Check(EnumFrustumCullMode.CullInstantShadowPassFar);
            Shadow(false);
            failure ??= Check(EnumFrustumCullMode.CullInstantShadowPassNear);
            FrustumSweep.Unbatch(); // the prefix on RemoveLocation, which OnFrame calls for the queued removals
            _master.OnFrame(0.016f, null, null);
            if (Ticks) FrustumSweep.NextFrame();

            Camera(frame);
            failure ??= Check(EnumFrustumCullMode.CullNormal);
            failure ??= Check(EnumFrustumCullMode.CullInstant);
            FrustumSweep.EndFrame(); // MainRenderLoop's postfix: the next step tesselates and unloads
            return failure is null ? null : $"frame {frame}: {failure}";
        }

        private string? Check(EnumFrustumCullMode mode)
        {
            if (Parallel) return CheckParallel(mode);

            foreach (var pool in _managers.SelectMany(m => Pools(m)))
            {
                var groups = FrustumSweep.Cull(_culler, mode, PoolLocations(pool), FrustumSweep.SlotOf(pool), _starts,
                    _sizes, out var rendered, out var allocated);
                if (Compare(mode, pool, (groups, rendered, allocated), true) is { } failure) return failure;
            }

            return null;
        }

        private string? Compare(EnumFrustumCullMode mode, MeshDataPool pool,
            (int Groups, int Rendered, int Allocated) komet,
            bool boxed)
        {
            pool.FrustumCull(_culler, mode);
            var at = $"{mode}, pool {FrustumSweep.SlotOf(pool)}";
            if (komet != (pool.indicesGroupsCount, pool.RenderedTriangles, pool.AllocatedTris))
            {
                return $"{at}: {komet.Groups} ranges, {komet.Rendered} of {komet.Allocated} triangles, the engine " +
                       $"{pool.indicesGroupsCount}, {pool.RenderedTriangles} of {pool.AllocatedTris}";
            }

            for (var i = 0; i < komet.Groups; i++)
            {
                if (_starts[2 * i] != pool.indicesStartsByte[2 * i] || _sizes[i] != pool.indicesSizes[i])
                {
                    return $"{at}: range {i} differs";
                }
            }

            return Boxed(pool, boxed) is { } boxes ? $"{at}: {boxes}" : null;
        }

        // The occlusion culling's boxes from the mirror against those of the walk over the engine's locations: equal to the bit
        // whenever the mirror gives them, and given whenever the mirror wrote the pool's ranges (expected)
        private string? Boxed(MeshDataPool pool, bool expected)
        {
            var camera = (_x, 90.25, _z);
            var swept = FrustumSweep.Boxes(pool, _swept, camera);
            var walked = Komet.Rendering.Occlusion.Match(pool.indicesStartsByte, pool.indicesSizes,
                pool.indicesGroupsCount, PoolLocations(pool), _walked, camera, out var unmatched);
            if (swept < 0) return expected && unmatched == 0 ? "no boxes from the mirror" : null;

            if (swept != walked || unmatched != 0) return $"{swept} boxes from the mirror, {walked} walked";

            for (var i = 0; i < swept; i++)
            {
                if (_swept[i] != _walked[i]) return $"box {i}: {_swept[i]} from the mirror, {_walked[i]} walked";
            }

            return null;
        }

        // Precull writes each pool's own fields; they are copied before the engine's FrustumCull overwrites them
        private string? CheckParallel(EnumFrustumCullMode mode)
        {
            var order =
                Shuffled ? [.. _managers.Where(_ => _r.Next(5) != 0).OrderBy(_ => _r.Next())] : _managers;
            foreach (var manager in order)
            {
                FrustumSweep.Precull(manager, mode);
                foreach (var pool in Pools(manager))
                {
                    var groups = pool.indicesGroupsCount;
                    Array.Copy(pool.indicesStartsByte, _starts, 2 * groups);
                    Array.Copy(pool.indicesSizes, _sizes, groups);
                    if (Compare(mode, pool, (groups, pool.RenderedTriangles, pool.AllocatedTris), false) is { } failure)
                    {
                        return failure;
                    }
                }

                FrustumSweep.Culled();
            }

            return null;
        }

        // ChunkCuller writes the back buffer and swaps; now and then a chunk's meshes are hidden or shown again
        private void Occlusion(int frame)
        {
            var sections = _columns.Values.SelectMany(c => c).ToList();
            if (frame % 64 == 63)
            {
                var back = 1 - ModelDataPoolLocation.VisibleBufIndex;
                foreach (var section in sections)
                {
                    section.Visible[back] = section.Visible[ModelDataPoolLocation.VisibleBufIndex] ^ (_r.Next(10) == 0);
                }

                ModelDataPoolLocation.VisibleBufIndex = back;
            }

            foreach (var section in sections.Where(_ => _r.Next(500) == 0))
            {
                foreach (var location in section.Center.Concat(section.Edge))
                {
                    location.Hide = !location.Hide;
                }
            }
        }

        // Columns come in nearest first, as the client tesselates them, so a pool filled first fit holds a ring around the player
        private void Reach()
        {
            foreach (var key in _columns.Keys.Where(c => !Near(c)).ToList())
            {
                Unload(key);
            }

            var wanted = from dx in Enumerable.Range(-_radius, 2 * _radius + 1)
                from dz in Enumerable.Range(-_radius, 2 * _radius + 1)
                let key = (_at.X + dx, _at.Z + dz)
                where Near(key) && !_columns.ContainsKey(key)
                orderby dx * dx + dz * dz
                select key;
            foreach (var key in wanted.ToList())
            {
                var column = Enumerable.Range(0, Height).Select(y => new Section(key, y, _r)).ToArray();
                _columns[key] = column;
                foreach (var section in column)
                {
                    _pending.Enqueue(section);
                }
            }
        }

        private bool Near((int X, int Z) column) =>
            (column.X - _at.X) * (column.X - _at.X) + (column.Z - _at.Z) * (column.Z - _at.Z) <=
                   _radius * _radius;

        private void Unload((int X, int Z) key)
        {
            foreach (var section in _columns[key])
            {
                section.Loaded = false;
                if (section.Center.Length > 0) _master.RemoveDataPoolLocations(section.Center);

                if (section.Edge.Length > 0) _master.RemoveDataPoolLocations(section.Edge);
            }

            _ = _columns.Remove(key);
        }

        private void Tesselate(Section section, bool edgeOnly)
        {
            for (var part = edgeOnly ? 1 : 0; part < 2; part++)
            {
                var old = part == 0 ? section.Center : section.Edge;
                if (old.Length > 0) _master.RemoveDataPoolLocations(old);

                var made = new List<ModelDataPoolLocation>();
                for (var pass = 0; pass < Passes; pass++)
                {
                    if (_r.NextDouble() >= Chance[pass]) continue;

                    for (var lod = 0; lod < 4; lod++)
                    {
                        if (lod != 1 && _r.Next(3) == 0) continue;

                        var quads = _r.Next(4, 160);
                        (_mesh.VerticesCount, _mesh.IndicesCount) = (4 * quads, 6 * quads);
                        var location = _managers[pass].AddModel(_mesh, section.Origin, 0, section.Bounds);
                        (location.CullVisible, location.LodLevel) = (section.Visible, lod);
                        made.Add(location);
                    }
                }

                if (part == 0)
                {
                    section.Center = [.. made];
                }
                else
                {
                    section.Edge = [.. made];
                }
            }
        }

        private void Camera(int frame)
        {
            var yaw = frame * 0.01;
            double[] eye = [_x, 111.6, _z], center = [_x + Math.Cos(yaw), 111.45, _z + Math.Sin(yaw)];
            _culler.CalcFrustumEquations(new BlockPos((int)_x, 110, (int)_z),
                Mat4d.Perspective(Mat4d.Create(), 70 * Math.PI / 180, 16.0 / 9, 0.1, 2.0 * _radius * Chunk),
                Mat4d.LookAt(Mat4d.Create(), eye, center, [0.0, 1.0, 0.0]));
            var (lod0, lod2) = (_radius * Chunk * 0.33f, _radius * Chunk * 0.8);
            (_culler.lod0BiasSq, _culler.lod2BiasSq) = (lod0 * lod0, lod2 * lod2);
        }

        // SystemRenderShadowMap: an orthographic box around the player along the sun, and the range the chunk passes test first
        private void Shadow(bool far)
        {
            var reach = far ? 0.6 * _radius * Chunk : 40;
            (_culler.shadowRangeX, _culler.shadowRangeZ) = (reach * 1.3, reach);
            double[] eye = [_x, 111.6, _z], sun = [_x + 0.35, 112.45, _z + 0.25];
            _culler.CalcFrustumEquations(new BlockPos((int)_x, 110, (int)_z),
                Mat4d.Ortho(Mat4d.Create(), -reach, reach, -0.75 * reach, 0.75 * reach, -2 * reach, 2 * reach),
                Mat4d.LookAt(Mat4d.Create(), sun, eye, [0.0, 1.0, 0.0]));
        }
    }

    private sealed class Section
    {
        public Section((int X, int Z) column, int y, Random r)
        {
            Column = column;
            Origin = new Vec3i(column.X * 32, y * 32, column.Z * 32);
            var (bottom, top) = (r.Next(0, 12), r.Next(16, 33));
            Bounds = new Sphere(Origin.X + 16f, Origin.Y + (bottom + top) / 2f, Origin.Z + 16f, 32, top - bottom, 32);
            var visible = r.Next(10) != 0;
            Visible = new Bools(visible, visible);
        }

        public (int X, int Z) Column { get; }
        public Vec3i Origin { get; }
        public Sphere Bounds { get; }
        public Bools Visible { get; }
        public bool Loaded { get; set; } = true;
        public ModelDataPoolLocation[] Center { get; set; } = [];
        public ModelDataPoolLocation[] Edge { get; set; } = [];
    }
}

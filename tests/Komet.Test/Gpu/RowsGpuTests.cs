using Komet.Rendering;

namespace Komet.Test.Gpu;

// Many boxes have a corner right on one of the frustum's planes or right on a LOD range's edge: the commands rows.comp
// writes must be the locations ModelDataPoolLocation.IsVisible(CullNormal) keeps, no more and no fewer.
public sealed class RowsGpuTests
{
    private const double Cx = 512003.37, Cy = 121.61, Cz = 511988.82;
    private const int Count = 6000, Chunks = 97;

    [TestCase(GpuRig.Gl, 0)]
    public void TheGpuKeepsWhatTheEngineKeeps(string backend, int buffer) =>
        Culled(backend, vao =>
        {
            ModelDataPoolLocation.VisibleBufIndex = buffer;
            var culler = Culler();
            var (locations, _) = Locations(culler, new Random(7 + buffer));
            var pool = Pool(locations, vao);
            var expected = Kept(locations, culler);
            Assert.Multiple(() =>
            {
                Assert.That(OcclusionCulling.RunRows([pool], culler, new Vec3d(Cx, Cy, Cz), ViewProjection()), Is.True,
                    "culled from rows");
                Assert.That(Drawn(locations.Count), Is.EqualTo(expected), "the commands are the engine's locations");
                Assert.That(expected, Has.Count.InRange(Count / 20, Count - Count / 20), "a mix of kept and left out");
            });
        });

    [TestCase(GpuRig.Gl)]
    public void TheRowsFollowWhatChangesBetweenFrames(string backend)
    {
        Culled(backend, vao =>
        {
            ModelDataPoolLocation.VisibleBufIndex = 0;
            var (culler, random, camera) = (Culler(), new Random(11), new Vec3d(Cx, Cy, Cz));
            var (locations, bools) = Locations(culler, random);
            var pool = Pool(locations, vao);
            var frames = new List<(string What, List<int> Drawn, List<int> Kept)>();

            void Frame(string what)
            {
                _ = OcclusionCulling.RunRows([pool], culler, camera, ViewProjection());
                frames.Add((what, Drawn(locations.Count), Kept(locations, culler)));
            }

            Frame("first");
            ModelDataPoolLocation[] hidden = [.. locations.Where((_, i) => i % 7 == 0)];
            foreach (var location in hidden)
            {
                location.Hide = !location.Hide;
            }

            OcclusionCulling.Rehide(hidden); // as the patched engine methods call it
            Frame("Hide written");
            // the chunk culler's thread writes the buffer not shown (ClientChunk.SetVisible), then swaps
            foreach (var chunk in bools.Where((_, i) => i % 3 == 0))
            {
                chunk[1] = !chunk[1];
            }

            Frame("the other buffer written");
            ModelDataPoolLocation.VisibleBufIndex = 1;
            Frame("the visible buffer swapped");
            locations.RemoveRange(100, 400);
            var (more, _) = Locations(culler, random, Count, bools);
            locations.InsertRange(50, more.Take(300));
            Frame("the list changed");
            Assert.Multiple(() =>
            {
                foreach (var (what, drawn, kept) in frames)
                {
                    Assert.That(drawn, Is.EqualTo(kept), what);
                }

                Assert.That(frames.Select(f => f.Kept.Count).Distinct().Count(), Is.GreaterThan(3),
                    "each change counted");
            });
        });
    }

    // Adds and removals as the patched TryAdd and RemoveLocation see them - one Insert or RemoveAt each, the version before
    // taken first - reach the rows without writing the region anew: removed rows taken by the region's last, added ones at its
    // end once the pool is drawn, the region moved to a larger block when it fills up
    [TestCase(GpuRig.Gl)]
    public void AddsAndRemovalsReachTheRows(string backend) =>
        Culled(backend, vao =>
        {
            ModelDataPoolLocation.VisibleBufIndex = 0;
            var (culler, random, camera) = (Culler(), new Random(5), new Vec3d(Cx, Cy, Cz));
            var (locations, bools) = Locations(culler, random);
            var pool = Pool(locations, vao);
            var frames = new List<(string What, List<int> Drawn, List<int> Kept)>();

            void Frame(string what)
            {
                _ = OcclusionCulling.RunRows([pool], culler, camera, ViewProjection());
                frames.Add((what, Drawn(locations.Count), Kept(locations, culler)));
            }

            void Remove(int at)
            {
                OcclusionCulling.Changing(pool, out var state);
                var location = locations[at];
                locations.RemoveAt(at);
                OcclusionCulling.Removed(pool, location, state);
            }

            void Add(int at, ModelDataPoolLocation location)
            {
                OcclusionCulling.Changing(pool, out var state);
                locations.Insert(at, location);
                OcclusionCulling.Added(pool, location, state);
            }

            Frame("first");
            var rewrites = OcclusionCulling.Rewrites;
            for (var i = 0; i < 700; i++)
            {
                Remove(random.Next(locations.Count));
            }

            Frame("removed");
            var (more, _) = Locations(culler, random, Count, bools);
            foreach (var location in more.Take(500))
            {
                Add(random.Next(locations.Count + 1), location);
            }

            for (var i = 0; i < 200; i++)
            {
                Remove(random.Next(locations.Count));
            }

            Frame("added and removed");
            var (many, _) = Locations(culler, random, 3 * Count, bools);
            foreach (var location in many.Concat(Locations(culler, random, 4 * Count, bools).Item1))
            {
                Add(random.Next(locations.Count + 1), location);
            }

            Frame("grown past its block");
            Assert.Multiple(() =>
            {
                foreach (var (what, drawn, kept) in frames)
                {
                    Assert.That(drawn, Is.EqualTo(kept), what);
                }

                Assert.That(locations, Has.Count.GreaterThan(2 * Count), "the region outgrew its first block");
                Assert.That(OcclusionCulling.Rewrites, Is.EqualTo(rewrites), "no region written anew");
            });
        });

    private static void Culled(string backend, Action<VAO> test)
    {
        using var rig = GpuRig.Open(backend);
        if (!GpuBackends.Current.Doubles()) Assert.Ignore(backend + " has no doubles in compute shaders");

        using var harmony = new TestHarmony("komet-test-rows-gpu");
        OcclusionCulling.Install(harmony, Client(), new CapturingLogger());
        var saved = ModelDataPoolLocation.VisibleBufIndex;
        using var vao = new VAO();
        try
        {
            test(vao);
        }
        finally
        {
            OcclusionCulling.Enabled = false;
            ModelDataPoolLocation.VisibleBufIndex = saved;
            OcclusionCulling.Clear();
        }
    }

    private static ICoreClientAPI Client() =>
        Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["get_Render"] = _ => Answers.Of<IRenderAPI>([]) });

    private static FrustumCulling Culler(double far = 1536)
    {
        var culler = new FrustumCulling();
        var projection = Mat4d.Perspective(Mat4d.Create(), 70 * GameMath.DEG2RAD, 16 / 9.0, 0.1, far);
        var view = Mat4d.Create();
        _ = Mat4d.RotateX(view, view, 0.3);
        _ = Mat4d.RotateY(view, view, 1.1);
        _ = Mat4d.Translate(view, view, -Cx, -Cy, -Cz);
        culler.CalcFrustumEquations(new BlockPos((int)Cx, (int)Cy, (int)Cz), projection, view);
        culler.UpdateViewDistance(512);
        (culler.lod0BiasSq, culler.lod2BiasSq) = (128 * 128, 0.45 * culler.ViewDistanceSq);
        return culler;
    }

    private static float[] ViewProjection() =>
        [.. Mat4d.Perspective(Mat4d.Create(), 70 * GameMath.DEG2RAD, 16 / 9.0, 0.1, 1536).Select(v => (float)v)];

    // count locations, a third with a corner on a plane, a fifth on a LOD range's edge; indices i * 6 on
    private static (List<ModelDataPoolLocation>, Bools[]) Locations(FrustumCulling culler, Random random, int first = 0,
        Bools[]? chunks = null)
    {
        var planes = (Plane[])AccessTools.Field(typeof(FrustumCulling), "frustum").GetValue(culler)!;
        var bools = chunks ??
        [
            .. Enumerable.Range(0, Chunks).Select(_ => new Bools(random.Next(4) > 0, random.Next(4) > 0))
        ];
        var limits = new[]
        {
            culler.lod0BiasSq + 1024f, culler.ViewDistanceSq, culler.lod2BiasSq
        };
        List<ModelDataPoolLocation> list = [];
        for (var i = first; i < first + Count; i++)
        {
            var size = 8 + random.Next(25);
            var (x, y, z) = Around(planes[0], random);
            var e = 0.8660254f * size / 1.7320508f;
            if (i % 3 == 0)
            {
                (x, y, z) = OnPlane(planes[random.Next(6)], (x, y, z), e, random);
            }
            else if (i % 5 == 1)
            {
                var (angle, edge) = (random.NextDouble() * Math.Tau, Math.Sqrt(limits[random.Next(3)]));
                (x, z) = ((int)Cx + edge * Math.Cos(angle), (int)Cz + edge * Math.Sin(angle));
            }

            list.Add(new ModelDataPoolLocation
            {
                IndicesStart = 6 * i, IndicesEnd = 6 * i + 6 * (1 + random.Next(4)),
                FrustumCullSphere = new Sphere((float)x, (float)y, (float)z, size, size, size),
                LodLevel = random.Next(-1, 5), Hide = random.Next(10) == 0, CullVisible = bools[random.Next(Chunks)]
            });
        }

        return (list, bools);
    }

    private static (double, double, double) Around(Plane near, Random random)
    {
        if (random.Next(3) == 0)
        {
            return (Cx + random.NextDouble() * 1400 - 700, Cy + random.NextDouble() * 300 - 100,
                Cz + random.NextDouble() * 1400 - 700);
        }

        var length = Math.Sqrt(near.normalX * near.normalX + near.normalY * near.normalY + near.normalZ * near.normalZ);
        var distance = 10 + random.NextDouble() * 600;

        double Spread() => (random.NextDouble() - 0.5) * distance;

        return (Cx + near.normalX / length * distance + Spread(), Cy + near.normalY / length * distance + Spread() / 3,
            Cz + near.normalZ / length * distance + Spread());
    }

    private static (double, double, double) OnPlane(Plane plane, (double X, double Y, double Z) c, float e,
        Random random)
    {
        var (nx, ny, nz) = (plane.normalX, plane.normalY, plane.normalZ);
        var corner = (c.X + Math.Sign(nx) * e) * nx + (c.Y + Math.Sign(ny) * e) * ny + (c.Z + Math.Sign(nz) * e) * nz +
                     plane.D + (random.NextDouble() - 0.5) * 1e-3;
        var step = corner / (nx * nx + ny * ny + nz * nz);
        return (c.X - step * nx, c.Y - step * ny, c.Z - step * nz);
    }

    private static MeshDataPool Pool(List<ModelDataPoolLocation> locations, VAO vao)
    {
        var pool = (MeshDataPool)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPool));
        AccessTools.Field(typeof(MeshDataPool), "poolLocations").SetValue(pool, locations);
        AccessTools.Field(typeof(MeshDataPool), "modelRef").SetValue(pool, vao);
        AccessTools.Field(typeof(MeshDataPool), "poolOrigin").SetValue(pool, new Vec3i((int)Cx, 0, (int)Cz));
        return pool;
    }

    private static List<int> Kept(List<ModelDataPoolLocation> locations, FrustumCulling culler,
        EnumFrustumCullMode mode = EnumFrustumCullMode.CullNormal) =>
        [.. locations.Where(l => l.IsVisible(mode, culler)).Select(l => l.IndicesStart).Order()];

    private static List<int> Drawn(int count, bool shadow = false)
    {
        var counters = new uint[8 + 3 * OcclusionCulling.MaxDraws];
        var commands = new uint[5 * count];
        CullingState.Frame(counters, commands, new uint[5], shadow);
        var drawn = Math.Min(counters[8 + (shadow ? 2 * OcclusionCulling.MaxDraws : 0)], (uint)count);
        return [.. Enumerable.Range(0, (int)drawn).Select(c => (int)commands[5 * c + 2]).Order()];
    }
}

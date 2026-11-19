namespace Komet.Test.Gpu;

// The GPU culling leaving out what faces away (FaceSorting, rows.comp) on TerrainWorld's sections sorted as the tessellator's
// faces are: whole directions and each direction's part past its split. Every face that faces the eye, of every row the
// culling draws, is still in the commands, each step leaves out more, and the picture with back faces culled (as RenderOpaque
// draws opaque and topsoil) is the same pixel for pixel.
[NonParallelizable]
public sealed class FaceSplitsGpuTests
{
    private const int Runs = 4;

    // Two views looking roughly opposite ways, so each side direction faces away in one of them
    private static readonly (double Yaw, double Pitch)[] Views = [(0.3, -0.15), (2.4, -0.5)];

    // Whole; directions and their splits left out
    internal static readonly bool[] Variants = [false, true];

    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    [Category("Slow")]
    public void EveryFaceSeenIsDrawn(string backend) =>
        TerrainWorld.Run(backend, 160, world =>
        {
            foreach (var (yaw, pitch) in Views)
            {
                var view = new TerrainView(yaw, pitch, [0.4, 0.8, 0.3]);
                var drawn = Variants.Select(v => Culled(world, view, v)).ToArray();
                var triangles = drawn.Select(d => d.Sum(pool => pool.Sum(c => c.Count)) / 3).ToArray();
                TestContext.Out.WriteLine(
                    $"yaw {yaw}, pitch {pitch}: {triangles[0]} triangles whole, {triangles[1]} with the " +
                    "directions facing away and their parts past the splits left out");
                Assert.Multiple(() =>
                {
                    Assert.That(Missing(world, view, drawn[0], drawn[1]), Is.Zero, $"yaw {yaw}: directions and splits");
                    Assert.That(triangles[1], Is.LessThan(triangles[0] * 9 / 10), $"yaw {yaw}: directions left out");
                });
            }
        }, true);

    // Each pool's commands (first index, index count) after one culled call
    private static List<(uint First, uint Count)>[] Culled(TerrainWorld world, TerrainView view, bool backFaces)
    {
        world.CullRows(view, backFaces);
        var counters = CullingState.Counters(1);
        var (region, factor) = (0, backFaces ? Runs : 1);
        var pools = new List<(uint, uint)>[world.Pools.Count];
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var count = (int)counters[CullingState.StatUints + p];
            var commands = CullingState.Commands(region, count);
            pools[p] = [.. Enumerable.Range(0, count).Select(c => (commands[5 * c + 2], commands[5 * c]))];
            region += factor * CullingState.Listed(world.Pools[p]).Count;
        }

        return pools;
    }

    // Faces facing the eye, of the locations drawn whole, the culled commands leave out
    private static int Missing(TerrainWorld world, TerrainView view, List<(uint First, uint Count)>[] whole,
        List<(uint First, uint Count)>[] culled)
    {
        var missing = 0;
        double[] eye = [view.Camera.X - TerrainWorld.Cx, view.Camera.Y, view.Camera.Z - TerrainWorld.Cz];
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var positions = world.Positions[p];
            var drawn = new bool[positions.Length / 12];
            foreach (var (first, count) in culled[p])
            {
                for (var f = first / 6; f < (first + count) / 6; f++)
                {
                    drawn[f] = true;
                }
            }

            foreach (var location in CullingState.Listed(world.Pools[p])
                         .Where(l => whole[p].Any(c => c.First == l.IndicesStart)))
            {
                for (var f = location.IndicesStart / 6; f < location.IndicesEnd / 6; f++)
                {
                    if (!drawn[f] && Facing(positions, f, eye)) missing++;
                }
            }
        }

        return missing;
    }

    // The quad's front side toward the eye
    private static bool Facing(float[] xyz, int face, double[] eye)
    {
        var v = 12 * face;
        double[] a = [xyz[v + 3] - xyz[v], xyz[v + 4] - xyz[v + 1], xyz[v + 5] - xyz[v + 2]];
        double[] b = [xyz[v + 6] - xyz[v], xyz[v + 7] - xyz[v + 1], xyz[v + 8] - xyz[v + 2]];
        double[] n = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
        return n[0] * (eye[0] - xyz[v]) + n[1] * (eye[1] - xyz[v + 1]) + n[2] * (eye[2] - xyz[v + 2]) > 0;
    }
}

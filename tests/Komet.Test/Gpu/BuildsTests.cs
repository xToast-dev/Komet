using Komet.Vulkan;

namespace Komet.Test.Gpu;

// A build's result is handed over once, whichever way its worker's end and its owner's drop interleave: gates hold the workers,
// so each order is the test's to choose.
[NonParallelizable]
public sealed class BuildsTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [SetUp]
    public void Unwaited() => _ = Builds.Waited = false;

    [TearDown]
    public void Done() => Assert.That(Builds.Wait(Long), Is.True, "no build left running");

    private static void Free(Made made) => _ = Interlocked.Increment(ref made.Freed);

    [Test]
    public void WhatWasMadeIsTheTakersOnce()
    {
        var made = new Made();
        var build = new Build<Made>(Builds.Kind.Port, () => (made, ""), Free);
        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(build.Done, Is.True);
        Assert.That(build.Take(out var why), Is.SameAs(made));
        Assert.That(why, Is.Empty);
        build.Drop();
        Assert.That(made.Freed, Is.Zero, "taken: its owner's to free");
    }

    [Test]
    public void DroppedWhileItIsMadeItsWorkerFreesIt()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var made = new Made();
        var build = new Build<Made>(Builds.Kind.Pipeline, () =>
        {
            // ReSharper disable once AccessToDisposedClosure - the worker is done once Builds.Wait returned
            started.Set();
            // ReSharper disable once AccessToDisposedClosure - the worker is done once Builds.Wait returned
            _ = gate.Wait(Long);
            return (made, "");
        }, Free);
        Assert.That(started.Wait(Long), Is.True);
        build.Drop();
        Assert.That(made.Freed, Is.Zero, "not made yet");
        gate.Set();
        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(made.Freed, Is.EqualTo(1), "freed by the worker as it finished");
    }

    private sealed class Made
    {
        public int Freed;
    }
}

// A renderer's pipelines: made on builders, forgotten into a list that is destroyed only once the frames that may have recorded
// them are done, dropped while still being made - and none outlives its set.
[NonParallelizable]
public sealed class PipelineSetGpuTests
{
    private const uint Rgba8 = 37, GlFloat = 0x1406; // VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_B8G8R8A8_UNORM

    private const string Vertex = """
                                  #version 330 core
                                  layout(location = 0) in vec3 position;
                                  void main() { gl_Position = vec4(position, 1.0); }
                                  """;

    private const string Fragment = """
                                    #version 330 core
                                    out vec4 color;
                                    void main() { color = vec4(1.0, 0.5, 0.25, 1.0); }
                                    """;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private static readonly TerrainPools.Attribute Position = new(0, 0, 3, (int)GlFloat, false, false) { Stride = 12 };

    // A draw awaits its pipeline; forgotten, it goes once the frames that may have recorded it are done
    [Test]
    public void APipelineIsAwaitedAndGoesOnceTheFramesThatMayHaveRecordedItAreDone()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        var device = rig.Device!;
        rig.Backend!.Flush();
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        var ported = Ported();
        using (var set = new PipelineSet<int>(device, frame!, null, 8))
        {
            Build<TerrainPipeline> Start(GlslPort.Ported port) =>
                TerrainPipeline.Started(device, port, [Position],
                    (default, new TerrainPipeline.Target([Rgba8], 0), Vk.TopologyTriangles));

            frame!.Next();
            var started = Builds.Started;
            var made = set.Get(1, ("test", ported), Start, out var waiting);
            Assert.That(made, Is.Not.Null, "made on a builder and awaited: the draw would be missing without it");
            Assert.That(made!.Pipeline, Is.Not.Zero);
            Assert.That(waiting, Is.False);
            Assert.That(Builds.Started, Is.EqualTo(started + 1), "on a builder");
            Assert.That(set.Get(1, ("test", ported), Start, out _), Is.SameAs(made), "made once");
            Assert.That(Builds.Started, Is.EqualTo(started + 1));
            set.Forget(key => key == 1);
            Assert.That(set.TryMade(1, out _), Is.False);
            for (var i = 1; i < VulkanFrame.Slots; i++)
            {
                frame.Next();
                set.Forget(static _ => false);
                Assert.That(made.Pipeline, Is.Not.Zero,
                    $"frame {frame.Number}: the frame it was forgotten in may still run");
            }

            frame.Next();
            set.Forget(static _ => false);
            Assert.That(made.Pipeline, Is.Zero,
                "that frame's slot came round again: destroyed");
            set.Start(3, ("test", ported), Start);
            set.Forget(key => key == 3); // still being made, or made and not taken: its builder or the drop destroys it
        }

        Assert.That(Builds.Wait(Long), Is.True);
    }

    private static GlslPort.Ported Ported()
    {
        var ported = GlslPort.Build("test", Vertex, Fragment, new Dictionary<string, int> { ["position"] = 0 },
            out var why);
        Assert.That(ported, Is.Not.Null, why);
        return ported!;
    }
}

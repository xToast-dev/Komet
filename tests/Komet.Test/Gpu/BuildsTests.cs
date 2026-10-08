using Komet.Vulkan;

namespace Komet.Test.Gpu;

// A build's result is handed over once, whichever way its worker's end and its owner's drop interleave: gates hold the workers,
// so each order is the test's to choose.
[NonParallelizable]
public sealed class BuildsTests
{
    private const int Workers = 2; // Builds.Workers
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private sealed class Made
    {
        public int Freed;
    }

    [SetUp]
    public void Unwaited() => Builds.Waited = false;

    [TearDown]
    public void Done() => Assert.That(Builds.Wait(Long), Is.True, "no build left running");

    private static void Free(Made made) => Interlocked.Increment(ref made.Freed);

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
            started.Set();
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

    [Test]
    public void DroppedOnceMadeTheDropFreesIt()
    {
        var made = new Made();
        var build = new Build<Made>(Builds.Kind.Pipeline, () => (made, ""), Free);
        Assert.That(Builds.Wait(Long), Is.True);
        build.Drop();
        build.Drop();
        Assert.That(made.Freed, Is.EqualTo(1), "once");
    }

    [Test]
    public void DroppedBeforeItBeganItIsNeverMade()
    {
        using var started = new CountdownEvent(Workers);
        using var gate = new ManualResetEventSlim();
        for (var i = 0; i < Workers; i++) // every worker held
            _ = new Build<Made>(Builds.Kind.Port, () =>
            {
                _ = started.Signal();
                _ = gate.Wait(Long);
                return (null, "held");
            });
        Assert.That(started.Wait(Long), Is.True);
        var ran = 0;
        var queued = new Build<Made>(Builds.Kind.Pipeline, () =>
        {
            ran++;
            return (new Made(), "");
        }, Free);
        queued.Drop();
        gate.Set();
        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(queued.Done, Is.True);
        Assert.That(ran, Is.Zero, "cancelled: nothing made");
    }

    [Test]
    public void AFailedBuildSaysWhy()
    {
        var build = new Build<Made>(Builds.Kind.Port, () => throw new InvalidOperationException("no compiler"));
        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(build.Take(out var why), Is.Null);
        Assert.That(why, Is.EqualTo("InvalidOperationException: no compiler"));
    }

    [Test]
    public void WaitedTheEngineThreadHasItAtOnce()
    {
        Builds.Waited = true;
        try
        {
            var build = new Build<Made>(Builds.Kind.Port, () =>
            {
                _ = SpinWait.SpinUntil(static () => false, 20); // still running when the engine thread would look
                return (new Made(), "");
            });
            Assert.That(build.Done, Is.True);
            Assert.That(build.Take(out _), Is.Not.Null);
        }
        finally
        {
            Builds.Waited = false;
        }
    }

    [Test]
    public void TheBuildsAreCountedOffTheEngineThread()
    {
        var before = Builds.Started;
        var build = new Build<Made>(Builds.Kind.Pipeline, () => (new Made(), ""));
        Assert.That(Builds.Started, Is.EqualTo(before + 1));
        Assert.That(build.Wait(Long), Is.True);
        Assert.That(Builds.Report(), Does.Contain("pipelines"));
    }
}

// A renderer's pipelines: made on builders, forgotten into a list that is destroyed only once the frames that may have recorded
// them are done, dropped while still being made - and none outlives its set.
[NonParallelizable]
public sealed class PipelineSetGpuTests
{
    private const uint Rgba8 = 37, Bgra8 = 44, GlFloat = 0x1406; // VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_B8G8R8A8_UNORM
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private static readonly TerrainPools.Attribute Position = new(0, 0, 3, (int)GlFloat, false, false) { Stride = 12 };

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

    [Test]
    public void APipelineGoesOnceTheFramesThatMayHaveRecordedItAreDone()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        var device = rig.Device!;
        rig.Backend!.Flush();
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        var ported = Ported();
        var live = TerrainPipeline.Live;
        using (var set = new PipelineSet<int>(device, frame!, null, 8))
        {
            Build<TerrainPipeline> Start(GlslPort.Ported port) => TerrainPipeline.Started(device, port, [Position],
                (default, new TerrainPipeline.Target([Rgba8], 0), Vk.TopologyTriangles));
            frame!.Next();
            Assert.That(set.Get(1, ("test", ported), Start, false, out var waiting), Is.Null);
            Assert.That(waiting, Is.True, "made on a builder, the draw OpenGL's meanwhile");
            Assert.That(Builds.Wait(Long), Is.True);
            Assert.That(set.Get(1, ("test", ported), Start, false, out waiting), Is.Null);
            Assert.That(waiting, Is.True, "done, but the frame that asked first goes on as it began");
            frame.Next();
            var made = set.Get(1, ("test", ported), Start, false, out waiting);
            Assert.That(made, Is.Not.Null);
            Assert.That(made!.Pipeline, Is.Not.Zero);
            Assert.That(waiting, Is.False, "the next frame draws with it");
            var started = Builds.Started;
            Assert.That(set.Get(1, ("test", ported), Start, false, out _), Is.SameAs(made), "made once");
            Assert.That(set.Get(2, ("test", ported), Start, true, out waiting), Is.Not.Null, "waited for where a draw cannot do without");
            Assert.That(waiting, Is.False);
            Assert.That(Builds.Started, Is.EqualTo(started + 1));
            set.Forget(key => key == 1);
            Assert.That((set.Count, set.Retired), Is.EqualTo((1, 1)));
            for (var i = 1; i < VulkanFrame.Slots; i++)
            {
                frame.Next();
                set.Forget(static _ => false);
                Assert.That(made.Pipeline, Is.Not.Zero, $"frame {frame.Number}: the frame it was forgotten in may still run");
            }

            frame.Next();
            set.Forget(static _ => false);
            Assert.That((made.Pipeline, set.Retired), Is.EqualTo((0UL, 0)), "that frame's slot came round again: destroyed");
            Assert.That(set.Get(3, ("test", ported), Start, false, out _), Is.Null);
            set.Forget(key => key == 3); // still being made, or made and not taken: its builder or the drop destroys it
            Assert.That(set.Building, Is.Zero);
        }

        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(TerrainPipeline.Live, Is.EqualTo(live), "every pipeline destroyed: taken, forgotten or dropped");
    }

    // A program's next port gets every pipeline its port drew with, under keys of its own, before it switches over
    [Test]
    public void ThePipelinesOfAPortAreMadeAgainForTheNext()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        Builds.Waited = false;
        var device = rig.Device!;
        rig.Backend!.Flush();
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        var (first, second) = (Ported(), Ported());
        var live = TerrainPipeline.Live;
        using (var set = new PipelineSet<(int Port, uint Format)>(device, frame!, null, 8))
        {
            System.Func<GlslPort.Ported, Build<TerrainPipeline>> Start(uint format) => port =>
                TerrainPipeline.Started(device, port, [Position],
                    (default, new TerrainPipeline.Target([format], 0), Vk.TopologyTriangles));
            frame!.Next();
            foreach (var format in (uint[])[Rgba8, Bgra8]) _ = set.Get((1, format), ("test", first), Start(format), false, out _);
            Assert.That(Builds.Wait(Long), Is.True);
            frame.Next();
            Assert.That(set.Get((1, Rgba8), ("test", first), Start(Rgba8), false, out _), Is.Not.Null);
            var keys = set.Warm(key => key.Port == 1, key => key with { Port = 2 }, ("test", second));
            Assert.That(keys, Is.EquivalentTo(((int, uint)[])[(2, Rgba8), (2, Bgra8)]), "both, the one not drawn with this frame too");
            Assert.That(set.Made(keys), Is.False);
            Assert.That(Builds.Wait(Long), Is.True);
            Assert.That(set.Made(keys), Is.False, "this frame counts them as it began");
            frame.Next();
            Assert.That(set.Made(keys), Is.True);
            Assert.That(set.Get((2, Bgra8), ("test", second), Start(Bgra8), false, out var waiting), Is.Not.Null);
            Assert.That(waiting, Is.False);
            Assert.That(set.Count, Is.EqualTo(4));
        }

        Assert.That(Builds.Wait(Long), Is.True);
        Assert.That(TerrainPipeline.Live, Is.EqualTo(live));
    }

    private static GlslPort.Ported Ported()
    {
        var ported = GlslPort.Build("test", Vertex, Fragment, new Dictionary<string, int> { ["position"] = 0 }, out var why);
        Assert.That(ported, Is.Not.Null, why);
        return ported!;
    }
}

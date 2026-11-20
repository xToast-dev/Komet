using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;
using Komet.Tessellation;
using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.Core;

// Every engine method and field Komet reaches by name. A feature whose method or accessor field is gone stands down at install with a
// logged assertion, so a game update that renames one turns it off; here it turns the build red instead. A name Harmony injects into
// a patch (a ___field or an argument) would make harmony.Patch throw at install; Il.Binds stands the feature down before that, and
// PatchBindsByName holds those names against this engine.
public sealed class EngineSeamTests
{
    private static readonly Type[] NoArgs = [];
    private static readonly Type[] Coords = [typeof(int), typeof(int), typeof(int)];
    private static readonly Type[] Renderer = [typeof(IRenderer), typeof(EnumRenderStage), typeof(string)];

    private static IEnumerable<TestCaseData> Methods()
    {
        yield return Case<ShaderProgramBase>("Use", NoArgs);
        yield return Case<DefaultShaderUniforms>("Update", null);
        yield return Case<MeshDataPool>("FrustumCull", null);
        yield return Case<MeshDataPoolMasterManager>("OnFrame", null);
        yield return Case<ClientPlatformWindows>("RenderMesh",
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        yield return Case<ClientEventManager>("TriggerRenderStage", null);
        yield return Case<GameTickListener>("OnTriggered", null);
        yield return Case<GameTickListenerBlock>("OnTriggered", null);
        yield return Case<ClientWorldMap>("GetChunk", [typeof(long)]);
        yield return Case<ClientWorldMap>("GetChunk", Coords);
        yield return Case<ClientWorldMap>("GetChunkAtBlockPos", Coords);
        yield return Case<ClientWorldMap>("loadChunkMT", null);
        yield return Case<ClientWorldMap>("OverloadChunkMT", null);
        yield return Case<SystemUnloadChunks>("HandleChunkUnload", null);
        yield return Case<MeshDataPool>("InsertAt", null);
        yield return Case<MeshDataPool>("RemoveLocation", null);
        yield return Case<MeshDataPool>("TrySqueezeInbetween", null);
        yield return Case<MeshData>("CloneExtraData", null);
        yield return Case<MeshData>("DisposeExtraData", null);
        // FaceSorting's seam: by name alone this is ambiguous on some engine builds, so pin the exact overload
        yield return Case<ChunkTesselator>("populateTesselatedChunkPart",
            [typeof(MeshData[][][]), typeof(TesselatedChunkPart[]).MakeByRefType()]);
        yield return Case<ChunkTesselatorManager>("OnBeforeFrame", null);
        yield return Case<ChunkTesselatorManager>("OnSeperateThreadGameTick", [typeof(float)]);
        yield return Case<ChunkTesselatorManager>("TesselateChunk",
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()]);
        yield return Case<ClientWorldMap>("LoadOrCreateLerpedClimateMapOffthread", null);
        yield return Case<ClientWorldMap>("LoadChunkFromPacket", null);
        yield return Case<ClientMain>("ExecuteMainThreadTasks", null);
        yield return Case<ClientChunk>("InitBlockEntitiesFromPacket", [typeof(ClientMain)]);
        yield return Case<ClientWorldMap>("GetClientChunk", Coords);
        yield return Case<BlockEntityGroundStorage>("UpdateIgnitable", NoArgs);
    }

    private static IEnumerable<TestCaseData> Fields()
    {
        yield return Field<MeshDataPool>("poolLocations");
        yield return Field<MeshDataPool>("poolId");
        yield return Field<MeshDataPool>("poolOrigin");
        yield return Field<MeshDataPool>("modelRef");
        yield return Field<FrustumCulling>("frustum");
        yield return Field<FrustumCulling>("playerPos");
        yield return Field<ClientWorldMap>("chunks", typeof(Dictionary<long, ClientChunk>));
        yield return Field<ClientWorldMap>("chunksLock", typeof(object));
        yield return Field<ClientWorldMap>("LerpedClimateMaps");
        yield return Field<ClientWorldMap>("LerpedClimateMapsLock");
        yield return Field<ShaderRegistry>("includes", typeof(Dictionary<string, string>));
        yield return Field<ClientMain>("reversedQueue", typeof(Queue<ClientTask>));
        yield return Field<ProcessPacketTask>("packet", typeof(Packet_Server));
        yield return Field<ClientEventManager>("EventBusListeners", typeof(List<EventBusListener>));
        yield return Field<List<int>>("_version");
        // IdleAnimators' accessors: the count's offset is measured at install, the version is read by every parked stage
        yield return Field<AnimatorBase>("activeAnimCount", typeof(int));
        yield return Field<List<RenderHandler>>("_version", typeof(int)).SetName("List<RenderHandler>._version");
        // TessSchedule's accessors: a mismatch would throw on the tessellation thread, which ends the game
        yield return Field<ClientSystem>("game", typeof(ClientMain));
        yield return Field<ChunkTesselator>("started", typeof(bool));
        foreach (var queue in (string[])["dirtyChunks", "dirtyChunksLast", "dirtyChunksPriority"])
        {
            yield return Field<ClientMain>(queue, typeof(UniqueQueue<long>));
            yield return Field<ClientMain>(queue + "Lock", typeof(object));
        }
    }

    private static TestCaseData Case<T>(string member, Type[]? args) =>
        new TestCaseData(typeof(T), member, args).SetName(
            $"{typeof(T).Name}.{member}{(args == null ? "" : $"({args.Length})")}");

    // A field type is given where an UnsafeAccessor binds it, which needs the exact type
    private static TestCaseData Field<T>(string member, Type? type = null) =>
        new TestCaseData(typeof(T), member, type).SetName($"{typeof(T).Name}.{member}");

    // One test per list: a seam that is gone names itself in the failure message
    [Test]
    public void EveryMethodExists() => Assert.Multiple(() =>
    {
        foreach (var c in Methods().Bounded(256))
        {
            MethodExists((Type)c.Arguments[0]!, (string)c.Arguments[1]!, (Type[]?)c.Arguments[2]);
        }
    });

    [Test]
    public void EveryFieldExists() => Assert.Multiple(() =>
    {
        foreach (var c in Fields().Bounded(256))
        {
            FieldExists((Type)c.Arguments[0]!, (string)c.Arguments[1]!, (Type?)c.Arguments[2]);
        }
    });

    [Test]
    public void EveryPatchBindsByName() => Assert.Multiple(() =>
    {
        foreach (var c in Patches().Bounded(256))
        {
            PatchBindsByName((Type)c.Arguments[0]!, (string)c.Arguments[1]!, (Type[]?)c.Arguments[2],
                (Type)c.Arguments[3]!, (string)c.Arguments[4]!);
        }
    });

    private static void MethodExists(Type type, string name, Type[]? args)
    {
        ArgumentNullException.ThrowIfNull(type);
        Assert.That(AccessTools.Method(type, name, args), Is.Not.Null,
            $"{type.FullName}.{name} is gone, the Komet feature that patches it stands down");
    }

    private static void FieldExists(Type type, string name, Type? fieldType)
    {
        ArgumentNullException.ThrowIfNull(type);
        var field = fieldType is null ? AccessTools.Field(type, name) : AccessTools.DeclaredField(type, name);
        Assert.That(field, Is.Not.Null, $"{type.FullName}.{name} is gone, the Komet feature that reads it stands down");
        if (fieldType is not null)
        {
            Assert.That(field.FieldType, Is.EqualTo(fieldType), $"{type.FullName}.{name} changed its type");
        }
    }

    private static IEnumerable<TestCaseData> Patches()
    {
        yield return Patch<MeshDataPool>("FrustumCull", null, typeof(FrustumSweep), "FrustumCull");
        yield return Patch<ClientPlatformWindows>("RenderMesh",
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)],
            typeof(IndirectDraw), "RenderMesh");
        yield return Patch<MeshData>("CloneExtraData", null, typeof(MeshRecycle), "CloneExtraData");
        yield return Patch<ClientWorldMap>("GetChunk", [typeof(long)], typeof(ChunkLookup), "ByIndex");
        yield return Patch<ClientWorldMap>("GetChunk", Coords, typeof(ChunkLookup), "ByCoord");
        yield return Patch<ClientWorldMap>("GetChunkAtBlockPos", Coords, typeof(ChunkLookup), "AtBlockPos");
        yield return Patch<ClientWorldMap>("loadChunkMT", null, typeof(ChunkLookup), "Loaded");
        yield return Patch<ClientWorldMap>("OverloadChunkMT", null, typeof(ChunkLookup), "Overloading");
        yield return Patch<ClientWorldMap>("OverloadChunkMT", null, typeof(ChunkLookup), "Overloaded");
        yield return Patch<SystemUnloadChunks>("HandleChunkUnload", null, typeof(ChunkLookup), "Unloading");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "Begin");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "End");
        yield return Patch<ClientEventManager>("TriggerRenderStage", null, typeof(ModTimes), "RenderStage");
        yield return Patch<AnimationUtil>("StartAnimation", null, typeof(IdleAnimators), "Woken");
        yield return Patch<ClientMain>("ExecuteMainThreadTasks", null, typeof(EventBusSweep), "Sweep");
        yield return Patch<BlockEntity>("OnBlockUnloaded", null, typeof(EventBusSweep), "Died");
        yield return Patch<BlockEntity>("OnBlockRemoved", null, typeof(EventBusSweep), "Died");
        yield return Patch<BlockEntity>("Initialize", null, typeof(EventBusSweep), "Revived");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(EventBusSweep), "Registered");
        yield return Patch<EventManager>("AddGameTickListenerBlockInternal", null, typeof(ListenerSlots), "AddTick");
        yield return Patch<EventManager>("RemoveGameTickListener", null, typeof(ListenerSlots), "RemovingTick");
        yield return Patch<EventManager>("RemoveGameTickListener", null, typeof(ListenerSlots), "RemovedTick");
        yield return Patch<ClientEventManager>("RegisterRenderer", Renderer, typeof(ListenerSlots), "Rendering");
        yield return Patch<ClientEventManager>("RegisterRenderer", Renderer, typeof(ListenerSlots), "Rendered");
        yield return Patch<ClientEventManager>("UnregisterRenderer", null, typeof(ListenerSlots), "Unrendering");
        yield return Patch<ClientEventManager>("UnregisterRenderer", null, typeof(ListenerSlots), "Unrendered");
        yield return Patch<DummyRenderer>("set_RenderOrder", null, typeof(ListenerSlots), "Moved");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(ListenerSlots), "Listening");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(ListenerSlots), "Listened");
        yield return Patch<ClientEventAPI>("UnregisterEventBusListener", null, typeof(ListenerSlots), "Unlistening");
        yield return Patch<ClientEventAPI>("UnregisterEventBusListener", null, typeof(ListenerSlots), "Unlistened");
        yield return Patch<BlockEntityFirepit>("getOrCreateMesh", null, typeof(BlockEntityCaches),
            "Mesh");
    }

    private static TestCaseData Patch<T>(string member, Type[]? args, Type komet, string patch) =>
        new TestCaseData(typeof(T), member, args, komet, patch).SetName(
            $"{typeof(T).Name}.{member} by {komet.Name}.{patch}");

    private static void PatchBindsByName(Type type, string name, Type[]? args, Type komet, string patch)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(komet);
        var (original, mine) = (AccessTools.Method(type, name, args), AccessTools.Method(komet, patch));
        Assert.That(Il.Binds(original, mine), Is.True,
            $"{komet.Name}.{patch} names what {type.Name}.{name} lacks, its Install stands down");
    }

}

// The features' fingerprints are pinned in FeaturesTests, each feature tests its own stand-down.
public sealed class EngineShapeTests
{
    [Test]
    public void FingerprintIsStableAndTellsMethodsApart()
    {
        var visible = AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.CalculateVisibleFaces),
            [typeof(bool), typeof(int), typeof(int), typeof(int)]);
        var fluids = AccessTools.DeclaredMethod(typeof(ChunkTesselator),
            nameof(ChunkTesselator.CalculateVisibleFaces_Fluids));
        var emit = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAo));
        var byFlag = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAoByFlag));
        Assert.Multiple(() =>
        {
            var first = EngineShape.Of(visible);
            Assert.That(EngineShape.Of(visible), Is.Not.Zero.And.EqualTo(first), "the same body, the same fingerprint");
            Assert.That(EngineShape.Of(visible), Is.Not.EqualTo(EngineShape.Of(fluids)),
                "the fluid twin differs in a few instructions");
            Assert.That(EngineShape.Of(emit), Is.Not.EqualTo(EngineShape.Of(byFlag)), "two near-identical one-liners");
            Assert.That(EngineShape.Of((MethodBase?)null), Is.Zero);
            Assert.That(EngineShape.Of(AccessTools.Method(typeof(IGeometryTester),
                nameof(IGeometryTester.GetCurrentBlockEntityOnSide), [typeof(BlockFacing)])), Is.Zero, "no body");
            Assert.That(EngineShape.Of([visible, null]), Is.Zero, "a missing method");
            Assert.That(EngineShape.Of([visible, fluids]), Is.Not.EqualTo(EngineShape.Of([fluids, visible])),
                "in order");
        });
    }

    private static bool RunOriginal() => true;

    [TestCaseSource(nameof(Declining))]
    public void AChangedEngineDeclinesTheInstall(string name, Action<Harmony, ILogger> install, Func<bool> installed)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(installed);
        using var harmony = new TestHarmony($"komet-test-{name}-changed");
        var logger = new CapturingLogger();
        install(harmony, logger);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(installed(), Is.False);
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
        }
    }

    private static IEnumerable<TestCaseData> Declining()
    {
        return
        [
            Case("occludedchunks", (h, l) => OccludedChunks.Install(h, l, OccludedChunks.Shape ^ 1),
                () => OccludedChunks.Installed)
        ];

        static TestCaseData Case(string name, Action<Harmony, ILogger> install, Func<bool> installed) =>
            new TestCaseData(name, install, installed).SetArgDisplayNames(name);
    }
}

public sealed class WorkerPoolTests
{
    private const int Items = 4096;
    private static readonly int[] Hits = new int[Items];
    private static readonly int[] Threads = new int[Items];

    private static readonly Action<int> Count = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        Threads[i] = WorkerPool.Current;
        Thread.SpinWait(200);
    };
    private static int _throwAt = -1;

    private static readonly Action<int> Throwing = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        if (i == _throwAt) throw new InvalidOperationException("item " + i);
    };

    private int _limit;
    private Func<bool>? _saved;

    [SetUp]
    public void Save()
    {
        (_saved, _limit) = (WorkerPool.Background, WorkerPool.BackgroundLimit);
        (WorkerPool.Background, WorkerPool.BackgroundLimit) = (null, WorkerPool.MaxThreads);
        Array.Clear(Hits);
        Array.Clear(Threads);
    }

    [TearDown]
    public void Restore()
    {
        Assert.That(WorkerPool.Stop(), Is.True, "every thread ended");
        (WorkerPool.Background, WorkerPool.BackgroundLimit) = (_saved, _limit);
    }

    [Test]
    public void EveryItemRunsOnceAndTheWorkersHelp()
    {
        WorkerPool.Resize(null, 4);
        for (var batch = 0; batch < 50; batch++)
        {
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        }

        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 50), Is.True, "each item once per batch");
            Assert.That(Threads.Any(t => t >= 0), Is.True, "workers took items");
        });
    }

    // A missing completion must not hold the main thread in a minutes-long user-space spin
    [Test]
    public void WaitForStopsAfterItsDeadline()
    {
        var done = 0;
        var started = Environment.TickCount64;
        Assert.That(WorkerPool.WaitFor(ref done, 1, 20), Is.False);
        Assert.That(Environment.TickCount64 - started, Is.LessThan(500));

        done = 1;
        Assert.That(WorkerPool.WaitFor(ref done, 1, 20), Is.True);
    }

    // An item that throws fails its batch after every other item ran; a second failure turns frame batches off until Stop
    [Test]
    public void FailedBatchesTurnFrameJobsOff()
    {
        WorkerPool.Resize(null, 3);
        _throwAt = 17;
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 1), Is.True, "the other items still ran");
            Assert.That(WorkerPool.LastError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(WorkerPool.FrameOff, Is.False, "one failure only fails its batch");
        });
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.FrameOff, Is.True);
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Declined));
            Assert.That(WorkerPool.Running, Is.EqualTo(3), "the threads keep running for background jobs");
        });
        _throwAt = -1;
    }

    // Fewer threads park the rest; another world retires them all, and Stop joins every thread, retired ones included
    [Test]
    public void ParkingAndAnotherWorld()
    {
        var (first, second) = (new object(), new object());
        WorkerPool.Resize(first, 4);
        WorkerPool.Resize(first, 1);
        Assert.That(WorkerPool.Running, Is.EqualTo(1));
        WorkerPool.Resize(first, 3);
        Assert.That(WorkerPool.Running, Is.EqualTo(3), "parked threads are reused");
        WorkerPool.Resize(second, 2);
        Assert.That(WorkerPool.Running, Is.EqualTo(2));
        Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads), Is.EqualTo(WorkerPool.FrameResult.Done));
        Assert.That(WorkerPool.Stop(), Is.True);
        Assert.That(WorkerPool.Running, Is.Zero);
    }
}

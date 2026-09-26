using HarmonyLib;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Test.Core;

// Every engine method and field Komet reaches by name. A feature whose method or accessor field is gone stands down at install with a
// logged assertion, so a game update that renames one turns it off; here it turns the build red instead. A name Harmony injects into
// a patch (a ___field or an argument) would make harmony.Patch throw at install; Il.Binds stands the feature down before that, and
// PatchBindsByName holds those names against this engine.
public sealed class EngineSeamTests
{
    private static readonly Type[] NoArgs = [];
    private static readonly Type[] Coords = [typeof(int), typeof(int), typeof(int)];

    private static IEnumerable<TestCaseData> Methods()
    {
        yield return Case<ShaderProgramBase>("Use", NoArgs);
        yield return Case<DefaultShaderUniforms>("Update", null);
        yield return Case<MeshDataPool>("FrustumCull", null);
        yield return Case<MeshDataPoolMasterManager>("OnFrame", null);
        yield return Case<ClientPlatformWindows>("RenderMesh",
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        yield return Case<SystemRenderSunMoon>("OnRenderFrame3DPost", null);
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
        yield return Case<ChunkTesselatorManager>("OnBeforeFrame", null);
        yield return Case<ChunkTesselatorManager>("OnSeperateThreadGameTick", [typeof(float)]);
        yield return Case<ChunkTesselatorManager>("TesselateChunk",
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()]);
        yield return Case<ClientWorldMap>("LoadOrCreateLerpedClimateMapOffthread", null);
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
        yield return Field<SystemRenderSunMoon>("occlQueryId");
        yield return Field<SystemRenderSunMoon>("firstTickDone");
        yield return Field<SystemRenderSunMoon>("nowQuerying");
        yield return Field<SystemRenderSunMoon>("targetSunSpec");
        yield return Field<List<int>>("_version");
        // TessSchedule's accessors: a mismatch would throw on the tessellation thread, which ends the game
        yield return Field<ClientSystem>("game", typeof(ClientMain));
        yield return Field<ChunkTesselator>("started", typeof(bool));
        foreach (var queue in (string[])["dirtyChunks", "dirtyChunksLast", "dirtyChunksPriority"])
        {
            yield return Field<ClientMain>(queue, typeof(UniqueQueue<long>));
            yield return Field<ClientMain>(queue + "Lock", typeof(object));
        }
    }

    private static TestCaseData Case<T>(string member, Type[]? args)
    {
        return new TestCaseData(typeof(T), member, args).SetName(
            $"{typeof(T).Name}.{member}{(args == null ? "" : $"({args.Length})")}");
    }

    // A field type is given where an UnsafeAccessor binds it, which needs the exact type
    private static TestCaseData Field<T>(string member, Type? type = null)
    {
        return new TestCaseData(typeof(T), member, type).SetName($"{typeof(T).Name}.{member}");
    }

    [TestCaseSource(nameof(Methods))]
    public void MethodExists(Type type, string name, Type[]? args)
    {
        ArgumentNullException.ThrowIfNull(type);
        Assert.That(AccessTools.Method(type, name, args), Is.Not.Null,
            $"{type.FullName}.{name} is gone, the Komet feature that patches it stands down");
    }

    [TestCaseSource(nameof(Fields))]
    public void FieldExists(Type type, string name, Type? fieldType)
    {
        ArgumentNullException.ThrowIfNull(type);
        var field = fieldType is null ? AccessTools.Field(type, name) : AccessTools.DeclaredField(type, name);
        Assert.That(field, Is.Not.Null, $"{type.FullName}.{name} is gone, the Komet feature that reads it stands down");
        if (fieldType is not null)
            Assert.That(field.FieldType, Is.EqualTo(fieldType), $"{type.FullName}.{name} changed its type");
    }

    // Each engine method with the Komet patch whose parameters Harmony binds to its arguments and fields by name
    private static IEnumerable<TestCaseData> Patches()
    {
        yield return Patch<SystemRenderSunMoon>("OnRenderFrame3DPost", null, typeof(SunOcclusion), "Prefix");
        yield return Patch<MeshDataPool>("InsertAt", null, typeof(MeshPool), "InsertAt");
        yield return Patch<MeshDataPool>("RemoveLocation", null, typeof(MeshPool), "RemoveLocation");
        yield return Patch<MeshDataPool>("TrySqueezeInbetween", null, typeof(MeshPool), "TrySqueezeInbetween");
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
        yield return Patch<SystemRenderEntities>("OnBeforeRender", [typeof(float)], typeof(EntityTessBudget), "Frame");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "Begin");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "End");
        yield return Patch<ClientEventManager>("TriggerRenderStage", null, typeof(ModTimes), "RenderStage");
    }

    private static TestCaseData Patch<T>(string member, Type[]? args, Type komet, string patch)
    {
        return new TestCaseData(typeof(T), member, args, komet, patch).SetName(
            $"{typeof(T).Name}.{member} by {komet.Name}.{patch}");
    }

    [TestCaseSource(nameof(Patches))]
    public void PatchBindsByName(Type type, string name, Type[]? args, Type komet, string patch)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(komet);
        var (original, mine) = (AccessTools.Method(type, name, args), AccessTools.Method(komet, patch));
        Assert.That(Il.Binds(original, mine), Is.True,
            $"{komet.Name}.{patch} names what {type.Name}.{name} lacks, its Install stands down");
    }

    [Test]
    public void BindsRefusesAParameterOrFieldTheOriginalLacks()
    {
        var remove = AccessTools.Method(typeof(MeshDataPool), "RemoveLocation");
        var squeeze = AccessTools.Method(typeof(MeshDataPool), "TrySqueezeInbetween");
        Assert.Multiple(() =>
        {
            Assert.That(Il.Binds(remove, AccessTools.Method(typeof(MeshPool), "TrySqueezeInbetween")), Is.False,
                "modeldata");
            Assert.That(Il.Binds(squeeze, AccessTools.Method(typeof(SunOcclusion), "Prefix")), Is.False,
                "___firstTickDone");
        });
    }

    [Test]
    public void ClientSizeIsStillAPropertyOnTheWindow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)), Is.Not.Null);
            Assert.That(AccessTools.PropertySetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(NativeWindow), "OnResize"), Is.Not.Null);
        });
    }
}

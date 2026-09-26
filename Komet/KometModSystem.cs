using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    private const int HudStageCount = 3;
    private const int PoolMs = 250; // how often the worker pool follows its knob and the world

    private static readonly EnumRenderStage[] HudStages =
        [EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done];

    private ICoreClientAPI? _api;
    private Harmony? _harmony;
    private HudOverlay? _overlay;
    private long _pool;

    [SuppressMessage("Design", "CA1063",
        Justification = "ModSystem.Dispose is virtual and the class is sealed, so the override cannot be overridden")]
    public override void Dispose()
    {
        if (_api is not null && _pool != 0) _api.Event.UnregisterGameTickListener(_pool);
        PreJit.Stop();
        ParticleLight.Forget();
        _overlay?.Dispose();
        ChunkLookup.Clear();
        AnimationFrames.Clear();
        TessWorkers.Stop();
        var ended = WorkerPool.Stop();
        _ = Assert(ended); // a worker still inside a job after WorkerPool.JoinMs
        TessAccounting.Clear();
        TessSchedule.Clear();
        _harmony?.UnpatchAll(_harmony.Id);
        TessSafety.Clear(); // after the unpatch: the engine's palette table is the one read again
        (_overlay, _harmony, _api, _pool) = (null, null, null, 0);
    }

    // Besides modinfo "side": "Client": a singleplayer server instance's Dispose would clear the client's statics
    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Client;
    }

    [SuppressMessage("Design", "CA1031",
        Justification =
            "ModLoader.TryRunModPhase drops a system that throws here without disposing it, and its patches would stay")]
    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Attach(Mod.Logger);
        if (!Assert(_harmony is null && _overlay is null) || !Assert(Mod.Info.ModID == "komet"))
            return; // started once; the id is hardcoded in the stats
        try
        {
            Install(api);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Mod.Logger.Error("Komet: install failed, Komet stands down: {0}", e);
            StandDown(api); // all or nothing: a feature that threw halfway through its own patches must not stay half installed
        }
    }

    private void Install(ICoreClientAPI api)
    {
        (_harmony, _api) = (new Harmony(Mod.Info.ModID), api);
        LocalServer.Detect(api, Mod.Logger);
        ModTimes.Install(_harmony, api.ModLoader,
            Mod.Logger); // before the HUD, whose settings may switch the mod times on
        _overlay = new HudOverlay(api);
        ShaderUseCache.Install(_harmony);
        FrustumSweep.Install(_harmony, Mod.Logger);
        IndirectDraw.Install(_harmony);
        SunOcclusion.Install(_harmony);
        WindowSizeCache.Install(_harmony);
        ChunkLookup.Install(_harmony);
        MeshPool.Install(_harmony);
        MeshRecycle.Install(_harmony);
        ClimateCache.Install(_harmony);
        AnimationFrames.Install(_harmony, Mod.Logger);
        InitOnce.Install(_harmony, Mod.Logger);
        ShapeInitMemo.Install(_harmony, Mod.Logger);
        EntityTessBudget.Install(_harmony, Mod.Logger);
        ChunkBudget.Install(_harmony);
        if (LocalServer.Present) ChunkThreadClosure.Install(_harmony); // server code only
        CloudTileScratch.Install(_harmony);
        DecompressScratch.Install(_harmony);
        LightScratch.Install(_harmony, Mod.Logger);
        ParticleLight.Install(_harmony);
        if (LocalServer.Present) ColumnNoiseScratch.Install(_harmony, Mod.Logger); // worldgen, server code only
        TessSafety.Install(_harmony,
            Mod.Logger); // before TessSchedule and TessWorkers, which run passes on the worker pool
        TessSeams.Install(_harmony, Mod.Logger);
        ExtendedRows.Install(_harmony, Mod.Logger);
        VisibleFaces.Install(_harmony, Mod.Logger);
        FaceLight.Install(_harmony, Mod.Logger);
        TessAccounting.Install(_harmony);
        TessSchedule.Install(_harmony);
        TessWorkers.Install(_harmony); // only with TessSafety and TessSchedule installed completely
        OccludedChunks.Install(_harmony, Mod.Logger);
        FrameClock.Install(_harmony);
        Benchmark.Install(api); // inert unless KOMET_BENCH names a bench.json
        api.Event.LevelFinalize += Recheck;
        _pool = api.Event.RegisterGameTickListener(SteerPool, PoolMs);
        foreach (var stage in HudStages.Bounded(HudStageCount))
            api.Event.RegisterRenderer(_overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet opens the settings");
        PreJit.Start(Mod.Logger); // last: every patch above is in place and left alone
    }

    // The worker pool follows its knob and the world being played, on the main thread
    private void SteerPool(float dt)
    {
        if (NotNull(_api) && Finite(dt)) WorkerPool.Steer(_api.World as ClientMain);
    }

    // Patches other mods applied after Komet installed
    private static void Recheck()
    {
        AnimationFrames.Recheck();
        InitOnce.Recheck();
        ShapeInitMemo.Recheck();
    }

    // ClientEventManager.RegisterRenderer throws when another mod reserved the HUD's render order, after earlier stages went in
    private void StandDown(ICoreClientAPI api)
    {
        api.Event.LevelFinalize -= Recheck;
        if (_overlay is { } overlay)
            foreach (var stage in HudStages.Bounded(HudStageCount))
                api.Event.UnregisterRenderer(overlay, stage);
        Dispose();
    }
}

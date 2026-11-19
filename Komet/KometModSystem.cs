using Komet.Core;
using Komet.Sdk;

namespace Komet;

/// <summary>
///     The game's entry: a thin lifecycle that forwards to ClientRuntime (the composition root's run of one world)
/// </summary>
public sealed class KometModSystem : ModSystem, IDisposable
{
    private const string ModId = KometMod.Id;

    private ClientRuntime? _runtime;

    public override void Dispose()
    {
        KometSdk.Dispose();
        var runtime = _runtime ?? new ClientRuntime();
        _runtime = null;
        runtime.Stop();
    }

    public override void StartPre(ICoreAPI api)
    {
        if (NotNull(api) && NotNull(Mod.Logger))
        {
            VulkanRecord.Build = Mod.Info?.Version ?? ""; // a Vulkan failure holds for this version only
            ClientRuntime.Prepare(api, Mod.Logger, typeof(KometModSystem).Assembly);
        }
    }

    // The Shader Studio's hooks go in before any mod's StartClientSide
    public override void Start(ICoreAPI api)
    {
        if (NotNull(api)) KometSdk.Start(api);
    }

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI capi && NotNull(Mod.Logger)) ClientRuntime.AssetsLoaded(capi);
    }

    /// <summary>
    ///     Client only, though modinfo says "Universal" for MapTracking: a singleplayer server instance's Dispose would clear
    ///     the
    ///     client's statics
    /// </summary>
    /// <param name="forSide"></param>
    /// <returns></returns>
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;

        Attach(Mod.Logger);
        if (!Assert(_runtime is null) || !Assert(Mod.Info.ModID == ModId)) return;

        _runtime = new ClientRuntime();
        try
        {
            _runtime.Start(api, Mod.Logger, Mod.Info.ModID);
            KometSdk.StartClientSide(api);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // All or nothing. RegisterRenderer throws when another mod took the HUD's order.
            Mod.Logger.Error("Komet: install failed, Komet did nothing soooo it's down :(   : {0}", e);
            Dispose();
        }
    }
}

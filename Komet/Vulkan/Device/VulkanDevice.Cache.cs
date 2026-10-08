namespace Komet.Vulkan;

// Kept across starts (ShaderCache). Data from another driver or GPU is not handed over: its header
// (VkPipelineCacheHeaderVersionOne) must name this vendor, device and pipelineCacheUUID.
internal sealed unsafe partial class VulkanDevice
{
    private const int HeaderBytes = 32, HeaderVersionOne = 1;

    private readonly byte[] _cacheUuid = new byte[UuidBytes];
    private uint _vendor, _model;

    public ulong PipelineCache { get; private set; }

    private void Identify(byte* properties)
    {
        if (!Assert(properties != null) || !Assert(Vk.CacheUuidOffset + UuidBytes <= Vk.PropertiesBytes)) return;
        (_vendor, _model) = (*(uint*)(properties + Vk.VendorOffset), *(uint*)(properties + Vk.ModelOffset));
        // a bool that is no 0 or 1 says the offsets are wrong
        var (both, period) = (*(uint*)(properties + Vk.TimestampsOffset), *(float*)(properties + Vk.TimestampPeriodOffset));
        TimestampPeriod = both == 1 && float.IsFinite(period) && period is > 0 and < 1e4f ? period : 0;
        new ReadOnlySpan<byte>(properties + Vk.CacheUuidOffset, UuidBytes).CopyTo(_cacheUuid);
    }

    private void OpenCache()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(PipelineCache == 0)) return;
        var data = ShaderCache.Pipelines(_vendor, _model);
        if (data is not null && !Matches(data)) data = null;
        ulong cache;
        fixed (byte* initial = data)
        {
            var info = new Vk.PipelineCacheInfo
            {
                SType = Vk.PipelineCacheCreateInfo, InitialDataSize = (nuint)(data?.Length ?? 0), InitialData = initial
            };
            if (VkApi.CreatePipelineCache(_device, &info, null, &cache) != Vk.Success) return;
        }

        PipelineCache = cache;
        ShaderCache.PipelineBytes = data?.Length ?? 0;
    }

    private bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes || !Assert(_cacheUuid.Length == UuidBytes)) return false;
        fixed (byte* header = data)
        {
            var words = (uint*)header;
            _ = Assert(header != null);
            return words[0] >= HeaderBytes && words[0] <= data.Length && words[1] == HeaderVersionOne &&
                   words[2] == _vendor && words[3] == _model &&
                   new ReadOnlySpan<byte>(header + 16, UuidBytes).SequenceEqual(_cacheUuid);
        }
    }

    // Written while the game runs too, off the engine's thread, once the pipelines asked for are made (PipelineSet), so a
    // crash keeps what was compiled. Reading the cache's data may run beside pipeline creation, as the cache was made
    // without the externally synchronized flag. While one write is queued or runs (_keeping is 1) further asks fold into
    // it; CloseCache waits for it as for every build.
    public void KeepCache()
    {
        if (PipelineCache == 0 || !ShaderCache.Active || !Assert(_device != IntPtr.Zero)) return;
        if (Interlocked.Exchange(ref _keeping, 1) == 1) return;
        var cache = PipelineCache;
        _ = Builds.Run(() =>
        {
            Kept(cache);
            Volatile.Write(ref _keeping, 0);
        });
        _ = Assert(cache != 0);
    }

    private int _keeping;

    private void Kept(ulong cache)
    {
        nuint size = 0;
        if (!Assert(cache != 0) || !Assert(_device != IntPtr.Zero)) return;
        if (VkApi.GetPipelineCacheData(_device, cache, &size, null) == Vk.Success && size > 0 && size < int.MaxValue)
        {
            var data = new byte[(int)size];
            fixed (byte* bytes = data)
                if (VkApi.GetPipelineCacheData(_device, cache, &size, bytes) == Vk.Success &&
                    Assert(size <= (nuint)data.Length))
                    ShaderCache.KeepPipelines(_vendor, _model, data[..(int)size]);
        }
    }

    // After every build: none may still use the cache or the device
    private void CloseCache()
    {
        _ = Builds.Wait(Timeout.InfiniteTimeSpan);
        if (PipelineCache == 0 || !Assert(_device != IntPtr.Zero)) return;
        if (ShaderCache.Active) Kept(PipelineCache);
        VkApi.DestroyPipelineCache(_device, PipelineCache, null);
        PipelineCache = 0;
    }
}

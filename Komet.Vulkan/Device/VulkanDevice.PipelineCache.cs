
namespace Komet.Vulkan;

// Kept across starts (ShaderCache). VkPipelineCacheHeaderVersionOne must match vendor, device and pipelineCacheUUID.
internal sealed unsafe partial class VulkanDevice
{
    private const int HeaderBytes = 32, HeaderVersionOne = 1;

    private readonly byte[] _cacheUuid = new byte[UuidBytes];
    private int _closing; // 1 once CloseCache began: no write is queued after its wait
    private int _keeping; // 1 while a KeepCache write is queued or running
    private nuint _kept; // the cache's size when last written: the same size is not read or written again
    private uint _vendor, _model;

    public ulong PipelineCache { get; private set; }

    private void Identify(byte* properties)
    {
        (_vendor, _model) = (*(uint*)(properties + Vk.VendorOffset), *(uint*)(properties + Vk.ModelOffset));
        // a bool that is not 0 or 1 says the offsets are wrong
        var (both, period) = (*(uint*)(properties + Vk.TimestampsOffset),
            *(float*)(properties + Vk.TimestampPeriodOffset));
        TimestampPeriod = both == 1 && float.IsFinite(period) && period is > 0 and < 1e4f ? period : 0;
        new ReadOnlySpan<byte>(properties + Vk.CacheUuidOffset, UuidBytes).CopyTo(_cacheUuid);
    }

    private void OpenCache()
    {
        var data = ShaderCache.Pipelines(_vendor, _model);
        if (data is not null && !Matches(data)) data = null;

        ulong cache;
        fixed (byte* initial = data)
        {
            var info = new Vk.PipelineCacheInfo
            {
                SType = Vk.PipelineCacheCreateInfo, InitialDataSize = (nuint)(data?.Length ?? 0), InitialData = initial
            };
            if (VkApi.CreatePipelineCache(Handle, &info, null, &cache) != Vk.Success) return;
        }

        PipelineCache = cache;
        ShaderCache.PipelineBytes = data?.Length ?? 0;
    }

    private bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes) return false;

        fixed (byte* header = data)
        {
            var words = (uint*)header;
            return words[0] >= HeaderBytes && words[0] <= data.Length && words[1] == HeaderVersionOne &&
                   words[2] == _vendor && words[3] == _model &&
                   new ReadOnlySpan<byte>(header + 16, UuidBytes).SequenceEqual(_cacheUuid);
        }
    }

    // Also written during play off the engine thread once requested pipelines exist (PipelineSet), so a crash keeps
    // them. Reads may run beside pipeline creation (cache not externally synchronized). While a write is queued or
    // running (_keeping 1) asks fold into it; CloseCache waits for it. _keeping set, then _closing read (both full
    // fences, CloseCache the other way round): either this backs out or CloseCache sees the write coming.
    public void KeepCache()
    {
        if (PipelineCache == 0 || !ShaderCache.Active || Volatile.Read(ref _closing) == 1) return;

        if (Interlocked.Exchange(ref _keeping, 1) == 1) return;

        if (Volatile.Read(ref _closing) == 1)
        {
            Volatile.Write(ref _keeping, 0);
            return;
        }

        var cache = PipelineCache;
        _ = Builds.Run(() =>
        {
            try
            {
                Kept(cache);
            }
            finally
            {
                Volatile.Write(ref _keeping, 0); // a throw would stop every later write
            }
        });
    }

    private void Kept(ulong cache)
    {
        nuint size = 0;
        var sized = VkApi.GetPipelineCacheData(Handle, cache, &size, null) == Vk.Success &&
                    size is > 0 and < int.MaxValue;
        if (!sized || size == _kept)
        {
            return; // nothing new: a join finishes pipeline sets dozens of times
        }

        var data = new byte[(int)size];
        fixed (byte* bytes = data)
        {
            if (VkApi.GetPipelineCacheData(Handle, cache, &size, bytes) != Vk.Success ||
                !Assert(size <= (nuint)data.Length))
            {
                return;
            }
        }

        ShaderCache.KeepPipelines(_vendor, _model, size == (nuint)data.Length ? data : data[..(int)size]);
        _kept = size;
    }

    // After every build: none may still use the cache or the device
    private void CloseCache()
    {
        _ = Interlocked.Exchange(ref _closing, 1);
        // a write past the check may not be queued yet, so Builds.Wait would miss it
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _keeping) == 0, TimeSpan.FromSeconds(Builds.LongestSeconds));
        _ = Builds.Wait(Timeout.InfiniteTimeSpan);
        if (PipelineCache == 0) return;

        if (ShaderCache.Active) Kept(PipelineCache);

        VkApi.DestroyPipelineCache(Handle, PipelineCache, null);
        PipelineCache = 0;
    }
}

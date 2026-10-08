namespace Komet.Vulkan;

// The engine sizes its sun's glare by the samples its occlusion query's quad passes, so each Vulkan draw inside an open GL
// query gets a Vulkan query of its own and the GL query's answer is their sum, resolved before the frame slot comes round
// again. A query no Vulkan draw was in is OpenGL's to answer.
internal sealed unsafe partial class SceneRenderer
{
    private const uint SamplesPassed = 0x8914, AnySamples = 0x8C2F, QueryResult = 0x8866, QueryAvailable = 0x8867;
    private const int QueriesPerSlot = 64, MaxOcclusions = 256;

    private readonly Dictionary<uint, Occlusion> _occlusions = [];
    private ulong _queries;
    private uint _query; // the GL query open now, 0 for none
    private (long Frame, int Used) _slotQueries = (-1, 0);

    private sealed class Occlusion(bool any)
    {
        public bool Any { get; } = any;
        public List<(long Frame, uint Slot)> Slots { get; } = [];
        public long Resolved { get; set; }
        public bool Taken { get; set; } // a Vulkan draw was in it
    }

    public void Querying(uint target, uint id)
    {
        if (target is not (SamplesPassed or AnySamples) || !Assert(_occlusions.Count <= MaxOcclusions)) return;
        _ = Assert(id == 0 || _query == 0); // OpenGL has one query of a target open at a time
        _query = id;
        if (id == 0) return;
        if (_occlusions.Count >= MaxOcclusions) _occlusions.Clear();
        _occlusions[id] = new Occlusion(target == AnySamples);
    }

    private long QueryBegin()
    {
        var frame = _terrain.Frame;
        if (_query == 0 || !_occlusions.TryGetValue(_query, out var occlusion) || !Assert(frame.Open)) return -1;
        _ = Assert(occlusion.Slots.Count <= QueriesPerSlot);
        if (_queries == 0 && !MadePool()) return -1;
        var first = (uint)(frame.Number % VulkanFrame.Slots * QueriesPerSlot);
        if (_slotQueries.Frame != frame.Number)
        {
            VkApi.CmdResetQueryPool(frame.Setup, _queries, first, QueriesPerSlot); // the setup runs before any rendering
            _slotQueries = (frame.Number, 0);
        }

        if (_slotQueries.Used >= QueriesPerSlot) return -1;
        var slot = first + (uint)_slotQueries.Used++;
        occlusion.Slots.Add((frame.Number, slot));
        occlusion.Taken = true;
        frame.Post(new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.BeginQuery, Pipeline = _queries, Count = slot,
            First = _terrain.Device.PreciseQueries ? Vk.QueryPrecise : 0
        });
        return slot;
    }

    private void QueryEnd(long slot)
    {
        if (slot < 0 || !Assert(_terrain.Frame.Open) || !Assert(_queries != 0)) return;
        _terrain.Frame.Post(new VulkanFrame.Op { Kind = VulkanFrame.OpKind.EndQuery, Pipeline = _queries, Count = (uint)slot });
    }

    private bool MadePool()
    {
        if (!Assert(_queries == 0)) return false;
        _queries = _terrain.Device.QueryPool(Vk.QueryOcclusion, QueriesPerSlot * VulkanFrame.Slots);
        return _queries != 0 && Assert(_terrain.Device is not null);
    }

    public long? Answer(uint id, uint name)
    {
        if (!_occlusions.TryGetValue(id, out var occlusion) || !occlusion.Taken) return null;
        if (name is not (QueryResult or QueryAvailable) || !Assert(_queries != 0)) return null;
        var (samples, available) = (occlusion.Resolved, true);
        foreach (var (_, slot) in occlusion.Slots.Bounded(QueriesPerSlot))
        {
            var (value, ready) = Result(slot, name == QueryResult);
            (samples, available) = (samples + value, available && ready);
        }

        _ = Assert(samples >= 0);
        if (name == QueryAvailable) return available ? 1 : 0;
        return occlusion.Any ? Math.Min(samples, 1) : samples;
    }

    private (long Samples, bool Ready) Result(uint slot, bool wait)
    {
        if (wait && _terrain.Frame.Open) _terrain.Frame.Close("an occlusion query's result asked for");
        var data = stackalloc ulong[2];
        var flags = Vk.Result64 | Vk.ResultAvailability | (wait ? Vk.ResultWait : 0);
        var result = VkApi.GetQueryPoolResults(_terrain.Device.Handle, _queries, slot, 1, 16, data, 16, flags);
        _ = Assert(result is Vk.Success or 1) && Assert(slot < QueriesPerSlot * VulkanFrame.Slots); // VK_NOT_READY is 1
        return ((long)data[0], data[1] != 0);
    }

    public void Resolve()
    {
        if (_queries == 0 || !Assert(_occlusions.Count <= MaxOcclusions) || !Assert(!_terrain.Frame.Open)) return;
        using var occlusions = _occlusions.Values.GetEnumerator(); // every frame: no iterator; nothing here adds or removes one
        for (var o = 0; o < MaxOcclusions && occlusions.MoveNext(); o++)
        {
            var occlusion = occlusions.Current;
            var kept = 0;
            for (var i = 0; i < Math.Min(occlusion.Slots.Count, QueriesPerSlot); i++)
            {
                var (frame, slot) = occlusion.Slots[i];
                if (frame <= _terrain.Frame.Done) occlusion.Resolved += Result(slot, true).Samples;
                else occlusion.Slots[kept++] = occlusion.Slots[i];
            }

            occlusion.Slots.RemoveRange(kept, occlusion.Slots.Count - kept);
        }
    }

    private void QueriesGone()
    {
        if (_queries != 0) VkApi.DestroyQueryPool(_terrain.Device.Handle, _queries, null);
        _queries = 0;
        _occlusions.Clear();
        _ = Assert(_query < uint.MaxValue) && Assert(_slotQueries.Used <= QueriesPerSlot);
    }
}

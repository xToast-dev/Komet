using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// Enough for Komet's compute and the engine's shaders: storage/uniform buffers, sampled and storage images (Resources),
// and a block's member offsets and strides as compiled, so a filler writes where the shader reads (Members).
internal static class Spirv
{
    public const uint Magic = 0x07230203;
    // VkDescriptorType: a combined image sampler, a separate (sampled) image, a storage image, uniform and storage buffers
    public const int CombinedSampler = 1, SeparateImage = 2, StorageImage = 3, UniformBuffer = 6, StorageBuffer = 7;
    private const int MaxWords = 1 << 22, MaxIds = 1 << 20, MaxResources = 64, Header = 5;

    private const int OpTypeImage = 25, OpTypeSampledImage = 27, OpTypeStruct = 30, OpTypePointer = 32, OpVariable = 59;
    private const int OpDecorate = 71, OpTypeArray = 28, OpTypeRuntimeArray = 29;
    private const int DecorationBufferBlock = 3, DecorationBinding = 33, DecorationSet = 34;
    private const int UniformConstant = 0, Uniform = 2, StorageBufferClass = 12;
    private const int OpName = 5, OpMemberName = 6, OpMemberDecorate = 72;
    private const int DecorationOffset = 35, DecorationArrayStride = 6, MaxMembers = 1024;

    public readonly record struct Resource(int Set, int Binding, int Type);
    public readonly record struct Member(string Name, int Offset, int Stride);

    public static Resource[] Resources(ReadOnlySpan<uint> words)
    {
        if (words.Length < Header || words[0] != Magic || !Assert(words.Length <= MaxWords)) return [];
        var bound = (int)Math.Min(words[3], MaxIds);
        var ids = new Id[bound];
        var variables = new List<int>();
        var at = Header;
        for (var step = 0; step < MaxWords && at < words.Length; step++)
        {
            var (count, op) = ((int)(words[at] >> 16), (int)(words[at] & 0xFFFF));
            if (count == 0 || at + count > words.Length) break;
            Read(words.Slice(at, count), op, ids, variables);
            at += count;
        }

        var resources = new List<Resource>();
        foreach (var variable in variables.Bounded(MaxResources))
        {
            // Through the pointer and any arrays to an image, sampled image or block
            var (storage, type) = (ids[variable].Storage, ids[variable].Of);
            for (var hop = 0; hop < 8 && Index(type, ids.Length) && ids[type].Kind == 0; hop++) type = ids[type].Of;
            if (!Index(type, ids.Length)) continue;
            var descriptor = (storage, ids[type].Kind) switch
            {
                (UniformConstant, CombinedSampler) => CombinedSampler,
                (UniformConstant, SeparateImage) => SeparateImage,
                (UniformConstant, StorageImage) => StorageImage,
                (StorageBufferClass, -1) => StorageBuffer,
                (Uniform, -1) => ids[type].BufferBlock ? StorageBuffer : UniformBuffer,
                _ => 0
            };
            if (descriptor > 0) resources.Add(new Resource(ids[variable].Set, ids[variable].Binding, descriptor));
        }

        resources.Sort((a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));
        return [.. resources];
    }

    private static void Read(ReadOnlySpan<uint> op, int code, Id[] ids, List<int> variables)
    {
        if (op.Length < 2) return;
        switch (code)
        {
            case OpDecorate when op.Length >= 3 && op[1] < ids.Length:
                ref var target = ref ids[op[1]];
                if (op[2] == DecorationBinding && op.Length >= 4) target.Binding = (int)op[3];
                else if (op[2] == DecorationSet && op.Length >= 4) target.Set = (int)op[3];
                else if (op[2] == DecorationBufferBlock) target.BufferBlock = true;
                break;
            case OpVariable when op.Length >= 4 && op[2] < ids.Length:
                (ids[op[2]].Of, ids[op[2]].Storage) = ((int)op[1], (int)op[3]);
                if (op[3] is UniformConstant or Uniform or StorageBufferClass) variables.Add((int)op[2]);
                break;
            case OpTypePointer or OpTypeArray or OpTypeRuntimeArray when op.Length >= 3 && op[1] < ids.Length:
                ids[op[1]].Of = (int)op[code == OpTypePointer ? 3 : 2];
                break;
            case OpTypeImage when op.Length >= 8 && op[1] < ids.Length:
                ids[op[1]].Kind = op[7] == 2 ? StorageImage : SeparateImage; // Sampled 2: a storage image
                break;
            case OpTypeSampledImage or OpTypeStruct when op[1] < ids.Length:
                ids[op[1]].Kind = code == OpTypeSampledImage ? CombinedSampler : -1;
                break;
        }
    }

    public static Member[] Members(ReadOnlySpan<uint> words, string block)
    {
        if (words.Length < Header || words[0] != Magic || !Assert(block.Length > 0)) return [];
        var layout = new Layout();
        var at = Header;
        for (var step = 0; step < MaxWords && at < words.Length; step++)
        {
            var (count, op) = ((int)(words[at] >> 16), (int)(words[at] & 0xFFFF));
            if (count == 0 || at + count > words.Length) break;
            Note(words.Slice(at, count), op, layout);
            at += count;
        }

        var id = -1;
        foreach (var (key, name) in layout.Names.Bounded(MaxIds))
            if (name == block && layout.Structs.ContainsKey(key))
                id = key;
        if (!layout.Structs.TryGetValue(id, out var types)) return [];
        var members = new Member[Math.Min(types.Length, MaxMembers)];
        for (var i = 0; i < Math.Min(members.Length, MaxMembers); i++)
            members[i] = new Member(layout.MemberNames.GetValueOrDefault((id, i), ""),
                layout.Offsets.GetValueOrDefault((id, i), -1), layout.Strides.GetValueOrDefault((int)types[i]));
        return members;
    }

    private static void Note(ReadOnlySpan<uint> op, int code, Layout layout)
    {
        switch (code)
        {
            case OpName when op.Length >= 3:
                layout.Names[(int)op[1]] = Text(op[2..]);
                break;
            case OpMemberName when op.Length >= 4:
                layout.MemberNames[((int)op[1], (int)op[2])] = Text(op[3..]);
                break;
            case OpMemberDecorate when op.Length >= 5 && op[3] == DecorationOffset:
                layout.Offsets[((int)op[1], (int)op[2])] = (int)op[4];
                break;
            case OpDecorate when op.Length >= 4 && op[2] == DecorationArrayStride:
                layout.Strides[(int)op[1]] = (int)op[3];
                break;
            case OpTypeStruct when op.Length >= 2:
                layout.Structs[(int)op[1]] = op[2..].ToArray();
                break;
        }
    }

    // UTF-8, zero-terminated, four bytes a word
    private static string Text(ReadOnlySpan<uint> words)
    {
        var bytes = MemoryMarshal.AsBytes(words);
        var end = bytes.IndexOf((byte)0);
        if (!Assert(end >= 0)) return "";
        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private struct Id
    {
        public int Of, Storage, Kind, Set, Binding;
        public bool BufferBlock;
    }

    private sealed class Layout
    {
        public Dictionary<int, string> Names { get; } = [];
        public Dictionary<(int, int), string> MemberNames { get; } = [];
        public Dictionary<(int, int), int> Offsets { get; } = [];
        public Dictionary<int, int> Strides { get; } = [];
        public Dictionary<int, uint[]> Structs { get; } = [];
    }
}

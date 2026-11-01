namespace Komet.Vulkan;

internal sealed unsafe class Samplers(IntPtr device) : IDisposable
{
    private const int MaxSamplers = 1024;
    private const uint Nearest = 0, Linear = 1, LessOrEqual = 3;
    private const int GlNearest = 0x2600, GlLinear = 0x2601, GlNearestMipmapNearest = 0x2700, GlLinearMipmapNearest = 0x2701;
    private const int GlNearestMipmapLinear = 0x2702, GlLinearMipmapLinear = 0x2703;
    private const int GlRepeat = 0x2901, GlMirroredRepeat = 0x8370, GlClampToEdge = 0x812F, GlClampToBorder = 0x812D;
    private const uint Repeat = 0, MirroredRepeat = 1, ClampToEdge = 2, ClampToBorder = 3, OpaqueBlack = 3, OpaqueWhite = 5;

    private readonly Dictionary<State, ulong> _made = [];

    // Mipmaps: 0 none, 1 nearest, 2 linear. U, V, W: VkSamplerAddressMode along S, T and R (default: repeat, as OpenGL's).
    public readonly record struct State(bool LinearMag, bool LinearMin, int Mipmaps, bool Compare)
    {
        public uint U { get; init; }
        public uint V { get; init; }
        public uint W { get; init; }
        public bool WhiteBorder { get; init; }
    }

    // Nearest, no mipmaps, clamped to the edge: for images read texel by texel
    public static State Clamped { get; } = FromGl(GlNearest, GlNearest, GlClampToEdge, false);

    // wrapT: the wrap along T, if apart from S; R wraps as S
    public static State FromGl(int min, int mag, int wrap, bool compare, int? wrapT = null)
    {
        var (linearMin, mipmaps) = Minifying(min);
        var u = Address(wrap);
        return new State(mag == GlLinear, linearMin, mipmaps, compare) { U = u, V = Address(wrapT ?? wrap), W = u };
    }

    // OpenGL's wrap as Vulkan's address mode; the rest (GL_CLAMP, mirror-clamp) clamps to the edge
    private static uint Address(int wrap)
    {
        return wrap switch
        {
            GlRepeat => Repeat,
            GlMirroredRepeat => MirroredRepeat,
            GlClampToBorder => ClampToBorder,
            _ => ClampToEdge
        };
    }

    // State after one glTexParameter: FromGl's parameters (and border red) change it, others keep it; null if unknown
    public static State? Tuned(State state, uint name, float value)
    {
        var v = (int)value;
        return name switch
        {
            0x2801 => Minifying(v) is var (linear, mipmaps) ? state with { LinearMin = linear, Mipmaps = mipmaps } : null,
            0x2800 => state with { LinearMag = v == GlLinear },
            0x2802 => state with { U = Address(v), W = Address(v) },
            0x2803 => state with { V = Address(v) },
            0x8072 => state with { W = Address(v) },
            0x884C => state with { Compare = v != 0 },
            0x1004 => state with { WhiteBorder = value > 0.5f },
            0x884D or 0x84FE => state, // the compare function (always LEQUAL here) and anisotropy: not followed
            _ => null
        };
    }

    private static (bool Linear, int Mipmaps) Minifying(int min)
    {
        var mipmaps = min switch
        {
            GlNearestMipmapNearest or GlLinearMipmapNearest => 1,
            GlNearestMipmapLinear or GlLinearMipmapLinear => 2,
            _ => 0
        };
        return (min is GlLinear or GlLinearMipmapNearest or GlLinearMipmapLinear, mipmaps);
    }

    public ulong Get(State state)
    {
        if (!Assert(state.Mipmaps is >= 0 and <= 2) || !Assert((state.U | state.V | state.W) <= ClampToBorder)) return 0;
        if (_made.TryGetValue(state, out var known)) return known;
        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, MagFilter = state.LinearMag ? Linear : Nearest,
            MinFilter = state.LinearMin ? Linear : Nearest, MipmapMode = state.Mipmaps == 2 ? Linear : Nearest,
            AddressU = state.U, AddressV = state.V, AddressW = state.W, MaxLod = state.Mipmaps == 0 ? 0.25f : 1000,
            MaxAnisotropy = 1, Compare = state.Compare ? 1u : 0, CompareOp = LessOrEqual,
            Border = state.WhiteBorder ? OpaqueWhite : OpaqueBlack
        };
        ulong sampler;
        if (_made.Count >= MaxSamplers || VkApi.CreateSampler(device, &info, null, &sampler) != Vk.Success) return 0;
        _made[state] = sampler;
        return sampler;
    }

    public void Dispose()
    {
        foreach (var sampler in _made.Values.Bounded(MaxSamplers)) VkApi.DestroySampler(device, sampler, null);
        _made.Clear();
    }
}

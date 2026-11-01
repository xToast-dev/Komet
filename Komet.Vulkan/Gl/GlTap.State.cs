using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// The state a draw's pipeline depends on (caps, depth, cull, blend and draw buffers per framebuffer), read from GL once
// when tapping begins and followed by the state taps. Blend factors/equations in Vulkan's numbering, Unsupported if none.
internal static unsafe partial class GlTap
{
    public const int MaxBuffers = 8;
    public const uint Unsupported = 15;

    [Flags]
    public enum Caps : uint
    {
        None = 0, DepthTest = 1, CullFace = 2, DepthClamp = 4, PolygonOffsetFill = 8, Scissor = 16, Stencil = 32,
        Discard = 64, // GL_RASTERIZER_DISCARD
        AlphaToCoverage = 128, LogicOp = 256,
        LineSmooth = 512,
        // Not OpenGL capabilities: polygons drawn as edges or corners (glPolygonMode), added for the pipeline
        WireLines = 1024, WirePoints = 2048
    }

    // Where glLogicOp's operation (counted from GL_CLEAR) sits in a draw's caps: bits 12 to 15
    public const uint LogicOperationBits = 0xF000;

    private const uint GlBlend = 0x0BE2, GlDepthTest = 0x0B71, GlCull = 0x0B44, GlDepthClamp = 0x864F;
    private const uint GlOffsetFill = 0x8037, GlScissor = 0x0C11, GlStencil = 0x0B90, GlDiscard = 0x8C89;
    private const uint GlAlphaToCoverage = 0x809E, GlLogicOp = 0x0BF2, GlLineSmooth = 0x0B20, GlLess = 0x201, GlBack = 0x405, GlCcw = 0x901;
    private const uint GlDrawFramebuffer = 0x8CA9, GlFramebuffer = 0x8D40, GlColor0 = 0x8CE0;
    private const int EnableAt = 22, DisableAt = 23, EnableiAt = 24, DisableiAt = 25, FuncAt = 26, FuncSeparateAt = 27;
    private const int FunciAt = 28, FuncSeparateiAt = 29, EquationAt = 30, EquationSeparateAt = 31, EquationiAt = 32;
    private const int EquationSeparateiAt = 33, DepthFuncAt = 34, DepthMaskAt = 35, ColorMaskAt = 36, ColorMaskiAt = 37;
    private const int CullFaceAt = 38, FrontFaceAt = 39, ViewportAt = 40, BindFramebufferAt = 41, OffsetAt = 42;
    private const int DrawBuffersAt = 43, DrawBufferAt = 44, NamedDrawBuffersAt = 45, NamedDrawBufferAt = 46;
    private const int DeleteFramebuffersAt = 47, DeleteProgramAt = 48, DeleteTexturesAt = 49;
    private const int MaxSets = 256, MaxFramebuffers = 256;
    private const int AllBuffers = -1; // SetFunc and the others: every draw buffer

    // Per draw buffer: bits 0-3 src color factor, 4-7 dst color, 8-11 src alpha, 12-15 dst alpha, 16-18 color eq, 19-21
    // alpha eq, 22-25 write mask (rgba), 26 blending on
    private static readonly uint[] Blends = new uint[MaxBuffers];
    // Each framebuffer's glDrawBuffers, and DrawBuffersOf's answers for them
    private static readonly Dictionary<uint, uint[]> DrawBuffers = [];
    private static readonly Dictionary<uint, int[]> DrawOutputs = [];

    // Each set of draw buffers seen, once, with its outputs: the engine switches some framebuffers between two sets every
    // frame, a new array pair each switch otherwise. Read only, shared by DrawBuffers and DrawOutputs.
    private static readonly Dictionary<(ulong, ulong, ulong, ulong), (uint[] Buffers, int[] Outputs)> Sets = [];
    private static Caps _caps;
    private static uint _depthFunc = GlLess, _cullFace = GlBack, _frontFace = GlCcw, _framebuffer;
    private static bool _depthMask = true;
    private static float _offsetFactor, _offsetUnits;
    private static (int X, int Y, int Width, int Height) _viewport;

    // A draw pipeline's dependencies, cheap to compare and hash. Depth: bits 0-2 compare op (Vulkan), 3 write, 4-5 cull
    // mode (Vulkan), 6 front face CCW (GL). Stencil: test ops (StencilWord), 0 when off or untapped
    public readonly record struct Fixed(uint Caps, uint Depth, float OffsetFactor, float OffsetUnits, BlendState Blend,
        uint Stencil = 0)
    {
        public bool DepthTest => (Caps & (uint)GlTap.Caps.DepthTest) != 0;
        public uint DepthCompare => Depth & 7;
        public bool DepthWrite => (Depth & 8) != 0;
        public uint Culls => (Depth >> 4) & 3; // Vulkan's cull mode
        public bool CounterClockwise => (Depth & 64) != 0;
    }

    public readonly record struct BlendState(uint B0, uint B1, uint B2, uint B3, uint B4, uint B5, uint B6, uint B7)
    {
        public uint this[int buffer] => buffer switch
        {
            0 => B0, 1 => B1, 2 => B2, 3 => B3, 4 => B4, 5 => B5, 6 => B6, _ => B7
        };

        public bool Blending(int buffer) => ((this[buffer] >> 26) & 1) != 0;
    }

    // The state with the given draw buffers' alpha writes masked (RGB in GL, RGBA in Vulkan: alpha stays 1)
    public static Fixed WithoutAlpha(Fixed state, ReadOnlySpan<bool> buffers)
    {
        Span<uint> words = stackalloc uint[MaxBuffers];
        for (var i = 0; i < MaxBuffers; i++) words[i] = state.Blend[i] & (i < buffers.Length && buffers[i] ? ~(1u << 25) : ~0u);
        return state with
        {
            Blend = new BlendState(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7])
        };
    }

    public static Fixed State
    {
        get
        {
            var cull = (_caps & Caps.CullFace) == 0 ? 0u : CullMode(_cullFace);
            var depth = ((_depthFunc - 0x200) & 7) | (_depthMask ? 8u : 0) | (cull << 4) |
                        (_frontFace == GlCcw ? 64u : 0);
            return new Fixed((uint)_caps, depth, _offsetFactor, _offsetUnits,
                new BlendState(Blends[0], Blends[1], Blends[2], Blends[3], Blends[4], Blends[5], Blends[6], Blends[7]),
                Tapped ? StencilWord() : 0);
        }
    }

    public static (int X, int Y, int Width, int Height) Viewport => _viewport;

    // Bumps with every change of the state, program, texture or sampler bindings: the same number means the same state
    public static long Version { get; private set; }

    public static int DrawFramebuffer => (int)_framebuffer;

    public static long BufferChanges { get; private set; }

    private static uint CullMode(uint face) => face switch { 0x404 => 1u, GlBack => 2u, 0x408 => 3u, _ => 0u };

    // Attachment per fragment output (-1: none) per draw buffers; asked once if unset while tapped (bound for drawing).
    // Past MaxFramebuffers a new framebuffer's are asked each time
    public static int[] DrawBuffersOf(int framebuffer)
    {
        if (!Assert(framebuffer > 0)) return [];
        if (DrawOutputs.TryGetValue((uint)framebuffer, out var outputs)) return outputs; // shared: callers only read it
        if (!DrawBuffers.TryGetValue((uint)framebuffer, out var buffers))
        {
            buffers = new uint[MaxBuffers];
            if (Defaulted((uint)framebuffer, false)) buffers[0] = GlColor0; // the rest GL_NONE
            else AskDrawBuffers((uint)framebuffer, buffers);
        }

        var set = Set(buffers);
        if (DrawBuffers.Count < MaxFramebuffers || DrawBuffers.ContainsKey((uint)framebuffer))
            (DrawBuffers[(uint)framebuffer], DrawOutputs[(uint)framebuffer]) = (set.Buffers, set.Outputs);
        return set.Outputs;
    }

    // GL_DRAW_BUFFERi answers for the draw framebuffer only: another (a named clear's, a named blit's target) is bound
    // for the question past the tap, then the draw framebuffer again. Once per framebuffer, the answer is kept
    private static void AskDrawBuffers(uint framebuffer, uint[] buffers)
    {
        var bind = Tapped && framebuffer != _framebuffer && Original[BindFramebufferAt] != IntPtr.Zero
            ? (delegate* unmanaged<uint, uint, void>)Original[BindFramebufferAt]
            : null;
        if (bind != null) bind(GlDrawFramebuffer, framebuffer);
        for (var i = 0; i < MaxBuffers; i++) buffers[i] = (uint)GL.GetInteger((GetPName)(0x8825 + i));
        if (bind != null) bind(GlDrawFramebuffer, _framebuffer);
    }

    // The kept pair of these buffers (made the first time; past MaxSets, new ones unkept)
    private static (uint[] Buffers, int[] Outputs) Set(ReadOnlySpan<uint> buffers)
    {
        var key = (Pair(buffers, 0), Pair(buffers, 2), Pair(buffers, 4), Pair(buffers, 6));
        if (Sets.TryGetValue(key, out var set)) return set;
        var outputs = new int[MaxBuffers];
        for (var i = 0; i < MaxBuffers; i++)
            outputs[i] = buffers[i] is >= GlColor0 and < GlColor0 + 16 ? (int)(buffers[i] - GlColor0) : -1;
        set = (buffers.ToArray(), outputs);
        if (Sets.Count < MaxSets) Sets[key] = set;
        return set;
    }

    private static ulong Pair(ReadOnlySpan<uint> buffers, int at) => buffers[at] | ((ulong)buffers[at + 1] << 32);

    // GL_BLEND_* factor as VkBlendFactor
    private static uint Factor(uint gl) => gl switch
    {
        0 => 0, 1 => 1, 0x300 => 2, 0x301 => 3, 0x306 => 4, 0x307 => 5, 0x302 => 6, 0x303 => 7, 0x304 => 8, 0x305 => 9,
        0x8001 => 10, 0x8002 => 11, 0x8003 => 12, 0x8004 => 13, 0x308 => 14, _ => Unsupported
    };

    // Blend equation as VkBlendOp
    private static uint Equation(uint gl) => gl switch
    {
        0x8006 => 0, 0x800A => 1, 0x800B => 2, 0x8007 => 3, 0x8008 => 4, _ => 7
    };

    // buffer: one draw buffer, or AllBuffers; the word is worked out once for all of them
    private static void SetFunc(int buffer, (uint Sc, uint Dc, uint Sa, uint Da) f)
    {
        var word = Factor(f.Sc) | (Factor(f.Dc) << 4) | (Factor(f.Sa) << 8) | (Factor(f.Da) << 12);
        Apply(buffer, ~0xFFFFu, word);
    }

    private static void SetEquation(int buffer, uint color, uint alpha)
    {
        var word = (Equation(color) << 16) | (Equation(alpha) << 19);
        Apply(buffer, ~(0x3Fu << 16), word);
    }

    private static void SetMask(int buffer, bool r, bool g, bool b, bool a)
    {
        var mask = (r ? 1u : 0) | (g ? 2u : 0) | (b ? 4u : 0) | (a ? 8u : 0);
        Apply(buffer, ~(0xFu << 22), mask << 22);
    }

    private static void SetBlending(int buffer, bool on) => Apply(buffer, ~(1u << 26), on ? 1u << 26 : 0);

    // The bits outside keep set to value, in the buffer's word or every buffer's
    private static void Apply(int buffer, uint keep, uint value)
    {
        if (buffer != AllBuffers && !Index(buffer, MaxBuffers)) return;
        var (from, to) = buffer == AllBuffers ? (0, MaxBuffers) : (buffer, buffer + 1);
        var changed = false;
        for (var i = from; i < Math.Min(to, MaxBuffers); i++)
        {
            var word = (Blends[i] & keep) | value;
            changed |= word != Blends[i];
            Blends[i] = word;
        }

        if (changed) Version++; // the same blend again leaves the terrain's recorded context valid
    }

    private static Caps Cap(uint cap) => cap switch
    {
        GlDepthTest => Caps.DepthTest, GlCull => Caps.CullFace, GlDepthClamp => Caps.DepthClamp,
        GlOffsetFill => Caps.PolygonOffsetFill, GlScissor => Caps.Scissor, GlStencil => Caps.Stencil,
        GlDiscard => Caps.Discard, GlAlphaToCoverage => Caps.AlphaToCoverage, GlLogicOp => Caps.LogicOp,
        GlLineSmooth => Caps.LineSmooth, _ => Caps.None
    };

    private static void Toggle(uint cap, bool on)
    {
        if (cap == GlBlend)
        {
            SetBlending(AllBuffers, on);
            return;
        }

        var (bit, was) = (Cap(cap), _caps);
        _caps = on ? _caps | bit : _caps & ~bit;
        if (_caps != was) Version++;
    }

    // Past MaxFramebuffers a new framebuffer's buffers are not kept: DrawBuffersOf asks OpenGL for them
    private static void SetBuffers(uint framebuffer, ReadOnlySpan<uint> buffers)
    {
        if (FramebufferRecords.TryGetValue(framebuffer, out var f)) f.DrawDefault = false;
        Span<uint> asked = stackalloc uint[MaxBuffers];
        asked.Clear();
        for (var i = 0; i < Math.Min(buffers.Length, MaxBuffers); i++) asked[i] = buffers[i];
        // the same again (the engine's OIT sets them every frame): nothing changed, no array and no rebuilt outputs
        var known = DrawBuffers.TryGetValue(framebuffer, out var held);
        if (known && asked.SequenceEqual(held)) return;
        (BufferChanges, Version) = (BufferChanges + 1, Version + 1);
        if (!known && DrawBuffers.Count >= MaxFramebuffers) return;
        var set = Set(asked);
        (DrawBuffers[framebuffer], DrawOutputs[framebuffer]) = (set.Buffers, set.Outputs);
    }

    private static void Seed()
    {
        _caps = Caps.None;
        foreach (var cap in (ReadOnlySpan<uint>)[GlDepthTest, GlCull, GlDepthClamp, GlOffsetFill, GlScissor, GlStencil,
                     GlDiscard, GlAlphaToCoverage, GlLogicOp, GlLineSmooth])
            if (GL.IsEnabled((EnableCap)cap))
                _caps |= Cap(cap);
        for (var i = 0; i < MaxBuffers; i++) SeedBuffer(i);
        (_depthFunc, _depthMask) = ((uint)GL.GetInteger(GetPName.DepthFunc), GL.GetBoolean(GetPName.DepthWritemask));
        (_cullFace, _frontFace) = ((uint)GL.GetInteger(GetPName.CullFaceMode), (uint)GL.GetInteger(GetPName.FrontFace));
        (_offsetFactor, _offsetUnits) = (GL.GetFloat(GetPName.PolygonOffsetFactor),
            GL.GetFloat(GetPName.PolygonOffsetUnits));
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        _viewport = (viewport[0], viewport[1], viewport[2], viewport[3]);
        _framebuffer = (uint)GL.GetInteger(GetPName.DrawFramebufferBinding);
        _readFramebuffer = (uint)GL.GetInteger(GetPName.ReadFramebufferBinding);
        DrawBuffers.Clear();
        DrawOutputs.Clear();
        var active = GL.GetInteger(GetPName.ActiveTexture);
        for (var unit = 0; unit < MaxUnits; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            UnitTextures[unit] = (uint)GL.GetInteger(GetPName.TextureBinding2D);
            UnitSamplers[unit] = (uint)GL.GetInteger(GetPName.SamplerBinding);
        }

        GL.ActiveTexture((TextureUnit)active);
        (_unit, _current) = ((uint)(active - (int)Texture0), (uint)GL.GetInteger(GetPName.CurrentProgram));
        _using = Programs.GetValueOrDefault(_current); // UseProgram only looks again when the program changes
    }

    private static void SeedBuffer(int i)
    {
        int Get(int name)
        {
            GL.GetInteger((GetIndexedPName)name, i, out int value);
            return value;
        }

        SetFunc(i, ((uint)Get(0x80C9), (uint)Get(0x80C8), (uint)Get(0x80CB), (uint)Get(0x80CA)));
        SetEquation(i, (uint)Get(0x8009), (uint)Get(0x883D));
        var mask = new bool[4];
        GL.GetBoolean((GetIndexedPName)0x0C23, i, mask);
        SetMask(i, mask[0], mask[1], mask[2], mask[3]);
        SetBlending(i, GL.IsEnabled((IndexedEnableCap)GlBlend, i));
    }

    private static Entry[] StateEntries() =>
    [
        new("glEnable", (IntPtr)(delegate* unmanaged<uint, void>)&Enable),
        new("glDisable", (IntPtr)(delegate* unmanaged<uint, void>)&Disable),
        new("glEnablei", (IntPtr)(delegate* unmanaged<uint, uint, void>)&Enablei),
        new("glDisablei", (IntPtr)(delegate* unmanaged<uint, uint, void>)&Disablei),
        new("glBlendFunc", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BlendFunc),
        new("glBlendFuncSeparate", (IntPtr)(delegate* unmanaged<uint, uint, uint, uint, void>)&BlendFuncSeparate),
        new("glBlendFunci", (IntPtr)(delegate* unmanaged<uint, uint, uint, void>)&BlendFunci),
        new("glBlendFuncSeparatei",
            (IntPtr)(delegate* unmanaged<uint, uint, uint, uint, uint, void>)&BlendFuncSeparatei),
        new("glBlendEquation", (IntPtr)(delegate* unmanaged<uint, void>)&BlendEquation),
        new("glBlendEquationSeparate", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BlendEquationSeparate),
        new("glBlendEquationi", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BlendEquationi),
        new("glBlendEquationSeparatei", (IntPtr)(delegate* unmanaged<uint, uint, uint, void>)&BlendEquationSeparatei),
        new("glDepthFunc", (IntPtr)(delegate* unmanaged<uint, void>)&DepthFunc),
        new("glDepthMask", (IntPtr)(delegate* unmanaged<byte, void>)&DepthMask),
        new("glColorMask", (IntPtr)(delegate* unmanaged<byte, byte, byte, byte, void>)&ColorMask),
        new("glColorMaski", (IntPtr)(delegate* unmanaged<uint, byte, byte, byte, byte, void>)&ColorMaski),
        new("glCullFace", (IntPtr)(delegate* unmanaged<uint, void>)&CullFace),
        new("glFrontFace", (IntPtr)(delegate* unmanaged<uint, void>)&FrontFace),
        new("glViewport", (IntPtr)(delegate* unmanaged<int, int, int, int, void>)&SetViewport),
        new("glBindFramebuffer", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindFramebuffer),
        new("glPolygonOffset", (IntPtr)(delegate* unmanaged<float, float, void>)&PolygonOffset),
        new("glDrawBuffers", (IntPtr)(delegate* unmanaged<int, uint*, void>)&SetDrawBuffers),
        new("glDrawBuffer", (IntPtr)(delegate* unmanaged<uint, void>)&DrawBuffer),
        new("glNamedFramebufferDrawBuffers", (IntPtr)(delegate* unmanaged<uint, int, uint*, void>)&NamedDrawBuffers),
        new("glNamedFramebufferDrawBuffer", (IntPtr)(delegate* unmanaged<uint, uint, void>)&NamedDrawBuffer),
        new("glDeleteFramebuffers", (IntPtr)(delegate* unmanaged<int, uint*, void>)&DeleteFramebuffers),
        new("glDeleteProgram", (IntPtr)(delegate* unmanaged<uint, void>)&DeleteProgram),
        new("glDeleteTextures", (IntPtr)(delegate* unmanaged<int, uint*, void>)&DeleteTextures)
    ];
}

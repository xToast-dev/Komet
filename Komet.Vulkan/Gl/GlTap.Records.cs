using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;
using Attachments = Komet.Vulkan.GlFramebuffer;

namespace Komet.Vulkan;

// What the taps learn of GL objects as the engine makes them (textures, framebuffers, samplers, shaders and programs,
// buffer sizes), so Vulkan never asks GL what it told GL. Objects made before the tap (or while untapped) are asked once
// on first sight, then followed. Answers are GL's own: a driver-chosen format (GL_RGBA) is asked once per format and type.
internal static unsafe partial class GlTap
{
    private const int MaxLevels = 16, MaxRecords = 1 << 16, MaxProgramRecords = 4096, MaxNamed = 1024;
    private const int MaxTextureRecords = MaxRecords + MaxRenderbuffers; // textures up to MaxRecords, stand-ins past it
    private const int GlTextureTarget = 0x1006, GlTextureWidth = 0x1000, GlTextureHeight = 0x1001;
    private const int GlTextureDepth = 0x8071, GlInternalFormat = 0x1003, GlMaxLevel = 0x813D, GlBorderColor = 0x1004,
        GlBaseLevel = 0x813C;
    private const int GlMinFilter = 0x2801, GlMagFilter = 0x2800, GlWrapS = 0x2802, GlWrapT = 0x2803;
    private const int GlCompareMode = 0x884C, GlCompareFunc = 0x884D;
    private const uint InvalidIndex = uint.MaxValue;

    // What OpenGL holds of a texture; Levels: bit n set while level n is specified
    private sealed class TextureRecord
    {
        public uint Target;
        public int Width, Height, Depth = 1, Format, Levels;
        public uint Type;
        public int Min = 0x2702, Mag = 0x2601, WrapS = 0x2901, WrapT = 0x2901, Compare, CompareFunc = 0x0203;
        public int MaxLevel = 1000, BaseLevel;
        public (float R, float G, float B, float A) Border;

        // Bound to an unfollowed target (3D, rectangle...) or specified by an unheard call: asked from GL each time
        public bool Untracked;
    }

    // Fresh: seen made; DrawDefault, ReadDefault: its draw or read buffers are GL's defaults until set; Renderbuffers:
    // renderbuffer attachment bits (names)
    private sealed class FramebufferRecord
    {
        public readonly Attachments.Attached[] Attached = new Attachments.Attached[Attachments.Colors + 1];
        public readonly uint[] Renderbuffer = new uint[Attachments.Colors + 1];
        public bool Fresh, DrawDefault, ReadDefault;
        public int Renderbuffers;
    }

    // What a program was linked from, if every attached stage was recorded; Other: a stage beside vertex and fragment
    public sealed record Linked(string Vertex, string Fragment, int Other, IReadOnlyDictionary<string, int> Attributes);

    private sealed class ProgramRecord
    {
        public readonly List<uint> Shaders = [];
        public readonly Dictionary<string, int> AttributeBinds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Locations = new(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Blocks = new(StringComparer.Ordinal);
        public readonly Dictionary<uint, int> Points = [];
        public Linked? Stages;
    }

    private sealed class ShaderRecord(uint type)
    {
        public uint Type { get; } = type;
        public string Source { get; set; } = "";
    }

    private static readonly Dictionary<uint, TextureRecord> TextureRecords = [];
    private static readonly Dictionary<uint, FramebufferRecord> FramebufferRecords = [];
    private static readonly Dictionary<uint, ProgramRecord> ProgramRecords = [];
    private static readonly Dictionary<uint, ShaderRecord> ShaderRecords = [];
    private static readonly Dictionary<uint, (int Min, int Mag, int Wrap, int Compare)> SamplerRecords = [];
    private static readonly Dictionary<uint, long> BufferSizes = [];
    // What the driver answers for each format left to it
    private static readonly Dictionary<(int Format, uint Type), int> SizedFormats = [];

    // OpenGL was asked for an object the record did not know (made before it); for the report
    public static long RecordMisses { get; private set; }

    public static (int Textures, int Framebuffers, int Programs, int Linked, int Buffers, long Misses) Records =>
        (TextureRecords.Count, FramebufferRecords.Count, ProgramRecords.Count,
            ProgramRecords.Values.Count(p => p.Stages is not null), BufferSizes.Count, RecordMisses);

    private static void Unrecord()
    {
        TextureRecords.Clear();
        FramebufferRecords.Clear();
        ProgramRecords.Clear();
        ShaderRecords.Clear();
        SamplerRecords.Clear();
        BufferSizes.Clear();
        SizedFormats.Clear();
        Unrenderbuffered();
        RecordMisses = 0;
    }

    private static bool Unsized(int format) =>
        format is 0x1902 or 0x84F9 or 0x1903 or 0x8227 or 0x1907 or 0x1908 or 0x1909 or 0x190A or 0x1906 or (>= 1 and <= 4);

    private static TextureRecord? Texture(uint texture)
    {
        if (texture == 0 || !Tapped) return null;
        if (texture >= RenderbufferBase) return TextureRecords.GetValueOrDefault(texture); // OpenGL has no such texture to ask
        if (TextureRecords.TryGetValue(texture, out var known) && !known.Untracked && known.Levels != 0) return known;
        var t = SeededTexture((int)texture);
        t.Untracked = (known?.Untracked ?? false) || (t.Target != 0 && !Tracked(t.Target));
        if (known is not null || TextureRecords.Count < MaxRecords) TextureRecords[texture] = t; // full: asked each time
        RecordMisses++;
        return t;
    }

    private static TextureRecord SeededTexture(int texture)
    {
        int Get(int name)
        {
            GL.GetTextureParameter(texture, (GetTextureParameter)name, out int value);
            return value;
        }

        int Level(int level, int name)
        {
            GL.GetTextureLevelParameter(texture, level, (GetTextureParameter)name, out int value);
            return value;
        }

        var t = new TextureRecord
        {
            Target = (uint)Get(GlTextureTarget), Width = Level(0, GlTextureWidth), Height = Level(0, GlTextureHeight),
            Depth = Level(0, GlTextureDepth), Format = Level(0, GlInternalFormat), Min = Get(GlMinFilter),
            Mag = Get(GlMagFilter), WrapS = Get(GlWrapS), WrapT = Get(GlWrapT), Compare = Get(GlCompareMode),
            CompareFunc = Get(GlCompareFunc), MaxLevel = Get(GlMaxLevel), BaseLevel = Get(GlBaseLevel)
        };
        var border = new float[4];
        GL.GetTextureParameter(texture, (GetTextureParameter)GlBorderColor, border);
        t.Border = (border[0], border[1], border[2], border[3]);
        for (var level = 0; level < MaxLevels && Level(level, GlTextureWidth) > 0; level++) t.Levels |= 1 << level;
        return t;
    }

    // Made under the record: GL defaults, target unknown until bound (glGenTextures) or given (glCreateTextures)
    private static void TexturesMade(uint target, int count, uint* names)
    {
        if (names == null || count > MaxNames) return;
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0 && (TextureRecords.Count < MaxRecords || TextureRecords.ContainsKey(names[i])))
                TextureRecords[names[i]] = new TextureRecord { Target = target }; // past MaxRecords asked as made before
    }

    // The first bind gives a texture made by glGenTextures its target
    private static void TextureBound(uint texture, uint target)
    {
        if (!TextureRecords.TryGetValue(texture, out var t) || t.Target != 0) return;
        (t.Target, t.Untracked) = (target, !Tracked(target));
    }

    // The targets whose bound texture the units follow (BoundName)
    private static bool Tracked(uint target) =>
        target is Texture2D or Texture2DArray or TextureCube or (>= CubeFirst and <= CubeLast);

    // The targets a glTexImage* or glTexStorage* names a texture by that the units follow (no proxies)
    private static bool Specifiable(uint target) => Assert(Tapped) && Tracked(target);

    private static uint Plain(uint target) => target is >= CubeFirst and <= CubeLast ? TextureCube : target;

    // A level specified anew (glTexImage*, glTexStorage* for levels at once); width 0 takes the level away
    private static void Specified(uint texture, uint target, (int Level, int Count) levels,
        (int Format, int Width, int Height, int Depth) spec, uint type)
    {
        if (!TextureRecords.TryGetValue(texture, out var t)) return;
        if (!Assert(levels.Count is >= 1 and <= 64) || !Index(levels.Level, MaxLevels)) return;
        if (t.Target == 0 && target != 0) t.Target = Plain(target);
        var bits = (int)(((1L << Math.Min(levels.Count, MaxLevels)) - 1) << levels.Level) & 0xFFFF;
        t.Levels = spec.Width > 0 ? t.Levels | bits : t.Levels & ~bits;
        if (levels.Level != 0) return;
        var format = Unsized(spec.Format) && SizedFormats.TryGetValue((spec.Format, type), out var sized)
            ? sized
            : spec.Format;
        (t.Width, t.Height, t.Depth) = (spec.Width, spec.Height, Math.Max(spec.Depth, 1));
        (t.Format, t.Type) = (format, type);
    }

    // glGenerateMipmap: every level from the base on, up to the smallest or GL_TEXTURE_MAX_LEVEL
    private static void MipmapsMade(uint texture)
    {
        if (!TextureRecords.TryGetValue(texture, out var t) || t.Width <= 0) return;
        var side = Math.Max(Math.Max(t.Width, t.Height), t.Target == 0x806F ? t.Depth : 1); // a 3D one in depth too
        var log = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)side);
        var top = Math.Min(Math.Min(log, t.MaxLevel), MaxLevels - 1);
        t.Levels |= (int)((2L << Math.Max(top, 0)) - 1);
    }

    private static void TextureTuned(uint texture, uint name, float value)
    {
        if (!TextureRecords.TryGetValue(texture, out var t) || !Finite(value)) return;
        var whole = (int)value;
        switch (name)
        {
            case GlMinFilter: t.Min = whole; break;
            case GlMagFilter: t.Mag = whole; break;
            case GlWrapS: t.WrapS = whole; break;
            case GlWrapT: t.WrapT = whole; break;
            case GlCompareMode: t.Compare = whole; break;
            case GlCompareFunc: t.CompareFunc = whole; break;
            case GlMaxLevel: t.MaxLevel = whole; break;
            case GlBaseLevel: t.BaseLevel = whole; break;
        }
    }

    // What OpenGL would answer for the texture's target, level 0 and format; null while untapped
    public static GlTexture.Description? Described(int texture)
    {
        if (texture <= 0 || Texture((uint)texture) is not { } t) return null;
        if (!Assert(t.Width >= 0)) return null;
        if (Unsized(t.Format) && t.Width > 0)
        {
            GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)GlInternalFormat, out int asked);
            if (SizedFormats.Count < MaxNamed) SizedFormats[(t.Format, t.Type)] = asked;
            (t.Format, RecordMisses) = (asked, RecordMisses + 1);
        }

        return new GlTexture.Description((int)t.Target, t.Width, t.Height, t.Depth, t.Format);
    }

    // The levels that exist from level 0 on, up to GL_TEXTURE_MAX_LEVEL (at least one, as OpenGL's answer counts)
    public static int? LevelsOf(int texture)
    {
        if (texture <= 0 || Texture((uint)texture) is not { } t || !Assert(t.MaxLevel >= 0)) return null;
        var levels = 1;
        // GL_TEXTURE_MAX_LEVEL may be int.MaxValue: + 1 only below MaxLevels
        for (var level = 1; level < Math.Min(MaxLevels, Math.Min(t.MaxLevel, MaxLevels - 1) + 1); level++)
        {
            if ((t.Levels & (1 << level)) == 0) break;
            levels++;
        }

        return levels;
    }

    public static Samplers.State? SamplingOf(int texture) =>
        texture > 0 && Texture((uint)texture) is { } t && Assert(t.Min >= 0) && Finite(t.Border.R)
            ? Samplers.FromGl(t.Min, t.Mag, t.WrapS, t.Compare != 0, t.WrapT) with { WhiteBorder = t.Border.R > 0.5f }
            : null;

    // One of the parameters the record follows (filters, wraps, comparison, GL_TEXTURE_MAX_LEVEL), else null
    public static int? ParameterOf(int texture, int name)
    {
        if (texture <= 0 || !Assert(name > 0) || Texture((uint)texture) is not { } t) return null;
        return name switch
        {
            GlMinFilter => t.Min, GlMagFilter => t.Mag, GlWrapS => t.WrapS, GlWrapT => t.WrapT,
            GlCompareMode => t.Compare, GlCompareFunc => t.CompareFunc, GlMaxLevel => t.MaxLevel, GlBaseLevel => t.BaseLevel,
            _ => null
        };
    }

    // glTex(ture)Parameter*v's GL_TEXTURE_BORDER_COLOR, integers normalized
    private static void Bordered(uint texture, float* values, bool integer)
    {
        if (values == null || !TextureRecords.TryGetValue(texture, out var t)) return;
        var scale = integer ? 1f / int.MaxValue : 1f;
        t.Border = (values[0] * scale, values[1] * scale, values[2] * scale, values[3] * scale);
    }

    // Whether the name is a texture: known to the record, or else asked; null while untapped
    public static bool? TextureExists(int texture)
    {
        if (!Tapped || texture <= 0) return null;
        return TextureRecords.ContainsKey((uint)texture) || GL.IsTexture(texture);
    }

    private static uint FramebufferAt(uint target) => target == GlReadFramebuffer ? _readFramebuffer : _framebuffer;

    // glFramebufferTexture: an array, cube map or 3D texture is attached with every layer
    private static Attachments.Attached WholeLayers(uint texture, int level)
    {
        var target = texture == 0 ? 0 : Texture(texture)?.Target ?? 0;
        var layered = target is Texture2DArray or TextureCube or 0x806F or 0x9009 or 0x8C18 or 0x9102;
        return new Attachments.Attached((int)texture, level, 0, layered);
    }

    private static int SlotOf(uint attachment) => attachment switch
    {
        >= GlColor0 and < GlColor0 + Attachments.Colors => (int)(attachment - GlColor0),
        GlDepthAttachment or GlDepthStencilAttachment => Attachments.Depth,
        _ => -1
    };

    private static void Attach(uint framebuffer, uint attachment, Attachments.Attached attached,
        uint renderbuffer = 0)
    {
        if (framebuffer == 0 || SlotOf(attachment) is not (>= 0 and var slot)) return;
        if (Framebuffer(framebuffer) is not { } f) return;
        if (renderbuffer is > 0 and < RenderbufferBase) // as the texture it stands as (one level, one layer)
            attached = new Attachments.Attached((int)RenderbufferName(renderbuffer), 0, 0, false);
        f.Attached[slot] = attached;
        f.Renderbuffers = renderbuffer != 0 ? f.Renderbuffers | (1 << slot) : f.Renderbuffers & ~(1 << slot);
        f.Renderbuffer[slot] = renderbuffer;
        Reattached(framebuffer);
    }

    // glDeleteRenderbuffers detaches the renderbuffer from the bound framebuffers only (others keep its orphaned image)
    private static void RenderbufferDetached(uint framebuffer, uint renderbuffer)
    {
        if (!FramebufferRecords.TryGetValue(framebuffer, out var f)) return;
        var detached = false;
        for (var slot = 0; slot <= Attachments.Colors; slot++)
        {
            if ((f.Renderbuffers & (1 << slot)) == 0 || f.Renderbuffer[slot] != renderbuffer) continue;
            f.Attached[slot] = default;
            (f.Renderbuffer[slot], f.Renderbuffers, detached) = (0, f.Renderbuffers & ~(1 << slot), true);
        }

        if (detached) Reattached(framebuffer);
    }

    // Complete while every attachment is a texture level 0 the record knows, of a format its attachment point renders
    private static uint? Status(uint target)
    {
        var framebuffer = FramebufferAt(target);
        if (framebuffer == 0 || framebuffer == Window ||
            !FramebufferRecords.TryGetValue(framebuffer, out var f) || !f.Fresh || !Assert(target > 0)) return null;
        var any = false;
        for (var i = 0; i <= Attachments.Colors; i++)
        {
            var a = f.Attached[i];
            if ((f.Renderbuffers & (1 << i)) != 0)
            {
                if (!RenderbufferFits(f.Renderbuffer[i], i == Attachments.Depth)) return null;
                any = true;
                continue;
            }

            if (a.Texture == 0) continue;
            if (a.Level != 0 || !TextureRecords.TryGetValue((uint)a.Texture, out var t) || t.Width <= 0 ||
                (t.Levels & 1) == 0 || Unsized(t.Format) || DepthFormat(t.Format) != (i == Attachments.Depth))
                return null;
            any = true;
        }

        return any ? GlComplete : null;
    }

    private static bool DepthFormat(int format) => format is 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC or 0x88F0 or 0x8CAD;

    private static FramebufferRecord? Framebuffer(uint framebuffer)
    {
        if (framebuffer == 0) return null;
        if (FramebufferRecords.TryGetValue(framebuffer, out var known)) return known;
        if (FramebufferRecords.Count >= MaxRecords) FramebufferRecords.Clear();
        var made = new FramebufferRecord();
        Attachments.Asked((int)framebuffer).CopyTo(made.Attached, 0);
        RecordMisses++;
        return FramebufferRecords[framebuffer] = made;
    }

    private static void FramebuffersMade(int count, uint* names)
    {
        if (names == null || count > MaxNames) return;
        if (FramebufferRecords.Count + count > MaxRecords) FramebufferRecords.Clear();
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0)
                FramebufferRecords[names[i]] = new FramebufferRecord { Fresh = true, DrawDefault = true, ReadDefault = true };
    }

    // The framebuffer's attachments as the record knows them (a copy), asked from OpenGL once for one made before it
    public static Attachments.Attached[]? AttachmentsOf(int framebuffer) =>
        framebuffer > 0 && Tapped && Framebuffer((uint)framebuffer) is { } f ? [.. f.Attached] : null;

    // Seen made, with its draw (or read) buffers never set: OpenGL's defaults
    private static bool Defaulted(uint framebuffer, bool read) =>
        FramebufferRecords.TryGetValue(framebuffer, out var f) && (read ? f.ReadDefault : f.DrawDefault);

    private static void SamplersMade(int count, uint* names)
    {
        if (names == null || count > MaxNames) return;
        if (SamplerRecords.Count + count > MaxNamed) SamplerRecords.Clear();
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0)
                SamplerRecords[names[i]] = (0x2702, 0x2601, 0x2901, 0);
    }

    private static void SamplerTuned(uint sampler, uint name, int value)
    {
        if (!SamplerRecords.TryGetValue(sampler, out var s)) return;
        SamplerRecords[sampler] = name switch
        {
            GlMinFilter => s with { Min = value }, GlMagFilter => s with { Mag = value },
            GlWrapS => s with { Wrap = value }, GlCompareMode => s with { Compare = value }, _ => s
        };
    }

    // A sampler object seen made, as its parameters were set since; null for one the record does not know
    public static Samplers.State? SamplerOf(int sampler) =>
        Tapped && sampler > 0 && SamplerRecords.TryGetValue((uint)sampler, out var s) && Assert(s.Min >= 0) &&
        Assert(s.Mag >= 0)
            ? Samplers.FromGl(s.Min, s.Mag, s.Wrap, s.Compare != 0)
            : null;

    private static void ShaderMade(uint shader, uint type)
    {
        if (ShaderRecords.Count >= MaxProgramRecords) ShaderRecords.Clear();
        ShaderRecords[shader] = new ShaderRecord(type);
    }

    private static void SourceSet(uint shader, int count, byte** strings, int* lengths)
    {
        if (!ShaderRecords.TryGetValue(shader, out var s) || !Assert(count is > 0 and <= 64)) return;
        var source = new StringBuilder();
        for (var i = 0; i < Math.Min(count, 64); i++)
        {
            var at = (nint)strings[i];
            if (at == 0) continue;
            _ = source.Append(lengths == null || lengths[i] < 0
                ? Marshal.PtrToStringUTF8(at)
                : Marshal.PtrToStringUTF8(at, lengths[i]));
        }

        s.Source = source.ToString();
    }

    private static void ProgramMade(uint program)
    {
        if (ProgramRecords.Count >= MaxProgramRecords) ProgramRecords.Clear();
        ProgramRecords[program] = new ProgramRecord();
    }

    private static void ShaderAttached(uint program, uint shader, bool on)
    {
        if (!ProgramRecords.TryGetValue(program, out var p) || !Assert(shader > 0)) return;
        if (on && !p.Shaders.Contains(shader) && p.Shaders.Count < MaxShaders) p.Shaders.Add(shader);
        else if (!on) _ = p.Shaders.Remove(shader);
    }

    private static void AttributeBound(uint program, uint index, byte* name)
    {
        if (!ProgramRecords.TryGetValue(program, out var p) || !Assert(index < 4096)) return;
        if (Named(name) is { } attribute && p.AttributeBinds.Count < MaxNamed) p.AttributeBinds[attribute] = (int)index;
    }

    // Linking takes the stages attached and the attribute locations bound; locations and block bindings start anew
    private static void ProgramLinked(uint program)
    {
        if (!ProgramRecords.TryGetValue(program, out var p)) return;
        (p.Stages, var vertex, var fragment, var other) = (null, "", "", 0);
        p.Locations.Clear();
        p.Blocks.Clear();
        p.Points.Clear();
        foreach (var shader in p.Shaders.Bounded(MaxShaders))
        {
            if (!ShaderRecords.TryGetValue(shader, out var s)) return;
            if (s.Type == VertexStage) vertex = s.Source;
            else if (s.Type == FragmentStage) fragment = s.Source;
            else other = (int)s.Type;
        }

        var bound = new Dictionary<string, int>(p.AttributeBinds, StringComparer.Ordinal);
        p.Stages = new Linked(vertex, fragment, other, bound);
        if (Linking is { } linking && other == 0 && vertex.Length > 0 && fragment.Length > 0) linking(program);
    }

    // The engine's (and the mods') own location questions, kept without a string for one already known
    private static void LocationAsked(uint program, byte* name, int location)
    {
        if (!ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (!Assert(location >= -1)) return;
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        if (bytes.Length >= MaxName || p.Locations.Count >= MaxNamed) return;
        Span<char> chars = stackalloc char[MaxName];
        var named = chars[..Encoding.UTF8.GetChars(bytes, chars)];
        var lookup = p.Locations.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(named, out var known) || known != location) lookup[named] = location;
    }

    private static void BlockAsked(uint program, byte* name, uint index)
    {
        if (!ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (Named(name) is { } block && p.Blocks.Count < MaxNamed) p.Blocks[block] = index;
    }

    private static void BlockBound(uint program, uint index, uint binding)
    {
        if (!ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (index == InvalidIndex) return;
        if (Assert(binding < 1024) && p.Points.Count < MaxNamed) p.Points[index] = (int)binding;
    }

    private static string? Named(byte* name)
    {
        if (name == null) return null;
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        return bytes.Length is > 0 and < MaxName ? Encoding.UTF8.GetString(bytes) : null;
    }

    // What the program was linked from, when it was linked under the record from stages the record heard
    public static Linked? LinkedOf(int program) =>
        Tapped && program > 0 && ProgramRecords.TryGetValue((uint)program, out var p) ? p.Stages : null;

    // A uniform's location as the engine was told it since the last link; null when nobody asked
    public static int? LocationOf(int program, string name) =>
        LinkedOf(program) is not null && NotNull(name) && Assert(name.Length > 0) &&
        ProgramRecords[(uint)program].Locations.TryGetValue(name, out var location)
            ? location
            : null;

    // Block binding point (declared: layout binding or 0 after link), -1 if unused; null if index not asked since link
    public static int? BlockPointOf(int program, string block, int declared)
    {
        if (LinkedOf(program) is null || !NotNull(block)) return null;
        if (!ProgramRecords[(uint)program].Blocks.TryGetValue(block, out var index)) return null;
        if (index == InvalidIndex) return -1;
        return ProgramRecords[(uint)program].Points.TryGetValue(index, out var point) ? point : declared;
    }

    // Size as glBufferData/glBufferStorage last gave it (record), else GL's answer (waits for glthread, not the GPU)
    public static long BufferSize(uint buffer)
    {
        if (!Assert(buffer is > 0 and < int.MaxValue)) return 0;
        if (BufferSizeOf(buffer) is { } recorded) return recorded;
        GL.GetNamedBufferParameter((int)buffer, BufferParameterName.BufferSize, out int asked);
        return Assert(asked >= 0) ? asked : 0;
    }

    // A lean buffer's size comes from the lean record: OpenGL holds only a stand-in, so asking it would be wrong
    public static long? BufferSizeOf(uint buffer)
    {
        if (!Tapped || buffer == 0) return null;
        if (LeanBuffers.TryGetValue(buffer, out var lean)) return lean.Size;
        return BufferSizes.TryGetValue(buffer, out var size) ? size : null;
    }

    // A full record starts over: what it forgets OpenGL still answers (BufferSize), lean buffers excepted, whose
    // sizes LeanBuffers keeps. Refusing new entries instead left new pools sized by their lean stand-ins.
    private static void Sized(uint buffer, long size)
    {
        if (buffer == 0 || !Assert(size >= 0)) return;
        if (BufferSizes.Count >= MaxRecords && !BufferSizes.ContainsKey(buffer)) BufferSizes.Clear();
        BufferSizes[buffer] = size;
    }
}

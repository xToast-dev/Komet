using System.Runtime.InteropServices;
using unsafe CreateShaderFn = delegate* unmanaged<uint, uint>;
using unsafe ShaderSourceFn = delegate* unmanaged<uint, int, byte**, int*, void>;
using unsafe AttachShaderFn = delegate* unmanaged<uint, uint, void>;
using unsafe BindAttribFn = delegate* unmanaged<uint, uint, byte*, void>;
using unsafe LinkProgramFn = delegate* unmanaged<uint, void>;
using unsafe UniformLocationFn = delegate* unmanaged<uint, byte*, int>;
using unsafe BlockIndexFn = delegate* unmanaged<uint, byte*, uint>;
using unsafe BlockBindingFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe CreateProgramFn = delegate* unmanaged<uint>;
using unsafe MakeNamesFn = delegate* unmanaged<int, uint*, void>;
using unsafe CreateTexturesFn = delegate* unmanaged<uint, int, uint*, void>;
using unsafe FramebufferTexture2DFn = delegate* unmanaged<uint, uint, uint, uint, int, void>;
using unsafe FramebufferTextureLayerFn = delegate* unmanaged<uint, uint, uint, int, int, void>;
using unsafe FramebufferTextureFn = delegate* unmanaged<uint, uint, uint, int, void>;
using unsafe FramebufferRenderbufferFn = delegate* unmanaged<uint, uint, uint, uint, void>;
using unsafe CheckStatusFn = delegate* unmanaged<uint, uint>;
using unsafe SamplerParameteriFn = delegate* unmanaged<uint, uint, int, void>;
using unsafe SamplerParameterfFn = delegate* unmanaged<uint, uint, float, void>;
using unsafe TextureStorage2DFn = delegate* unmanaged<uint, int, uint, int, int, void>;
using unsafe TextureStorage3DFn = delegate* unmanaged<uint, int, uint, int, int, int, void>;
using unsafe TextureParameterfFn = delegate* unmanaged<uint, uint, float, void>;
using unsafe TextureParameterivFn = delegate* unmanaged<uint, uint, int*, void>;
using Attachments = Komet.Vulkan.GlFramebuffer;

namespace Komet.Vulkan;

// The record's own entries: program, framebuffer, sampler and texture creation, heard after the
// driver's call (unknown objects are asked from GL then). Entries OpenTK's table lacks are skipped, not a failed tap.
// glCheckFramebufferStatus is answered from the record for framebuffers seen made.
internal static unsafe partial class GlTap
{
    private const int RecordsAt = ObjectsAt + ObjectCount, RecordCount = 53, MaxShaders = 8, MaxName = 256;
    private const uint GlComplete = 0x8CD5, GlDepthAttachment = 0x8D00, GlDepthStencilAttachment = 0x821A;
    private const uint VertexStage = 0x8B31, FragmentStage = 0x8B30;

    private static Entry[] RecordEntries() =>
    [
        new("glCreateShader", (nint)(CreateShaderFn)(&CreateShader), Optional: true),
        new("glShaderSource", (nint)(ShaderSourceFn)(&ShaderSource), Optional: true),
        new("glAttachShader", (nint)(AttachShaderFn)(&AttachShader), Optional: true),
        new("glDetachShader", (nint)(AttachShaderFn)(&DetachShader), Optional: true),
        new("glBindAttribLocation", (nint)(BindAttribFn)(&BindAttribLocation), Optional: true),
        new("glLinkProgram", (nint)(LinkProgramFn)(&LinkProgram), Optional: true),
        new("glGetUniformLocation", (nint)(UniformLocationFn)(&GetUniformLocation), Optional: true),
        new("glGetUniformBlockIndex", (nint)(BlockIndexFn)(&GetUniformBlockIndex), Optional: true),
        new("glUniformBlockBinding", (nint)(BlockBindingFn)(&UniformBlockBinding), Optional: true),
        new("glCreateProgram", (nint)(CreateProgramFn)(&CreateProgram), Optional: true),
        new("glGenFramebuffers", (nint)(MakeNamesFn)(&GenFramebuffers), Optional: true),
        new("glCreateFramebuffers", (nint)(MakeNamesFn)(&CreateFramebuffers), Optional: true),
        new("glFramebufferTexture2D", (nint)(FramebufferTexture2DFn)(&FramebufferTexture2D), Optional: true),
        new("glFramebufferTextureLayer", (nint)(FramebufferTextureLayerFn)(&FramebufferTextureLayer), Optional: true),
        new("glFramebufferTexture", (nint)(FramebufferTextureFn)(&FramebufferTexture), Optional: true),
        new("glNamedFramebufferTexture", (nint)(FramebufferTextureFn)(&NamedFramebufferTexture), Optional: true),
        new("glNamedFramebufferTextureLayer", (nint)(FramebufferTextureLayerFn)(&NamedFramebufferTextureLayer), Optional: true),
        new("glFramebufferRenderbuffer", (nint)(FramebufferRenderbufferFn)(&FramebufferRenderbuffer), Optional: true),
        new("glNamedFramebufferRenderbuffer", (nint)(FramebufferRenderbufferFn)(&NamedFramebufferRenderbuffer), Optional: true),
        new("glCheckFramebufferStatus", (nint)(CheckStatusFn)(&CheckFramebufferStatus), Optional: true),
        new("glCheckFramebufferStatusEXT", (nint)(CheckStatusFn)(&CheckFramebufferStatusExt), Optional: true),
        new("glGenSamplers", (nint)(MakeNamesFn)(&GenSamplers), Optional: true),
        new("glCreateSamplers", (nint)(MakeNamesFn)(&CreateSamplers), Optional: true),
        new("glSamplerParameteri", (nint)(SamplerParameteriFn)(&SamplerParameteri), Optional: true),
        new("glSamplerParameterf", (nint)(SamplerParameterfFn)(&SamplerParameterf), Optional: true),
        new("glGenTextures", (nint)(MakeNamesFn)(&GenTextures), Optional: true),
        new("glCreateTextures", (nint)(CreateTexturesFn)(&CreateTextures), Optional: true),
        new("glTextureStorage2D", (nint)(TextureStorage2DFn)(&TextureStorage2D), Optional: true),
        new("glTextureStorage3D", (nint)(TextureStorage3DFn)(&TextureStorage3D), Optional: true),
        new("glTextureParameterf", (nint)(TextureParameterfFn)(&TextureParameterf), Optional: true),
        new("glTextureParameteriv", (nint)(TextureParameterivFn)(&TextureParameteriv), Optional: true),
        .. SpecEntries(),
        .. StencilEntries()
    ];

    private static bool Kept(int n) => Original[RecordsAt + n] != IntPtr.Zero;

    [UnmanagedCallersOnly]
    private static uint CreateShader(uint type)
    {
        var shader = Kept(0) && Assert(type > 0) ? ((CreateShaderFn)Original[RecordsAt])(type) : 0;
        if (shader != 0) Heard(static s => ShaderMade(s.shader, s.type), (shader, type));
        return shader;
    }

    [UnmanagedCallersOnly]
    private static void ShaderSource(uint shader, int count, byte** strings, int* lengths)
    {
        if (Kept(1) && Assert(count >= 0))
            ((ShaderSourceFn)Original[RecordsAt + 1])(shader, count, strings, lengths);
        if (strings != null && count > 0)
            Heard(static s => SourceSet(s.shader, s.count, (byte**)s.Item3, (int*)s.Item4),
                (shader, count, (nint)strings, (nint)lengths));
    }

    [UnmanagedCallersOnly]
    private static void AttachShader(uint program, uint shader)
    {
        if (Kept(2) && Assert(program > 0))
            ((AttachShaderFn)Original[RecordsAt + 2])(program, shader);
        Heard(static s => ShaderAttached(s.program, s.shader, true), (program, shader));
    }

    [UnmanagedCallersOnly]
    private static void DetachShader(uint program, uint shader)
    {
        if (Kept(3) && Assert(program > 0))
            ((AttachShaderFn)Original[RecordsAt + 3])(program, shader);
        Heard(static s => ShaderAttached(s.program, s.shader, false), (program, shader));
    }

    [UnmanagedCallersOnly]
    private static void BindAttribLocation(uint program, uint index, byte* name)
    {
        if (Kept(4) && Assert(index < 4096))
            ((BindAttribFn)Original[RecordsAt + 4])(program, index, name);
        if (name != null)
            Heard(static s => AttributeBound(s.program, s.index, (byte*)s.Item3),
                (program, index, (nint)name));
    }

    [UnmanagedCallersOnly]
    private static void LinkProgram(uint program)
    {
        if (Kept(5) && Assert(program > 0)) ((LinkProgramFn)Original[RecordsAt + 5])(program);
        Heard(ProgramLinked, program);
    }

    [UnmanagedCallersOnly]
    private static int GetUniformLocation(uint program, byte* name)
    {
        var location = Kept(6) && Assert(program > 0)
            ? ((UniformLocationFn)Original[RecordsAt + 6])(program, name)
            : -1;
        if (name != null)
            Heard(static s => LocationAsked(s.program, (byte*)s.Item2, s.location),
                (program, (nint)name, location));
        return location;
    }

    [UnmanagedCallersOnly]
    private static uint GetUniformBlockIndex(uint program, byte* name)
    {
        var index = Kept(7) && Assert(program > 0)
            ? ((BlockIndexFn)Original[RecordsAt + 7])(program, name)
            : InvalidIndex;
        if (name != null)
            Heard(static s => BlockAsked(s.program, (byte*)s.Item2, s.index),
                (program, (nint)name, index));
        return index;
    }

    [UnmanagedCallersOnly]
    private static void UniformBlockBinding(uint program, uint index, uint binding)
    {
        if (Kept(8) && Assert(program > 0))
            ((BlockBindingFn)Original[RecordsAt + 8])(program, index, binding);
        Heard(static s => BlockBound(s.program, s.index, s.binding), (program, index, binding));
    }

    [UnmanagedCallersOnly]
    private static uint CreateProgram()
    {
        var program = Kept(9) ? ((CreateProgramFn)Original[RecordsAt + 9])() : 0;
        if (program != 0) Heard(ProgramMade, program);
        return program;
    }

    [UnmanagedCallersOnly]
    private static void GenFramebuffers(int count, uint* names)
    {
        if (Kept(10) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 10])(count, names);
        Heard(static s => FramebuffersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateFramebuffers(int count, uint* names)
    {
        if (Kept(11) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 11])(count, names);
        Heard(static s => FramebuffersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level)
    {
        if (Kept(12) && Assert(level >= 0))
            ((FramebufferTexture2DFn)Original[RecordsAt + 12])(target, attachment, textureTarget, texture, level);
        // a cube map's face is its layer
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment,
                new Attachments.Attached((int)s.texture, s.level, Face(s.textureTarget), false)),
            (target, attachment, textureTarget, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer)
    {
        if (Kept(13) && Assert(level >= 0))
            ((FramebufferTextureLayerFn)Original[RecordsAt + 13])(target, attachment, texture, level, layer);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment,
            new Attachments.Attached((int)s.texture, s.level, s.layer, false)),
            (target, attachment, texture, level, layer));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTexture(uint target, uint attachment, uint texture, int level)
    {
        if (Kept(14) && Assert(level >= 0))
            ((FramebufferTextureFn)Original[RecordsAt + 14])(target, attachment, texture, level);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment, WholeLayers(s.texture, s.level)),
            (target, attachment, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferTexture(uint framebuffer, uint attachment, uint texture, int level)
    {
        if (Kept(15) && Assert(level >= 0))
            ((FramebufferTextureFn)Original[RecordsAt + 15])(framebuffer, attachment, texture, level);
        Heard(static s => Attach(s.framebuffer, s.attachment, WholeLayers(s.texture, s.level)),
            (framebuffer, attachment, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferTextureLayer(uint framebuffer, uint attachment, uint texture, int level,
        int layer)
    {
        if (Kept(16) && Assert(level >= 0))
            ((FramebufferTextureLayerFn)Original[RecordsAt + 16])(framebuffer, attachment, texture, level, layer);
        Heard(static s => Attach(s.framebuffer, s.attachment, new Attachments.Attached((int)s.texture, s.level, s.layer,
            false)), (framebuffer, attachment, texture, level, layer));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget,
        uint renderbuffer)
    {
        if (Kept(17) && Assert(attachment > 0))
            ((FramebufferRenderbufferFn)Original[RecordsAt + 17])(target, attachment, renderbufferTarget, renderbuffer);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment, default, s.renderbuffer),
            (target, attachment, renderbuffer));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferRenderbuffer(uint framebuffer, uint attachment, uint renderbufferTarget,
        uint renderbuffer)
    {
        if (Kept(18) && Assert(attachment > 0))
            ((FramebufferRenderbufferFn)Original[RecordsAt + 18])(framebuffer, attachment, renderbufferTarget, renderbuffer);
        Heard(static s => Attach(s.framebuffer, s.attachment, default, s.renderbuffer),
            (framebuffer, attachment, renderbuffer));
    }

    [UnmanagedCallersOnly]
    private static uint CheckFramebufferStatus(uint target)
    {
        if (Asked(static t => Status(t), target, null) is { } status) return status;
        return Kept(19) ? ((CheckStatusFn)Original[RecordsAt + 19])(target) : 0;
    }

    // The engine checks its framebuffers through GL.Ext
    [UnmanagedCallersOnly]
    private static uint CheckFramebufferStatusExt(uint target)
    {
        if (Asked(static t => Status(t), target, null) is { } status) return status;
        return Kept(20) ? ((CheckStatusFn)Original[RecordsAt + 20])(target) : 0;
    }

    [UnmanagedCallersOnly]
    private static void GenSamplers(int count, uint* names)
    {
        if (Kept(21) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 21])(count, names);
        Heard(static s => SamplersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateSamplers(int count, uint* names)
    {
        if (Kept(22) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 22])(count, names);
        Heard(static s => SamplersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void SamplerParameteri(uint sampler, uint name, int value)
    {
        if (Kept(23) && Assert(name > 0))
            ((SamplerParameteriFn)Original[RecordsAt + 23])(sampler, name, value);
        Heard(static s => SamplerTuned(s.sampler, s.name, s.value), (sampler, name, value));
    }

    [UnmanagedCallersOnly]
    private static void SamplerParameterf(uint sampler, uint name, float value)
    {
        if (Kept(24) && Assert(name > 0))
            ((SamplerParameterfFn)Original[RecordsAt + 24])(sampler, name, value);
        if (Finite(value)) Heard(static s => SamplerTuned(s.sampler, s.name, (int)s.value), (sampler, name, value));
    }

    [UnmanagedCallersOnly]
    private static void GenTextures(int count, uint* names)
    {
        if (Kept(25) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 25])(count, names);
        Heard(static s => TexturesMade(0, s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateTextures(uint target, int count, uint* names)
    {
        if (Kept(26) && Assert(count >= 0))
            ((CreateTexturesFn)Original[RecordsAt + 26])(target, count, names);
        Heard(static s => TexturesMade(s.target, s.count, (uint*)s.Item3), (target, count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void TextureStorage2D(uint texture, int levels, uint format, int width, int height)
    {
        LeanGone(texture, true); // immutable storage is never lean: OpenGL could not be given its size back
        if (Kept(27) && Assert(levels >= 0))
            ((TextureStorage2DFn)Original[RecordsAt + 27])(texture, levels, format, width, height);
        Heard(static s => StorageMade(s.texture, (s.levels, ((int)s.format, s.width, s.height, 1))),
            (texture, levels, format, width, height));
    }

    [UnmanagedCallersOnly]
    private static void TextureStorage3D(uint texture, int levels, uint format, int width, int height, int depth)
    {
        LeanGone(texture, true);
        if (Kept(28) && Assert(levels >= 0))
            ((TextureStorage3DFn)Original[RecordsAt + 28])(texture, levels, format, width, height, depth);
        Heard(static s => StorageMade(s.texture, (s.levels, ((int)s.format, s.width, s.height, s.depth))),
            (texture, levels, format, width, height, depth));
    }

    // glTextureStorage*: the record's, then Writes' (Vulkan-only) for a texture of a target the units follow
    private static void StorageMade(uint texture, (int Levels, (int Format, int Width, int Height, int Depth) Spec) made)
    {
        if (!Assert(made.Levels >= 0)) return;
        Specified(texture, 0, (0, made.Levels), made.Spec, 0);
        var target = TextureRecords.TryGetValue(texture, out var t) ? t.Target : 0;
        if (target != 0 && Tracked(target)) Imaged(texture, target, (0, Math.Max(made.Levels, 1)), made.Spec, default, 0);
        else UntrackedCalls++;
    }

    [UnmanagedCallersOnly]
    private static void TextureParameterf(uint texture, uint name, float value)
    {
        Version++;
        Tuned(texture, name, value);
        if (Kept(29) && Assert(Tapped) && Assert(name > 0))
            ((TextureParameterfFn)Original[RecordsAt + 29])(texture, name, value);
    }

    [UnmanagedCallersOnly]
    private static void TextureParameteriv(uint texture, uint name, int* values)
    {
        Version++;
        if (values != null) Tuned(texture, name, name == GlBorderColor ? values[0] / (float)int.MaxValue : values[0]);
        if (values != null) BorderInts(texture, name, values);
        if (Kept(30) && Assert(Tapped) && Assert(name > 0))
            ((TextureParameterivFn)Original[RecordsAt + 30])(texture, name, values);
    }

    private static void BorderFloats(uint texture, uint name, float* values)
    {
        if (name == GlBorderColor && values != null)
            Heard(static s => Bordered(s.texture, (float*)s.Item2, false), (texture, (nint)values));
    }

    private static void BorderInts(uint texture, uint name, int* values)
    {
        if (name != GlBorderColor || values == null) return;
        var floats = stackalloc float[4];
        for (var i = 0; i < 4; i++) floats[i] = values[i];
        Heard(static s => Bordered(s.texture, (float*)s.Item2, true), (texture, (nint)floats));
    }
}

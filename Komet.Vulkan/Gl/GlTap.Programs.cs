using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Watched programs' uniforms reach their mirror as the engine sets them; texture units and sampler objects are followed
// per unit, so a draw knows when what its samplers read may have changed
internal static unsafe partial class GlTap
{
    private const int MaxUnits = 96, MaxPrograms = 512, MaxArray = 64, MaxMembers = 1024, NearLocations = 1024;
    private const uint Texture0 = 0x84C0, Texture2D = 0x0DE1;
    private const int UseAt = 0, U1FAt = 1, U2FAt = 2, U3FAt = 3, U4FAt = 4, U1IAt = 5, U2IAt = 6, U3IAt = 7, U4IAt = 8;
    private const int U1FvAt = 9, U2FvAt = 10, U3FvAt = 11, U4FvAt = 12, U1IvAt = 13, U2IvAt = 14, U3IvAt = 15;
    private const int U4IvAt = 16;
    private const int M4FvAt = 17, ActiveAt = 18, BindAt = 19, BindUnitAt = 20, SamplerAt = 21;

    private static readonly Dictionary<uint, Watched> Programs = [];
    private static readonly uint[] UnitTextures = new uint[MaxUnits], UnitSamplers = new uint[MaxUnits];
    private static uint _unit, _current;
    private static int _unsent; // programs marked Unsent
    private static Watched? _using;

    // A program's uniforms by location: uniform, array element, words per element. Drivers hand out locations densely
    // from 0, so those index an array (Entry null if unwatched); rare ones past NearLocations use the dictionary.
    private sealed class Watched(UniformMirror mirror, Slot[] near, Dictionary<int, Slot> far)
    {
        public UniformMirror Mirror { get; } = mirror;
        public Slot[] Near { get; } = near;
        public Dictionary<int, Slot> Far { get; } = far;
        public bool Unsent { get; set; } // values the mirror holds that resting GL was not sent (Withheld)
    }

    private readonly record struct Slot(UniformMirror.Entry Entry, int Element, int Words, char Kind = 'i');

    public static int Program => (int)_current;

    // Bumps when a unit's content may change (texture or sampler object bound, texture deleted); a watched program's
    // sampler uniforms are counted by its mirror (UniformMirror.SamplerWrites)
    public static long TextureVersion { get; private set; }

    // Bumps whenever textures are deleted: a name bound now may be another texture than before
    public static long DeleteVersion { get; private set; }

    private static Entry[] ProgramEntries() =>
    [
        new("glUseProgram", (IntPtr)(delegate* unmanaged<uint, void>)&UseProgram),
        new("glUniform1f", (IntPtr)(delegate* unmanaged<int, float, void>)&U1F),
        new("glUniform2f", (IntPtr)(delegate* unmanaged<int, float, float, void>)&U2F),
        new("glUniform3f", (IntPtr)(delegate* unmanaged<int, float, float, float, void>)&U3F),
        new("glUniform4f", (IntPtr)(delegate* unmanaged<int, float, float, float, float, void>)&U4F),
        new("glUniform1i", (IntPtr)(delegate* unmanaged<int, int, void>)&U1I),
        new("glUniform2i", (IntPtr)(delegate* unmanaged<int, int, int, void>)&U2I),
        new("glUniform3i", (IntPtr)(delegate* unmanaged<int, int, int, int, void>)&U3I),
        new("glUniform4i", (IntPtr)(delegate* unmanaged<int, int, int, int, int, void>)&U4I),
        new("glUniform1fv", (IntPtr)(delegate* unmanaged<int, int, float*, void>)&U1Fv),
        new("glUniform2fv", (IntPtr)(delegate* unmanaged<int, int, float*, void>)&U2Fv),
        new("glUniform3fv", (IntPtr)(delegate* unmanaged<int, int, float*, void>)&U3Fv),
        new("glUniform4fv", (IntPtr)(delegate* unmanaged<int, int, float*, void>)&U4Fv),
        new("glUniform1iv", (IntPtr)(delegate* unmanaged<int, int, int*, void>)&U1Iv),
        new("glUniform2iv", (IntPtr)(delegate* unmanaged<int, int, int*, void>)&U2Iv),
        new("glUniform3iv", (IntPtr)(delegate* unmanaged<int, int, int*, void>)&U3Iv),
        new("glUniform4iv", (IntPtr)(delegate* unmanaged<int, int, int*, void>)&U4Iv),
        new("glUniformMatrix4fv", (IntPtr)(delegate* unmanaged<int, int, byte, float*, void>)&M4Fv),
        new("glActiveTexture", (IntPtr)(delegate* unmanaged<uint, void>)&ActiveTexture),
        new("glBindTexture", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindTexture),
        new("glBindTextureUnit", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindTextureUnit),
        new("glBindSampler", (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindSampler)
    ];

    // Watches a program: uniforms (from the port) by location into the mirror, seeded with the program's current values
    public static void Watch(int program, GlslPort.Ported ported, UniformMirror mirror)
    {
        if (!NotNull(ported) || !NotNull(mirror) || program <= 0) return;
        if (Programs.GetValueOrDefault((uint)program) is { Unsent: true } before) Send((uint)program, before); // read below
        var at = new Dictionary<int, Slot>();
        GlslPort.Uniform[] members =
            [.. ported.VertexUniforms.Members, .. ported.FragmentUniforms.Members, .. ported.HotUniforms.Members];
        foreach (var u in members.Bounded(MaxMembers))
        {
            var (columns, rows) = GlslPort.Shape(u.Type);
            if (mirror.EntryOf(u.Name) is not { } entry) continue;
            for (var e = 0; e < Math.Min(u.Count, MaxArray); e++)
            {
                var location = UniformLocation(program, u.Count > 1 ? $"{u.Name}[{e}]" : u.Name);
                if (location >= 0) at[location] = new Slot(entry, e, columns * rows, KindOf(u.Type, columns));
            }
        }

        foreach (var sampler in ported.Samplers.Bounded(32))
            if (UniformLocation(program, sampler.Name) is var location and >= 0 &&
                mirror.EntryOf(sampler.Name) is { } entry)
                at[location] = new Slot(entry, 0, 1);
        var (floats, ints) = (new float[16], new int[16]);
        var slots = at.ToArray();
        foreach (var (location, slot) in slots.Bounded(MaxMembers * MaxArray))
        {
            if (!Assert(slot.Words is > 0 and <= 16)) continue;
            var whole = slot.Kind is 'i' or 'u'; // asked as integers, kept as their bits
            if (whole) GL.GetUniform(program, location, ints);
            else GL.GetUniform(program, location, floats);
            mirror.SetAt(slot.Entry, slot.Element * slot.Words, whole
                ? MemoryMarshal.Cast<int, uint>(ints.AsSpan(0, slot.Words))
                : MemoryMarshal.Cast<float, uint>(floats.AsSpan(0, slot.Words)));
        }

        if (Programs.Count >= MaxPrograms)
        {
            SendUnsent(); // the values OpenGL was not sent go with the programs forgotten
            Programs.Clear();
        }

        Programs[(uint)program] = WatchedOf(mirror, slots);
        _using = Programs.GetValueOrDefault(_current);
    }

    // A uniform's replay call: 'f' floats, 'm' a matrix (columns > 1), 'i' ints, bools and samplers, 'u' unsigned
    private static char KindOf(string type, int columns)
    {
        if (columns > 1) return 'm';
        if (!UniformMirror.Integer(type)) return 'f';
        return type[0] == 'u' ? 'u' : 'i';
    }

    // Resting GL (QuietUniforms) is not sent a uniform the mirror holds: the program is marked, SendUnsent gives
    // the mirror's values to OpenGL before it may draw or be read from again
    private static bool Withheld(bool held)
    {
        if (!held || !MutedUniforms || _using is not { } w) return false;
        if (!w.Unsent) (w.Unsent, _unsent) = (true, _unsent + 1);
        return true;
    }

    // OpenGL draws again (or a program is watched anew): every withheld value is given to it from the mirror
    private static void SendUnsent()
    {
        if (_unsent == 0) return;
        foreach (var (program, w) in Programs.Bounded(MaxPrograms))
            if (w.Unsent)
                Send(program, w);
        _unsent = 0;
    }

    private static void Send(uint program, Watched w)
    {
        if (w.Unsent) (w.Unsent, _unsent) = (false, Math.Max(_unsent - 1, 0));
        for (var location = 0; location < Math.Min(w.Near.Length, NearLocations); location++)
            if (w.Near[location] is { Entry: not null, Element: 0 } slot)
                Sent(program, location, slot);
        foreach (var (location, slot) in w.Far.Bounded(MaxMembers * MaxArray))
            if (slot.Element == 0)
                Sent(program, location, slot);
    }

    // The whole array (or value) from element 0's location; GL ignores elements past the array's end. Only the kinds
    // the taps hear are sent: glUniform*ui and glUniformMatrix2/3fv reach OpenGL directly, so the mirror's seed of
    // those may be older than what OpenGL holds
    private static void Sent(uint program, int location, Slot slot)
    {
        var held = slot.Entry.Held;
        if (location < 0 || !Assert(slot.Words is > 0 and <= 16) || held.Length < slot.Words) return;
        var count = held.Length / slot.Words;
        fixed (uint* words = held)
        {
            var p = (int)program;
            var f = (float*)words;
            var i = (int*)words;
            switch (slot.Kind, slot.Words)
            {
                case ('m', 16): GL.ProgramUniformMatrix4(p, location, count, false, f); break;
                case ('f', 1): GL.ProgramUniform1(p, location, count, f); break;
                case ('f', 2): GL.ProgramUniform2(p, location, count, f); break;
                case ('f', 3): GL.ProgramUniform3(p, location, count, f); break;
                case ('f', 4): GL.ProgramUniform4(p, location, count, f); break;
                case ('i', 1): GL.ProgramUniform1(p, location, count, i); break;
                case ('i', 2): GL.ProgramUniform2(p, location, count, i); break;
                case ('i', 3): GL.ProgramUniform3(p, location, count, i); break;
                case ('i', 4): GL.ProgramUniform4(p, location, count, i); break;
            }
        }
    }

    private static Watched WatchedOf(UniformMirror mirror, KeyValuePair<int, Slot>[] slots)
    {
        var top = -1;
        foreach (var (location, _) in slots.Bounded(MaxMembers * MaxArray))
            if (location < NearLocations) top = Math.Max(top, location);
        var (near, far) = (new Slot[top + 1], new Dictionary<int, Slot>());
        foreach (var (location, slot) in slots.Bounded(MaxMembers * MaxArray))
            if (location < NearLocations) near[location] = slot;
            else far[location] = slot;
        return new Watched(mirror, near, far);
    }

    // The record's answer where the engine asked the same since the link; OpenGL's (heard by the record too) otherwise
    public static int UniformLocation(int program, string name) =>
        Assert(program > 0) && NotNull(name) ? LocationOf(program, name) ?? GL.GetUniformLocation(program, name) : -1;

    public static void Unwatch(int program)
    {
        if (!Assert(program >= 0)) return;
        // values withheld from resting GL go first: forgotten, GL would draw the program with older ones once awake
        // (ProgramGone calls before the driver deletes it, so the program is still there to take them)
        if (Programs.Remove((uint)program, out var gone) && gone.Unsent) Send((uint)program, gone);
        _using = Programs.GetValueOrDefault(_current);
    }

    public static (int Texture, int Sampler) Unit(int unit) =>
        Index(unit, MaxUnits) ? ((int)UnitTextures[unit], (int)UnitSamplers[unit]) : (0, 0);

    // Whether the mirror holds the value now
    private static bool Note(int location, ReadOnlySpan<uint> words)
    {
        if (_using is not { } w || words.IsEmpty) return false;
        var near = w.Near;
        var slot = (uint)location < (uint)near.Length ? near[location] : w.Far.GetValueOrDefault(location);
        if (slot.Entry is not { } entry || !Assert(slot.Words > 0)) return false;
        w.Mirror.SetAt(entry, slot.Element * slot.Words, words);
        return true;
    }

    private static bool Floats(int location, ReadOnlySpan<float> values) =>
        Assert(values.Length <= 4096) && Assert(location >= -1) && Note(location, MemoryMarshal.Cast<float, uint>(values));

    private static bool Ints(int location, ReadOnlySpan<int> values) =>
        Assert(values.Length <= 4096) && Assert(location >= -1) && Note(location, MemoryMarshal.Cast<int, uint>(values));

    // Whether the unit held another texture for the target (the targets not held count as changed)
    private static bool Rebound(uint unit, uint target, uint texture)
    {
        var held = target switch
        {
            Texture2D => UnitTextures, Texture2DArray => UnitArrays, TextureCube => UnitCubes, _ => null
        };
        if (held is null) return true;
        if (held[unit] == texture) return false;
        held[unit] = texture;
        return true;
    }

    [UnmanagedCallersOnly]
    private static void UseProgram(uint program)
    {
        // the same program again: the same state, and _using already follows _current (Watch, Unwatch, Seed)
        if (program != _current) (_current, _using, Version) = (program, Programs.GetValueOrDefault(program), Version + 1);
        if (Assert(Original[UseAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[UseAt])(program);
    }

    [UnmanagedCallersOnly]
    private static void U1F(int l, float x)
    {
        if (Withheld(Floats(l, [x]))) return;
        if (!Assert(Original[U1FAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, float, void>)Original[U1FAt])(l, x);
    }

    [UnmanagedCallersOnly]
    private static void U2F(int l, float x, float y)
    {
        if (Withheld(Floats(l, [x, y]))) return;
        if (!Assert(Original[U2FAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, float, float, void>)Original[U2FAt])(l, x, y);
    }

    [UnmanagedCallersOnly]
    private static void U3F(int l, float x, float y, float z)
    {
        if (Withheld(Floats(l, [x, y, z]))) return;
        if (!Assert(Original[U3FAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, float, float, float, void>)Original[U3FAt])(l, x, y, z);
    }

    [UnmanagedCallersOnly]
    private static void U4F(int l, float x, float y, float z, float w)
    {
        if (Withheld(Floats(l, [x, y, z, w]))) return;
        if (!Assert(Original[U4FAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, float, float, float, float, void>)Original[U4FAt])(l, x, y, z, w);
    }

    [UnmanagedCallersOnly]
    private static void U1I(int l, int x)
    {
        if (Withheld(Ints(l, [x]))) return;
        if (!Assert(Original[U1IAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, int, void>)Original[U1IAt])(l, x);
    }

    [UnmanagedCallersOnly]
    private static void U2I(int l, int x, int y)
    {
        if (Withheld(Ints(l, [x, y]))) return;
        if (!Assert(Original[U2IAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, int, int, void>)Original[U2IAt])(l, x, y);
    }

    [UnmanagedCallersOnly]
    private static void U3I(int l, int x, int y, int z)
    {
        if (Withheld(Ints(l, [x, y, z]))) return;
        if (!Assert(Original[U3IAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, int, int, int, void>)Original[U3IAt])(l, x, y, z);
    }

    [UnmanagedCallersOnly]
    private static void U4I(int l, int x, int y, int z, int w)
    {
        if (Withheld(Ints(l, [x, y, z, w]))) return;
        if (!Assert(Original[U4IAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, int, int, int, int, void>)Original[U4IAt])(l, x, y, z, w);
    }

    private static void Vector(int l, int count, float* v, int size, int entry)
    {
        if (!Assert(Original[entry] != IntPtr.Zero)) return;
        // bound by elements: Floats takes at most 4096 (the mirror's MaxWords)
        if (v != null && count > 0 && count <= 4096 / size && Withheld(Floats(l, new ReadOnlySpan<float>(v, count * size)))) return;
        ((delegate* unmanaged<int, int, float*, void>)Original[entry])(l, count, v);
    }

    private static void Vector(int l, int count, int* v, int size, int entry)
    {
        if (!Assert(Original[entry] != IntPtr.Zero)) return;
        if (v != null && count > 0 && count <= 4096 / size && Withheld(Ints(l, new ReadOnlySpan<int>(v, count * size)))) return;
        ((delegate* unmanaged<int, int, int*, void>)Original[entry])(l, count, v);
    }

    [UnmanagedCallersOnly]
    private static void U1Fv(int l, int count, float* v) => Vector(l, count, v, 1, U1FvAt);

    [UnmanagedCallersOnly]
    private static void U2Fv(int l, int count, float* v) => Vector(l, count, v, 2, U2FvAt);

    [UnmanagedCallersOnly]
    private static void U3Fv(int l, int count, float* v) => Vector(l, count, v, 3, U3FvAt);

    [UnmanagedCallersOnly]
    private static void U4Fv(int l, int count, float* v) => Vector(l, count, v, 4, U4FvAt);

    [UnmanagedCallersOnly]
    private static void U1Iv(int l, int count, int* v) => Vector(l, count, v, 1, U1IvAt);

    [UnmanagedCallersOnly]
    private static void U2Iv(int l, int count, int* v) => Vector(l, count, v, 2, U2IvAt);

    [UnmanagedCallersOnly]
    private static void U3Iv(int l, int count, int* v) => Vector(l, count, v, 3, U3IvAt);

    [UnmanagedCallersOnly]
    private static void U4Iv(int l, int count, int* v) => Vector(l, count, v, 4, U4IvAt);

    // The engine sends no transposed matrices
    [UnmanagedCallersOnly]
    private static void M4Fv(int l, int count, byte transpose, float* v)
    {
        if (v != null && count > 0 && count <= 256 && transpose == 0 && Withheld(Floats(l, new ReadOnlySpan<float>(v, count * 16))))
            return;
        if (!Assert(Original[M4FvAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<int, int, byte, float*, void>)Original[M4FvAt])(l, count, transpose, v);
    }

    [UnmanagedCallersOnly]
    private static void ActiveTexture(uint texture)
    {
        _unit = texture - Texture0;
        if (!Assert(Original[ActiveAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<uint, void>)Original[ActiveAt])(texture);
    }

    [UnmanagedCallersOnly]
    private static void BindTexture(uint target, uint texture)
    {
        if (texture > 0) Heard(static s => TextureBound(s.texture, s.target), (texture, target));
        if (_unit >= MaxUnits || Rebound(_unit, target, texture)) (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        if (!Assert(Original[BindAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<uint, uint, void>)Original[BindAt])(target, texture);
    }

    [UnmanagedCallersOnly]
    private static void BindTextureUnit(uint unit, uint texture)
    {
        // DSA binds by the texture's own target (TargetOf); 0 unbinds every target
        var changed = unit >= MaxUnits;
        if (unit < MaxUnits && texture == 0)
        {
            changed = (UnitTextures[unit] | UnitArrays[unit] | UnitCubes[unit]) != 0;
            (UnitTextures[unit], UnitArrays[unit], UnitCubes[unit]) = (0, 0, 0);
        }
        else if (unit < MaxUnits) changed = Rebound(unit, TargetOf(texture), texture);

        if (changed) (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        if (!Assert(Original[BindUnitAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<uint, uint, void>)Original[BindUnitAt])(unit, texture);
    }

    [UnmanagedCallersOnly]
    private static void BindSampler(uint unit, uint sampler)
    {
        if (unit >= MaxUnits || UnitSamplers[unit] != sampler) (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        if (unit < MaxUnits) UnitSamplers[unit] = sampler;
        if (!Assert(Original[SamplerAt] != IntPtr.Zero)) return;
        ((delegate* unmanaged<uint, uint, void>)Original[SamplerAt])(unit, sampler);
    }
}

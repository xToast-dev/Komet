using HarmonyLib;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// OpenTK calls every GL function through GL's EntryPoints table, so its entries are where all callers pass (inlined
// setters and raw uploads too). Komet's own GL work runs Quietly: no listener hears it.
internal static unsafe partial class GlTap
{
    // Optional: an entry OpenTK's table lacks is left out instead of failing the tap
    private readonly record struct Entry(string Name, IntPtr Function, bool Optional = false);

    private const int MaxEntries = 224;

    private static readonly IntPtr[] Original = new IntPtr[MaxEntries];
    private static readonly int[] At = new int[MaxEntries]; // each entry's place in OpenTK's table, -1 when it has none
    private static Entry[] _entries = [];
    private static IntPtr[]? _table;
    private static int _quiet;

    public static bool Tapped => _table is not null;

    // The render path's question (Rendering.Links.TapState): the engine's state as recorded, null without a tap
    public static Rendering.GlStateView? View()
    {
        if (!Tapped) return null;
        var state = State;
        var blended = false;
        for (var i = 0; i < MaxBuffers; i++) blended |= state.Blend.Blending(i);
        return new Rendering.GlStateView(state.DepthTest, state.DepthCompare, state.DepthWrite, blended, state.Culls, state.CounterClockwise, state.Depth);
    }

    // Told of every texture about to be deleted, while tapped; its owner sets and clears it
    public static Action<uint>? Deleting { get; set; }

    // Told of every FBO about to be deleted, while tapped (its name may come back with other attachments)
    public static Action<uint>? FramebufferGone { get; set; }

    // Told of every program about to be deleted, while tapped (its name may come back for another program)
    public static Action<uint>? ProgramGone { get; set; }

    // Told of every program linked while tapped with vertex and fragment stages (its sources are the record's now)
    public static Action<uint>? Linking { get; set; }

    // Every entry in at once; true when tapped already
    public static bool Tap()
    {
        if (_table is not null) return true;
        var table = AccessTools.Field(typeof(GL), "EntryPoints")?.GetValue(null) as IntPtr[];
        var names = AccessTools.Field(typeof(GL), "EntryPointNames")?.GetValue(null) as string[];
        if (table is null || names is null || !Assert(table.Length == names.Length)) return false;
        _entries = [.. ProgramEntries(), .. StateEntries(), .. Drawings(), .. Uploads(), .. ObjectEntries(), .. RecordEntries()];
        if (!Assert(_entries.Length <= MaxEntries) || !Assert(_entries.Length == RecordsAt + RecordCount) || !Assert(Ordered()))
        {
            (_entries, Missing) = ([], "entry order (GlTap lists)"); // not a GL function: the tap's own lists are off
            return false;
        }

        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
        {
            At[i] = Array.IndexOf(names, _entries[i].Name);
            Original[i] = At[i] >= 0 ? table[At[i]] : IntPtr.Zero;
            if (Original[i] != IntPtr.Zero) continue;
            if (_entries[i].Optional)
            {
                At[i] = -1;
                continue;
            }

            Array.Clear(Original);
            (_entries, Missing) = ([], _entries[i].Name);
            return false;
        }

        Seed();
        SeedObjects();
        SeedStencil();
        // Untapped calls went uncounted: nothing seen before may be taken as unchanged
        (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
            if (At[i] >= 0)
                table[At[i]] = _entries[i].Function;
        (_table, Missing) = (table, "");
        Taps++;
        return true;
    }

    // Each list starts where its numbered constants say, and the entries called from other files are where they are named
    private static bool Ordered() =>
        Index(RecordsAt + StencilAt, _entries.Length) && _entries[UseAt].Name == "glUseProgram" &&
        _entries[EnableAt].Name == "glEnable" && _entries[TouchAt].Name == "glDrawArrays" &&
        _entries[BufferDataAt].Name == "glBufferData" && _entries[NamedBufferDataAt].Name == "glNamedBufferData" &&
        _entries[TexImage2DAt].Name == "glTexImage2D" && _entries[ObjectsAt].Name == "glBindBuffer" &&
        _entries[RecordsAt].Name == "glCreateShader" && _entries[RecordsAt + SpecsAt].Name == "glCompressedTexImage2D" &&
        _entries[RecordsAt + StencilAt].Name == "glStencilFunc";

    // The GL function OpenTK's table lacks, when the last Tap failed
    public static string Missing { get; private set; } = "";

    // Taps so far: a renderer made under one heard nothing between two (Vulkan-only)
    public static long Taps { get; private set; }

    // An exception must not leave an [UnmanagedCallersOnly] tap (the runtime ends the game). The first drops all
    // listeners, GL runs on alone, the renderer fails next frame
    public static string Fault { get; private set; } = "";

    private static void Faulted(Exception e)
    {
        if (Fault.Length == 0) Fault = $"a GL tap threw {e.GetType().Name}: {e.Message}";
        try
        {
            Unlisten();
        }
        catch (Exception again) when (again is not OutOfMemoryException)
        {
            _resting = false; // sending the withheld uniforms threw: GL draws on with older ones, the game goes on
        }
    }

    // The listeners go before Resting: waking GL sends withheld uniforms (GL calls), which may throw
    private static void Unlisten()
    {
        (Drawing, Clearing, Touching, Writes, Querying, Answering) = (null, null, null, null, null, null);
        (Blitting, Reading, TextureClearing) = (null, null, null);
        Resting = false; // OpenGL draws again at once
    }

    private static void Heard<T>(Action<T> heard, T state) where T : allows ref struct
    {
        try
        {
            heard(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Faulted(e);
        }
    }

    private static TResult Asked<T, TResult>(System.Func<T, TResult> asked, T state, TResult failed) where T : allows ref struct
    {
        try
        {
            return asked(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Faulted(e);
            return failed;
        }
    }

    private static void Changed(uint texture)
    {
        if (Writes is { } writes) Heard(static s => s.writes.TextureChanged(s.texture), (writes, texture));
    }

    private static void Unseen(uint buffer, string why)
    {
        if (Writes is { } writes) Heard(static s => s.writes.BufferUnseen(s.buffer, s.why), (writes, buffer, why));
    }

    private static void Queried(uint target, uint id)
    {
        if (Querying is { } querying && Assert(target > 0)) Heard(static s => s.querying(s.target, s.id), (querying, target, id));
    }

    private static long? Answered(uint id, uint name) => Answering is { } answering && Assert(name > 0)
        ? Asked(static s => s.answering(s.id, s.name), (answering, id, name), null)
        : null;

    public static void Untap()
    {
        if (_table is not { } table) return;
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
            if (At[i] >= 0 && Original[i] != IntPtr.Zero)
                table[At[i]] = Original[i];
        (_table, _quiet, Fault) = (null, 0, "");
        Unlisten();
        Unrecord();
        Unlean();
        Reads.Clear();
        Programs.Clear();
        (_using, _current, _unit) = (null, 0, 0);
        DrawBuffers.Clear();
        DrawOutputs.Clear();
        Array.Clear(Blends);
        Arrays.Clear();
        TextureTargets.Clear();
        (_array, _vao) = (null, 0);
        Array.Clear(Uniforms);
        Array.Clear(Storages);
        Array.Clear(UnitArrays);
        Array.Clear(UnitCubes);
    }

    public static QuietScope Quietly()
    {
        _quiet++;
        _ = Assert(_quiet < 64); // a scope never disposed
        return new QuietScope(true);
    }

    public readonly struct QuietScope(bool live) : IDisposable
    {
        public void Dispose() => Loud(live);
    }

    private static void Loud(bool live)
    {
        if (live && Assert(_quiet > 0)) _quiet--;
    }
}

// Blits go to Blitting first, which may take them over (GL skips them). Each FBO's read buffer is followed via
// glReadBuffer, asked once for one set before tapping (while bound for reading). A 2D texture's mipmaps may be made in
// its copy too (Writes.TextureMipmapped), else it changed.
internal static unsafe partial class GlTap
{
    public const int ReadNone = -1, ReadUnknown = -2;
    private const int MaxReads = 256;

    private static readonly Dictionary<uint, int> Reads = [];

    // Read: read framebuffer and its read buffer's attachment (or ReadNone, ReadUnknown); From, To as GL takes them
    public readonly record struct BlitCall(int Read, int ReadAttachment, int Draw, (int X0, int Y0, int X1, int Y1) From,
        (int X0, int Y0, int X1, int Y1) To, uint Mask, uint Filter);

    // Only blits between framebuffer objects
    public static System.Func<BlitCall, bool>? Blitting { get; set; }

    // glReadPixels (Name 0: read framebuffer, Framed, read buffer) or glGetTexImage of 2D Name's level, per GL_PACK_*
    public readonly record struct ReadCall(int Fbo, int Attachment, uint Name, int Level,
        (int X, int Y, int Width, int Height) Rect, (uint Format, uint Type) Data, nint Pixels);

    // Vulkan-only answers a read from its own images (OpenGL skips it then)
    public static System.Func<ReadCall, bool>? Reading { get; set; }

    // Vulkan-only runs (VulkanRenderer.GlIdle): GL rests. Draws, clears, blits, copies or dispatches Vulkan did not
    // take go missing rather than run by GL into unseen objects. Uploads, names, state, unanswered reads and Komet's
    // quiet work still reach GL ("shadow GL") so it takes over at once when Vulkan-only lets go
    public static bool Resting
    {
        get => _resting;
        set
        {
            if (!value && _resting) SendUnsent(); // GL draws again: the uniforms it was not sent first
            _resting = value;
        }
    }

    private static bool _resting;

    // Stranded, the GUI's stage is OpenGL's (VulkanRenderer.Overlaid): the way out of the world
    private static bool Rests => (Resting || (Stranded && !GuiStage)) && _quiet == 0;

    // VulkanQuietUniforms: resting GL is not sent uniforms the mirror holds (Withheld), only once it draws again
    public static bool QuietUniforms { get; set; } = true;

    private static bool MutedUniforms => QuietUniforms && _resting && _quiet == 0;

    private static bool ReadAnswered(ReadCall call) =>
        _quiet == 0 && Reading is { } reading && Assert(call.Level >= 0) && Asked(reading, call, false);

    private static bool Blitted(uint read, uint draw, ReadOnlySpan<int> corners, uint mask, uint filter)
    {
        if (_quiet != 0 || Blitting is not { } blitting || read == 0 || draw == 0) return false;
        return Asked(blitting, new BlitCall((int)read, ReadBufferOf(read), (int)draw, (corners[0], corners[1], corners[2], corners[3]),
            (corners[4], corners[5], corners[6], corners[7]), mask, filter), false);
    }

    public static int ReadBufferOf(uint framebuffer)
    {
        if (framebuffer == 0) return ReadUnknown;
        if (Reads.TryGetValue(framebuffer, out var known)) return known;
        if (Defaulted(framebuffer, true)) return 0; // GL_COLOR_ATTACHMENT0
        if (framebuffer != _readFramebuffer) return ReadUnknown;
        // bound for reading: OpenGL knows; the answer is kept while the table has room
        var mode = ReadOf((uint)GL.GetInteger(GetPName.ReadBuffer));
        if (Reads.Count < MaxReads) Reads[framebuffer] = mode;
        return mode;
    }

    private static int ReadOf(uint mode) => mode switch
    {
        >= GlColor0 and < GlColor0 + 16 => (int)(mode - GlColor0),
        0 => ReadNone,
        _ => ReadUnknown
    };

    // A texture parameter set: texture 0's too, whose sampling is then no longer default. tracked: the call names a
    // texture the units follow (a bind-target call of another target names none of theirs and is left alone)
    private static void Tuned(uint texture, uint name, float value, bool tracked = true)
    {
        if (!tracked) return;
        if (texture != 0) TextureTuned(texture, name, value);
        if (texture == 0 && Assert(name > 0)) DefaultTextureTouched = true;
        else if (Writes is { } writes) Heard(static s => s.writes.TextureTuned(s.texture, s.name, s.value), (writes, texture, name, value));
    }

    // Komet's own framebuffers (quiet) are no blit's source: their read buffers are not followed. Past MaxReads a new
    // framebuffer's read buffer is asked from OpenGL whenever it is bound for reading
    private static void ReadSet(uint framebuffer, uint mode)
    {
        if (framebuffer == 0 || _quiet > 0) return;
        if (FramebufferRecords.TryGetValue(framebuffer, out var f)) f.ReadDefault = false;
        if (Reads.Count < MaxReads || Reads.ContainsKey(framebuffer)) Reads[framebuffer] = ReadOf(mode);
    }
}

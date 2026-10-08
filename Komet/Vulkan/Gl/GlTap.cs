using HarmonyLib;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// OpenTK calls each GL function through GL's EntryPoints table, so replacing its entries is where every caller must pass (no
// caller escapes, inlined setters and raw uploads included). Komet's own OpenGL work runs Quietly: nothing listening hears of it.
internal static unsafe partial class GlTap
{
    private enum Group
    {
        Programs,
        State,
        Touch,
        Objects
    }

    private readonly record struct Entry(string Name, IntPtr Function, Group Kind);

    private const int MaxEntries = 160;

    private static readonly IntPtr[] Original = new IntPtr[MaxEntries];
    private static readonly int[] At = new int[MaxEntries]; // each entry's place in OpenTK's table, -1 when it has none
    private static Entry[] _entries = [];
    private static IntPtr[]? _table;
    private static int _quiet;

    public static bool Tapped => _table is not null;

    // Whether the Objects and Touch groups are in
    public static bool Scene { get; private set; }

    public static Action<uint>? Binding { get; set; }

    // Told of every texture about to be deleted, while tapped; its owner sets and clears it
    public static Action<uint>? Deleting { get; set; }

    // Told of every framebuffer object about to be deleted, while tapped (its name may come back with other attachments)
    public static Action<uint>? FramebufferGone { get; set; }

    // Told of every program about to be deleted, while tapped (its name may come back for another program)
    public static Action<uint>? ProgramGone { get; set; }

    public static bool Tap(bool scene = false)
    {
        if (_table is not null) return scene == Scene;
        var table = AccessTools.Field(typeof(GL), "EntryPoints")?.GetValue(null) as IntPtr[];
        var names = AccessTools.Field(typeof(GL), "EntryPointNames")?.GetValue(null) as string[];
        if (table is null || names is null || !Assert(table.Length == names.Length)) return false;
        _entries = scene
            ? [.. ProgramEntries(), .. StateEntries(), .. Drawings(), .. Uploads(), .. ObjectEntries()]
            : [.. ProgramEntries(), .. StateEntries()];
        if (!Assert(_entries.Length <= MaxEntries)) return false;
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
        {
            At[i] = Array.IndexOf(names, _entries[i].Name);
            Original[i] = At[i] >= 0 ? table[At[i]] : IntPtr.Zero;
            if (Original[i] != IntPtr.Zero) continue;
            Array.Clear(Original);
            (_entries, Missing) = ([], _entries[i].Name);
            _ = Assert(Missing.Length > 0) && Assert(Binding is null || _entries.Length == 0);
            return false;
        }

        Seed();
        if (scene) SeedObjects();
        // what happened untapped went uncounted: nothing seen before may be taken as unchanged
        (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++) table[At[i]] = _entries[i].Function;
        (_table, Scene, Missing) = (table, scene, "");
        return true;
    }

    // The GL function OpenTK's table lacks, when the last Tap failed
    public static string Missing { get; private set; } = "";

    public static void Untap()
    {
        if (_table is not { } table || !Assert(_entries.Length <= MaxEntries)) return;
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
            if (At[i] >= 0 && Original[i] != IntPtr.Zero)
                table[At[i]] = Original[i];
        (_table, Binding, Scene, _quiet) = (null, null, false, 0);
        (Drawing, Clearing, Touching, Writes, Querying, Answering) = (null, null, null, null, null, null);
        (Quieting, Blitting) = (null, null);
        Reads.Clear();
        Programs.Clear();
        SamplerUnits.Clear();
        (_using, _samplers, _current, _unit) = (null, null, 0, 0);
        _ = Assert(Programs.Count == 0) && Assert(UnitTextures.Length == MaxUnits);
        Buffers.Clear();
        Outputs.Clear();
        Array.Clear(Blends);
        _ = Assert(Buffers.Count == 0) && Assert(Blends[0] == 0);
        Arrays.Clear();
        TextureTargets.Clear();
        (_array, _vao) = (null, 0);
        Array.Clear(Uniforms);
        Array.Clear(Storages);
        Array.Clear(UnitArrays);
        Array.Clear(UnitCubes);
        _ = Assert(Arrays.Count == 0) && Assert(TextureTargets.Count == 0) && Assert(!Tapped);
    }

    public static QuietScope Quietly()
    {
        _quiet++;
        _ = Assert(_quiet is > 0 and < 64);
        return new QuietScope(true);
    }

    // Quiet work is about to run a call of the Touch group
    public static Action? Quieting { get; set; }

    public readonly struct QuietScope(bool live) : IDisposable
    {
        public void Dispose() => Loud(live);
    }

    private static void Loud(bool live)
    {
        if (live && Assert(_quiet > 0)) _quiet--;
        _ = Assert(_quiet >= 0);
    }
}

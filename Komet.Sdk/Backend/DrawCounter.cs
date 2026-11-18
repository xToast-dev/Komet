using Komet.Rendering;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// Which programs the last frame used: ModShaders.Used (ShaderProgramBase.Use's postfix, also while the engine's Use is
// replaced) notes the frame per PassId, a renderer at the very start of the Before stage turns the frame over. A program a pass skips on most frames
// (the shadow reuse keeps maps) counts as drawn while it was used within the last Recent frames.
//
// API, main thread: Drawn whether a program was used within Recent frames, FramesSince the frames since its last use,
// Frame the frames counted, Listening is set by ShaderModules.
// The count of a reloaded program is valid one frame after the reload: a build reads Drawn on a later frame.
// Names map to programs as Use sees them: the engine's built-in programs (sky, chunkopaque …, registered by
// EnumShaderProgram) are missing from its by-name table, so GetProgramByName("sky") is null and asking it made every
// built-in program look unused.
internal static class DrawCounter
{
    private const int MaxPasses = 1024, Recent = 30;

    private static readonly long[] LastUsed = new long[MaxPasses];
    private static readonly ShaderProgramBase?[] Programs = new ShaderProgramBase?[MaxPasses];
    private static readonly Dictionary<string, int> ByName = new(StringComparer.Ordinal);
    private static ICoreClientAPI? _api;
    private static Marker? _marker;

    private static long Frame { get; set; }

    // The Studio shows the frame: the tap counts draws too, ShaderModules attributes programs to renderers
    public static bool Listening { get; set; }

    internal static void Install(ICoreClientAPI api)
    {
        Clear();
        _api = api;
        _marker = new Marker();
        api.Event.RegisterRenderer(_marker, EnumRenderStage.Before, "komet-studio-frame");
    }

    internal static void Clear()
    {
        if (_api is { } api && _marker is { } marker) api.Event.UnregisterRenderer(marker, EnumRenderStage.Before);

        (_api, _marker, Frame) = (null, null, 0);
        Array.Clear(LastUsed);
        Array.Clear(Programs);
        ByName.Clear();
        Listening = false;
    }

    // ModShaders.Used: one Use of the program with this PassId
    internal static void Used(ShaderProgramBase program)
    {
        var pass = program.PassId;
        UsesSeen++;
        if ((uint)pass < MaxPasses)
        {
            LastUsed[pass] = Frame;
            if (!ReferenceEquals(Programs[pass], program))
            {
                Programs[pass] = program; // a reload's new program object: its name and GL id follow
                if (program.PassName is { Length: > 0 } name) ByName[name] = pass;
            }
        }

        if (Listening) ShaderModules.Used(pass);
    }

    public static bool Drawn(string name) => FramesSince(name) <= Recent;

    // Frames since the program was last used; long.MaxValue when never (or unknown)
    private static long FramesSince(string name)
    {
        var pass = Pass(name);
        return pass >= 0 && LastUsed[pass] > 0 ? Frame - LastUsed[pass] : long.MaxValue;
    }

    // The program a name stands for: the one Use last saw under it, else the engine's by-name table (mod programs)
    public static ShaderProgramBase? Program(string name)
    {
        if (_api is { } api && ShaderMods.ProgramByName(api, name) is { } current)
        {
            return current; // the registry's object now: after a reload the one Use saw last is disposed
        }

        return ByName.TryGetValue(name, out var pass) ? Programs[pass] : null;
    }

    // What the counter knows of a name, for a "not visible" that contradicts the screen: the program it resolved to
    // (its PassId, GL id, whether it is the registry's object), its last use and the frame now
    public static string Evidence(string name)
    {
        var program = Program(name);
        var pass = Pass(name);
        var since = FramesSince(name);
        var registry = _api is { } api && ShaderMods.ProgramByName(api, name) is { } current && ReferenceEquals(current, program);
        return "pass " + pass + ", program " + (program?.ProgramId ?? 0) + (registry ? "" : " (not the registry's)") +
               ", last use " + (since == long.MaxValue ? "never" : since + " frames ago") + ", frame " + Frame +
               ", hook " + (StudioHooks.Used is null ? "missing" : "set") + ", uses seen " + UsesSeen;
    }

    // Uses counted since the install, any program: 0 means the Use postfix never reached the counter
    private static long UsesSeen { get; set; }

    // The PassId the program has now (a reload may give it another)
    private static int Pass(string name) =>
        Program(name) is { } program && (uint)program.PassId < MaxPasses ? program.PassId : -1;

    // First of the Before stage: the frame turns over
    private sealed class Marker : IRenderer
    {
        public double RenderOrder => double.MinValue;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => NextFrame();

        private static void NextFrame()
        {
            Frame++;
            ShaderModules.NextFrame();
        }

        public void Dispose()
        {
        }
    }

    internal static bool IsMarker(IRenderer? renderer) => renderer is Marker;
}

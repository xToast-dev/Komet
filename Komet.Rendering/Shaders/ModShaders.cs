using System.Text.RegularExpressions;
using HarmonyLib;
using Komet.Api;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// KometShaders' defines, uniforms and source edits for the game's shader programs. Defines and edits go in as a program
// loads (ShaderEdits.Edit, after Komet's own edits); uniforms are set in a postfix of ShaderProgramBase.Use, at locations
// looked up once per program. Under Vulkan-only the edited programs are ported like the game's own, and uniform writes
// reach Vulkan through the tap. A mod's callback that throws is logged once and skipped.
internal static class ModShaders
{
    private const int MaxLocations = 1 << 14, MaxSource = 1 << 20;

    private sealed record DefineEntry(string Mod, string Name, Func<string> Value, string[] Programs);

    private sealed record EditEntry(string Mod, string Program, System.Func<string, string>? Vertex, System.Func<string, string>? Fragment);

    // Key: unique per registration, so the location cache, asked on every Use for every uniform, hashes two ints, not the name
    private sealed record UniformEntry(string Mod, string Name, Func<float[]> Value, int Key)
    {
        // The value of frame Frame: the API asks once per frame, Use comes per program and pass
        public long Frame;
        public float[]? Last;
    }

    private static readonly List<DefineEntry> Defines = [];
    private static readonly List<EditEntry> Edits = [];
    private static readonly List<UniformEntry> Uniforms = [];
    private static readonly Dictionary<(int Program, int Uniform), int> Locations = [];
    private static readonly HashSet<string> Reported = [];
    private static readonly HashSet<string> Off = []; // mods the player switched off on the overview page
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;
    private static bool _reloadAsked;
    private static int _keys; // the last UniformEntry.Key

    // a field, not [GeneratedRegex]: generated sources under obj/ confuse CodeQL's flow analysis
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool Installed { get; private set; }

    // A registration came or went, a mod was switched, a pass failed: the overview page is built anew
    internal static event Action? Changed;

    internal static void Touch() => Changed?.Invoke();

    internal static string[] Disabled() => [.. Off];

    internal static bool IsOn(string mod) => NotNull(mod) && !Off.Contains(mod);

    // A mod's defines, uniforms, edits and passes left out (off) or back in; the shaders reload
    internal static void Turn(string mod, bool on)
    {
        if (!NotNull(mod) || !Assert(mod.Length > 0)) return;
        if (on ? !Off.Remove(mod) : !Off.Add(mod)) return;
        RequestReload();
        Touch();
    }

    // Every mod with a registration, in the order first seen (defines, uniforms, edits, then passes)
    internal static List<string> Mods()
    {
        return [.. Defines.Select(d => d.Mod).Concat(Uniforms.Select(u => u.Mod)).Concat(Edits.Select(e => e.Mod))
            .Concat(ModPasses.Listed().Select(p => p.Mod)).Distinct()];
    }

    internal static (int Defines, int Uniforms, int Edits) Count(string mod) =>
        NotNull(mod) ? (Defines.Count(d => d.Mod == mod), Uniforms.Count(u => u.Mod == mod), Edits.Count(e => e.Mod == mod)) : default;

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        (_api, _logger, Installed) = (api, logger, false);
        var use = AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        if (!NotNull(harmony) || !NotNull(use)) return;
        Installed = harmony.Patch(use, postfix: new HarmonyMethod(typeof(ModShaders), nameof(Used))) is not null;
    }

    // The world closed: no registration, callback or program id outlives it
    public static void Clear()
    {
        Defines.Clear();
        Edits.Clear();
        Uniforms.Clear();
        Locations.Clear();
        Reported.Clear();
        Off.Clear();
        Changed = null;
        (_api, _reloadAsked, Installed) = (null, false, false);
        KometShaders.Clear();
    }

    public static IDisposable Define(string mod, string name, Func<string> value, string[] programs)
    {
        if (!Valid(mod, name) || !NotNull(value) || !Assert(Defines.Count < KometShaders.MaxDefines)) return Registration.None;
        var entry = new DefineEntry(mod, name, value, programs ?? []);
        Defines.Add(entry);
        RequestReload();
        Touch();
        return new Registration(() =>
        {
            if (!Defines.Remove(entry)) return;
            RequestReload();
            Touch();
        });
    }

    public static IDisposable Uniform(string mod, string name, Func<float[]> value)
    {
        if (!Valid(mod, name) || !NotNull(value) || !Assert(Uniforms.Count < KometShaders.MaxUniforms)) return Registration.None;
        _keys++;
        var entry = new UniformEntry(mod, name, value, _keys);
        Uniforms.Add(entry);
        Touch();
        return new Registration(() =>
        {
            if (Uniforms.Remove(entry)) Touch();
        });
    }

    public static IDisposable Edit(string mod, string program, System.Func<string, string>? vertex, System.Func<string, string>? fragment)
    {
        if (mod.Length == 0 || program.Length == 0 || (vertex is null && fragment is null) ||
            !Assert(Edits.Count < KometShaders.MaxEdits)) return Registration.None;
        var entry = new EditEntry(mod, program, vertex, fragment);
        Edits.Add(entry);
        RequestReload();
        Touch();
        return new Registration(() =>
        {
            if (!Edits.Remove(entry)) return;
            RequestReload();
            Touch();
        });
    }

    // A pass program went: its id may come back for another
    internal static void Forget() => Locations.Clear();

    // Batched: the reload runs once on the main thread after this frame's registrations
    public static void RequestReload()
    {
        if (_reloadAsked || _api?.World is null || !NotNull(_api.Event)) return;
        _reloadAsked = true;
        _api.Event.EnqueueMainThreadTask(() =>
        {
            _reloadAsked = false;
            Locations.Clear(); // the programs are made anew under new ids
            ShaderEdits.Reload();
            ModPasses.Rebuild(); // their defines and KOMET_VULKAN too
        }, "komet-mod-shaders");
    }

    // ShaderEdits.Edit, as a program loads. The cached locations go even without defines or edits: a reload deletes every
    // program first, so a new one may get an old one's id, and a mod's uniform would be set at that old program's location
    internal static void Edit(ShaderProgram program)
    {
        Locations.Clear();
        if (Defines.Count == 0 && Edits.Count == 0) return;
        var name = program.PassName ?? "";
        var defines = DefineText(d => d.Programs.Length == 0 || Array.IndexOf(d.Programs, name) >= 0);
        foreach (var shader in (ReadOnlySpan<Shader?>)[program.VertexShader, program.FragmentShader])
            if (shader is not null && defines.Length > 0)
                shader.PrefixCode = (shader.PrefixCode ?? "") + defines;
        for (var i = 0; i < Math.Min(Edits.Count, KometShaders.MaxEdits); i++)
        {
            var edit = Edits[i];
            if ((edit.Program != "*" && edit.Program != name) || Off.Contains(edit.Mod)) continue;
            Rewrite(program.VertexShader, edit.Vertex, edit, name);
            Rewrite(program.FragmentShader, edit.Fragment, edit, name);
        }
    }

    // What a komet.json or komet-shader.json pack registers under: the prefix and its id (the mod's domain)
    internal const string PackPrefix = "shaderpack:";

    // A pass's defines: its mod's own, given to every program or named "pass:<id>". A pack's pass and the same mod's code
    // count as one mod: "shaderpack:mymod" and "mymod".
    internal static string PassDefines(string mod, string pass)
    {
        var (named, own) = ("pass:" + pass, Unpacked(mod));
        return DefineText(d => Unpacked(d.Mod) == own && (d.Programs.Length == 0 || Array.IndexOf(d.Programs, named) >= 0));
    }

    // A mod id without the pack prefix
    private static string Unpacked(string mod) => mod.StartsWith(PackPrefix, StringComparison.Ordinal) ? mod[PackPrefix.Length..] : mod;

    private static string DefineText(System.Func<DefineEntry, bool> takes)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Math.Min(Defines.Count, KometShaders.MaxDefines); i++)
        {
            var define = Defines[i];
            if (!takes(define) || Off.Contains(define.Mod)) continue;
            string value;
            try
            {
                value = define.Value() ?? "";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Report(define.Mod, define.Name, e);
                continue;
            }

            // one line: a value with a line break would end the define early
            _ = text.Append("#define ").Append(define.Name).Append(' ').Append(value.ReplaceLineEndings(" ")).Append('\n');
        }

        _ = Assert(text.Length < MaxSource);
        return text.ToString();
    }

    private static void Rewrite(Shader? shader, System.Func<string, string>? edit, EditEntry entry, string program)
    {
        if (shader is null || edit is null) return;
        try
        {
            var code = edit(shader.Code ?? "");
            if (code is { Length: > 0 and < MaxSource }) shader.Code = code;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(entry.Mod, program, e);
        }
    }

    // ShaderProgramBase.Use's postfix: each registered uniform the program declares, at its cached location
    internal static void Used(ShaderProgramBase __instance)
    {
        if (Uniforms.Count == 0 || !NotNull(__instance) || __instance.ProgramId <= 0) return;
        UsedBy(__instance.ProgramId);
    }

    // The same for a program in use that the engine does not know (a mod's pass)
    internal static void UsedBy(int id)
    {
        if (Uniforms.Count == 0) return;
        if (Locations.Count >= MaxLocations) Locations.Clear();
        for (var i = 0; i < Math.Min(Uniforms.Count, KometShaders.MaxUniforms); i++)
        {
            var uniform = Uniforms[i];
            if (Off.Count > 0 && Off.Contains(uniform.Mod)) continue;
            if (!Locations.TryGetValue((id, uniform.Key), out var at))
                Locations[(id, uniform.Key)] = at = GL.GetUniformLocation(id, uniform.Name);
            if (at < 0) continue;
            Set(at, uniform);
        }
    }

    private static void Set(int at, UniformEntry uniform)
    {
        var frame = Komet.Diagnostics.FrameClock.Frames; // 0: the frame patch is missing, ask every time as before
        if (frame != 0 && uniform.Frame == frame)
        {
            Upload(at, uniform.Last);
            return;
        }

        float[]? v;
        try
        {
            v = uniform.Value();
            (uniform.Frame, uniform.Last) = (frame, v);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(uniform.Mod, uniform.Name, e);
            return;
        }

        Upload(at, v);
    }

    // One to four floats as the uniform's size (ModPasses too)
    internal static void Upload(int at, float[]? v)
    {
        switch (v?.Length)
        {
            case 1: GL.Uniform1(at, v[0]); break;
            case 2: GL.Uniform2(at, v[0], v[1]); break;
            case 3: GL.Uniform3(at, v[0], v[1], v[2]); break;
            case >= 4: GL.Uniform4(at, v[0], v[1], v[2], v[3]); break;
        }
    }

    private static bool Valid(string mod, string name)
    {
        if (mod.Length > 0 && Identifier.IsMatch(name)) return true;
        _logger?.Warning("Komet shaders: {0} registered \"{1}\", which is no GLSL name: left out", mod, name);
        return false;
    }

    private static void Report(string mod, string what, Exception e)
    {
        if (!Reported.Add(mod + "/" + what)) return;
        _logger?.Warning("Komet shaders: {0}'s callback for {1} threw, it is skipped: {2}", mod, what, e.Message);
    }
}

// What KometShaders and KometOptions hand back: Dispose takes the registration away again, once
internal sealed class Registration(Action? undo) : IDisposable
{
    public static readonly Registration None = new(null);

    private Action? _undo = undo;

    public void Dispose()
    {
        var taken = Interlocked.Exchange(ref _undo, null);
        taken?.Invoke();
    }
}

// KometShaders' calls land here (Modules.Wire): the API lives below this module and cannot name it
internal sealed class ShaderBackend : IShaderBackend
{
    public static readonly ShaderBackend Instance = new();

    public IDisposable Define(string modId, string name, Func<string> value, string[] programs) =>
        NotNull(modId) && NotNull(name) ? ModShaders.Define(modId, name, value, programs) : Registration.None;

    public IDisposable Uniform(string modId, string name, Func<float[]> value) =>
        NotNull(modId) && NotNull(name) ? ModShaders.Uniform(modId, name, value) : Registration.None;

    public IDisposable Edit(string modId, string program, System.Func<string, string>? vertex, System.Func<string, string>? fragment) =>
        NotNull(modId) && NotNull(program) ? ModShaders.Edit(modId, program, vertex, fragment) : Registration.None;

    public void RequestReload() => ModShaders.RequestReload();

    public IDisposable AddPass(KometPass pass) => ModPasses.Add(pass);
}

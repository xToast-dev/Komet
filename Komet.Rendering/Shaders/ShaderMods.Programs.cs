using Komet.Core;
using Komet.Api;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ShaderMods: the mods' programs, their OpenGL compiles and Vulkan ports.
internal static partial class ShaderMods
{
    // Mods with shader files, in load order
    public static List<Found> All(ICoreAPI? api)
    {
        List<Found> found = [];
        if (api?.ModLoader?.Mods is not { } mods) return found;

        foreach (var mod in mods.Bounded(MaxMods))
        {
            if (mod?.Info is not { } info || info.ModID == KometMod.Id ||
                mod.SourcePath is not { Length: > 0 } source)
            {
                continue;
            }

            if (mod.SourceType is not (EnumModSourceType.ZIP or EnumModSourceType.Folder)) continue;

            if (!Seen.TryGetValue(source, out var known)) Seen[source] = known = Scan(api, mod);

            if (known is not null) found.Add(known);
        }

        return found;
    }

    // How its programs fare: (programs, failed in OpenGL, failed for Vulkan with the first reason, still translating)
    public static (int Programs, string[] Gl, (string Pass, string Why)[] Vulkan, int Pending) State(Found mod)
    {
        if (!NotNull(mod)) return (0, [], [], 0);

        var mine = Programs.Values.Where(p => Belongs(mod, p)).Take(MaxPrograms).ToList();
        var vulkan = ShaderHub.Renderer == KapiRenderer.Vulkan;
        List<(string, string)> failed = [];
        var pending = 0;
        foreach (var program in mine.Bounded(MaxPrograms))
        {
            if (!vulkan || !program.Compiled) continue;

            if (!Ported.TryGetValue(program.Pass, out var why))
            {
                pending++;
                Port(program);
            }
            else if (why.Length > 0)
            {
                failed.Add((program.Pass, why));
            }
        }

        return (mine.Count, [.. mine.Where(p => !p.Compiled).Select(p => p.Pass)], [.. failed], pending);
    }

    // Its file is the mod's: a replaced game file, one of the mod's own programs, or a replaced include it reads
    private static bool Belongs(Found mod, Program program)
    {
        var (domain, pass) = (program.Domain.ToLowerInvariant(), program.Pass.ToLowerInvariant());
        var file = domain == "game" && Stages.Any(stage => mod.Replaced.Contains("shaders/" + pass + stage));
        return file || mod.Own.Contains(domain + ":" + pass) ||
               Stages.Any(stage => mod.Elsewhere.Contains(domain + ":shaders/" + pass + stage)) ||
               program.Includes.Any(i =>
                   mod.Replaced.Contains("shaderincludes/" + i) || mod.Replaced.Contains("shaders/" + i));
    }

    // A program the scene has not translated yet: its sources read here (GL), the port on a worker
    private static void Port(Program program)
    {
        if (program.Id <= 0 || !Porting.Add(program.Pass)) return;

        var (port, error) = Links.Vulkan.ReadPort(program.Id, program.Pass);
        if (port is null)
        {
            Ported[program.Pass] = error.Length > 0 ? error : "its sources could not be read";
            return;
        }

        var (pass, key, kept) = (program.Pass, program.Domain + ":" + program.Pass, program.Kept);
        _ = Task.Run(() =>
        {
            try
            {
                port(); // records its outcome in Ported
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a translator that throws fails this port: else the faulted task goes unseen and the page says
                // "translating" for good
                Ported[pass] = "the port threw: " + e.Message;
            }

            var api = _api;
            api?.Event?.EnqueueMainThreadTask(() => Finished(key, pass, kept), "komet-shader-mods");
        });
    }

    // A port outlived by a recompile (Keep) wrote the old program's outcome: dropped, so State ports the new one
    private static void Finished(string key, string pass, int kept)
    {
        if (!Programs.TryGetValue(key, out var now) || now.Kept != kept)
        {
            _ = Ported.TryRemove(pass, out _);
            _ = Porting.Remove(pass);
        }

        Changed?.Invoke();
    }

    // The registry's programs as they stand (those compiled before Komet's patch)
    private static void Read()
    {
        var programs = Registry?.Invoke() ?? [];
        foreach (var program in programs.Bounded(MaxPrograms))
        {
            if (program is { PassName.Length: > 0 }) Keep(program, program is { ProgramId: > 0, LoadError: false });
        }
    }

    private static void AfterCompile(ShaderProgram? __instance, bool __result)
    {
        if (__instance is { PassName.Length: > 0 }) Keep(__instance, __result);
    }

    // A shader stage that does not compile: its source as the driver got it (the engine's prefix after #version) goes to
    // Logs/komet-shader-<file>.txt once a session, so the error's line number points into it
    private static void AfterStage(Shader? shader, bool __result)
    {
        if (shader is null || FileName?.Invoke(shader) is not { Length: > 0 } name) return;

        if (__result)
        {
            _ = StageErrors.Remove(name);
            return;
        }

        var log = GL.GetShaderInfoLog(shader.ShaderId);
        if (log.Length > 0)
        {
            if (StageErrors.Count >= MaxPrograms * 3) StageErrors.Clear();

            StageErrors[name] = log[..Math.Min(log.Length, 8192)];
        }

        if (shader.Code is not { } code || !Dumped.Add(name)) return;

        var at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
        var text = code.Insert(at, shader.PrefixCode ?? "");
        try
        {
            var file = Path.Join(GamePaths.Logs,
                "komet-shader-" + string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".txt");
            File.WriteAllText(file, text);
            _api?.Logger?.Notification("Komet shaders: the source of {0} that did not compile is in {1}", name, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _api?.Logger?.Warning("Komet shaders: the source of {0} could not be written: {1}", name, e.Message);
        }
    }

    internal static string? StageError(string relative)
    {
        foreach (var (file, log) in StageErrors.Bounded(MaxPrograms * 3))
        {
            if (file.EndsWith(relative, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase))
            {
                return log;
            }
        }

        return null;
    }

    private static void Keep(ShaderProgram program, bool compiled)
    {
        var domain = program.AssetDomain ?? "game";
        _kept++;
        Programs[domain + ":" + program.PassName] =
            new Program(program.ProgramId, domain, program.PassName, [.. program.includes.Take(64)], compiled, _kept);
        _ = Ported.TryRemove(program.PassName, out _); // compiled anew: its port is due again
        _ = Porting.Remove(program.PassName);
    }

    private static void Loading()
    {
        if (_api is { } api)
        {
            Swap(api);
            StudioHooks.Loading?.Invoke(api); // the SDK's active projects' copies go in
        }
    }

    // Checked once the reload is through: the mods recompile their programs in the ReloadShader event after this
    // postfix, so a check here read their last load. A program a Studio project's copy broke is the Studio's to report.
    private static void RegistryLoaded()
    {
        if (_api is not { } api || _validating || StudioHooks.Validate is null) return;

        _validating = true;
        api.Event.EnqueueMainThreadTask(() =>
        {
            _validating = false;
            // ReSharper disable once ConstantConditionalAccessQualifier the task runs later: the SDK may unhook meanwhile
            if (_api is { } now && StudioHooks.Validate?.Invoke(Diagnostics(now)) == true)
            {
                now.Logger?.Warning("Komet Shader Studio: a project copy without a good state broke a program, the game's file runs");
                _ = now.Shader.ReloadShaders();
            }
        }, "komet-shader-validate");
    }

    private static bool _validating;

    // Whether the program's last compile went through (Compile's result; LoadError only marks a missing file); null when
    // no program of that name compiled this session
    internal static bool? CompiledNow(string pass)
    {
        foreach (var program in Programs.Values.Bounded(MaxPrograms))
        {
            if (program.Pass == pass) return program.Compiled;
        }

        return null;
    }

    // A program's Vulkan port: "" translated, the reason it was not, null while it translates (started here if due)
    internal static string? PortState(ICoreAPI api, string pass)
    {
        _ = api;
        if (Ported.TryGetValue(pass, out var why)) return why;

        foreach (var program in Programs.Values.Bounded(MaxPrograms))
        {
            if (program.Pass == pass && program.Compiled)
            {
                Port(program);
                return Ported.GetValueOrDefault(pass);
            }
        }

        return "no compiled program " + pass;
    }
}

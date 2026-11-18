using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// The linked GL programs as the driver's binaries (glGetProgramBinary), kept in memory and under
// ModConfig/komet/.programs. A program whose stages, prefixes and attribute slots are the same as before takes its binary
// (glProgramBinary) instead of a link: on radeonsi and most drivers the link is where the shader is compiled for the GPU,
// so a reload (every Shader Studio build, every Komet switch that reloads shaders, the next start) skips that work for
// every program that did not change. A binary the driver refuses (a new driver, another GPU) is linked as before and
// replaced. The key holds the GL renderer and version, so another GPU or driver never reads a binary it did not write.
// Main thread (the GL context's).
internal static class ProgramCache
{
    private const int MaxMemory = 512, MaxBytes = 16 << 20;
    private const GetProgramParameterName BinaryLength = (GetProgramParameterName)0x8741;

    private static readonly Dictionary<string, (BinaryFormat Format, byte[] Data)> Memory = new(StringComparer.Ordinal);
    private static string _driver = "";

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static int Hits { get; private set; }
    public static int Misses { get; private set; }

    private static string Folder => Path.Join(Vintagestory.API.Config.GamePaths.ModConfig, "komet", ".programs");

    public static void Install(Harmony harmony)
    {
        var link = AccessTools.DeclaredMethod(typeof(ClientPlatformWindows),
            nameof(ClientPlatformWindows.CreateShaderProgram), [typeof(ShaderProgram)]);
        if (!NotNull(link)) return;

        _ = harmony.Patch(link, new HarmonyMethod(typeof(ProgramCache), nameof(Linking)) { priority = Priority.Low },
            postfix: new HarmonyMethod(typeof(ProgramCache), nameof(Linked)));
        Installed = true;
    }

    public static void Forget() => Memory.Clear();

    // Harmony: the binary of the same program in place of the link; false skips the engine's link
    private static bool Linking(ShaderProgram program, ref bool __result, out string? __state)
    {
        __state = null;
        // Vulkan draws: the GL context only feeds the ports, nothing to gain and its calls are tapped
        if (!Enabled || Api.KapiShaders.Renderer == Api.KapiRenderer.Vulkan || Key(program) is not { } key) return true;

        __state = key;
        if (Find(key) is not { } binary) return true;

        var id = GL.CreateProgram();
        GL.ProgramBinary(id, binary.Format, binary.Data, binary.Data.Length);
        GL.GetProgram(id, GetProgramParameterName.LinkStatus, out var linked);
        if (linked != 1)
        {
            GL.DeleteProgram(id); // the driver refused it: linked as before, the binary replaced
            _ = Memory.Remove(key);
            return true;
        }

        program.ProgramId = id;
        (__result, __state) = (true, null);
        Hits++;
        return false;
    }

    // Harmony: a fresh link's binary kept for the next time
    private static void Linked(ShaderProgram program, bool __result, string? __state)
    {
        if (__state is not { } key || !__result || program.ProgramId == 0) return;

        Misses++;
        GL.GetProgram(program.ProgramId, BinaryLength, out var length);
        if (length <= 0 || length > MaxBytes) return;

        var data = new byte[length];
        GL.GetProgramBinary(program.ProgramId, length, out _, out BinaryFormat format, data);
        if (Memory.Count >= MaxMemory) Memory.Clear();

        Memory[key] = (format, data);
        Save(key, format, data);
    }

    // The program as the driver sees it: every stage's code and prefix, the attribute slots, the GPU and driver
    private static string? Key(ShaderProgram program)
    {
        if (program.VertexShader is not { Code: { } vertex } vs || program.FragmentShader is not { Code: { } fragment } fs)
        {
            return null;
        }

        if (_driver.Length == 0)
        {
            _driver = GL.GetString(StringName.Renderer) + "|" + GL.GetString(StringName.Version) + "|" +
                      (Vintagestory.Client.ScreenManager.Platform is { UseSSBOs: true } ? "ssbo" : "");
        }

        var text = new StringBuilder(_driver).Append('\0').Append(vs.PrefixCode).Append('\0').Append(vertex).Append('\0')
            .Append(fs.PrefixCode).Append('\0').Append(fragment).Append('\0');
        if (program.GeometryShader is { } gs) _ = text.Append(gs.PrefixCode).Append('\0').Append(gs.Code).Append('\0');

        foreach (var (slot, name) in program.attributes.OrderBy(a => a.Key).ToArray().Bounded(64))
        {
            _ = text.Append(slot).Append('=').Append(name).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static (BinaryFormat Format, byte[] Data)? Find(string key)
    {
        if (Memory.TryGetValue(key, out var kept)) return kept;

        try
        {
            var file = Path.Join(Folder, key + ".bin");
            if (!File.Exists(file)) return null;

            var bytes = File.ReadAllBytes(file);
            if (bytes.Length <= 4) return null;

            var found = ((BinaryFormat)BitConverter.ToInt32(bytes, 0), bytes[4..]);
            if (Memory.Count < MaxMemory) Memory[key] = found;

            return found;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(string key, BinaryFormat format, byte[] data)
    {
        var bytes = new byte[data.Length + 4];
        BitConverter.GetBytes((int)format).CopyTo(bytes, 0);
        data.CopyTo(bytes, 4);
        _ = Task.Run(() =>
        {
            try
            {
                _ = Directory.CreateDirectory(Folder);
                var file = Path.Join(Folder, key + ".bin");
                File.WriteAllBytes(file + ".tmp", bytes);
                File.Move(file + ".tmp", file, true);
            }
            catch (IOException)
            {
                // kept in memory; the disk tries again next time
            }
            catch (UnauthorizedAccessException)
            {
                // a read-only config folder: memory only
            }
        });
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Komet.Rendering;

internal sealed class SheyderJit(ICoreClientAPI api)
{
    private const int MaxTypes = 4096, MaxMethods = 4096, MaxSystems = 4096;
    private readonly HashSet<Assembly> _prepared = [];

    internal void Prepare()
    {
        foreach (var system in (api.ModLoader?.Systems ?? []).Bounded(MaxSystems))
        {
            if (system is null ||
                !string.Equals(system.Mod?.Info?.ModID, "sheydermod", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assembly = system.GetType().Assembly;
            if (_prepared.Contains(assembly)) continue;

            var started = Stopwatch.GetTimestamp();
            var count = PrepareAssembly(assembly);
            _ = _prepared.Add(assembly);
            api.Logger?.Notification("Komet: prepared {0} SheyderMod methods ({1:F1} ms)", count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static int PrepareAssembly(Assembly assembly)
    {
        var count = 0;
        var attempts = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(assembly).Take(MaxTypes).OrderByDescending(RenderPriority)
                     .Bounded(MaxTypes))
        {
            if (type.ContainsGenericParameters) continue;

            foreach (var method in AccessTools.GetDeclaredMethods(type).Take(MaxMethods).Bounded(MaxMethods))
            {
                if (attempts >= MaxMethods) return count;

                attempts++;
                if (PrepareMethod(method)) count++;
            }
        }

        return count;
    }

    private static bool RenderPriority(Type type) =>
        typeof(IRenderer).IsAssignableFrom(type) ||
               type.FullName?.Contains("Shadow", StringComparison.OrdinalIgnoreCase) == true;

    private static bool PrepareMethod(MethodInfo method)
    {
        try
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) return false;

            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                      or MissingMemberException or InvalidProgramException or NotSupportedException
                                      or FileNotFoundException or FileLoadException)
        {
            return false;
        }
    }
}

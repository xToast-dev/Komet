using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ShaderMods: starting, pausing and resuming a mod's systems while the game runs.
internal static partial class ShaderMods
{
    // A held-back mod switched on: its systems run every phase now, in the game's order, then its files and the shaders
    // load again. False when it may not start (a dependency of another major version) or one of its phases threw.
    public static bool Start(ICoreClientAPI api, Found mod)
    {
        if (!NotNull(mod) || !Off.Contains(mod.Id) || Mismatch(api, mod.Id) is not null) return false;

        if (Pauses.Remove(mod.Id, out var paused))
        {
            return Resume(api, mod, paused); // ran before: its phases ran already
        }

        if (Serving.Contains(mod.Id))
        {
            return Resume(api, mod, new Paused([], [])); // its systems run: only its files come back
        }

        var systems = (api.ModLoader?.Systems ?? []).Where(s => s?.Mod?.Info?.ModID == mod.Id && s.ShouldLoad(api))
            .OrderBy(s => s.ExecuteOrder()).Take(MaxMods).ToList();
        _ = Off.Remove(mod.Id); // from here its phases are its own (Skip lets them through)
        Skipped.ExceptWith(systems);
        var started = true;
        foreach (var phase in Phases.Bounded(Phases.Length))
        {
            foreach (var system in systems.Bounded(MaxMods))
            {
                started &= Run(api, system, phase);
            }
        }

        Reloaded(api);
        Links.ShaderUi.ModStarted(api, mod.Id); // Komet's holds and the mod's settings from Komet's level
        api.Logger?.Notification("Komet: shader mod {0} started late ({1} systems){2}", mod.Id, systems.Count,
            started ? "" : ", a phase failed");
        return started;
    }

    // A running shader mod switched off: its renderers and Harmony patches go, the game's shader files come back and the
    // shaders load again, so the picture is the game's at once. Its systems stay alive (no second Start when switched on
    // again); the next world holds it back from the start.
    public static bool Pause(ICoreClientAPI api, Found mod)
    {
        if (!NotNull(mod) || Off.Contains(mod.Id) || api.World is not ClientMain game) return false;

        var assemblies = (api.ModLoader?.Systems ?? []).Where(s => s?.Mod?.Info?.ModID == mod.Id)
            .Select(s => s.GetType().Assembly)
            .Distinct().ToHashSet();
        var paused = new Paused([], []);
        if (mod.Content)
        {
            assemblies.Clear(); // its systems serve its content: only its files give way
        }

        var stages = game.eventManager.renderersByStage;
        for (var stage = 0; stage < Math.Min(stages.Length, 64); stage++)
        {
            foreach (var handler in stages[stage].ToArray().Bounded(1 << 12))
            {
                if (Owned(handler.Renderer, assemblies))
                {
                    var name = handler.ProfilingName ?? "";
                    var prefix = (EnumRenderStage)stage + "-";
                    paused.Renderers.Add((handler.Renderer, (EnumRenderStage)stage,
                        name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name));
                    api.Event.UnregisterRenderer(handler.Renderer, (EnumRenderStage)stage);
                }
            }
        }

        Unpatched(assemblies, paused);
        // its shader-reload listeners too: else it builds its own programs against the game's files Komet swapped in
        // (SheyderMod's deferredlighting then failed on the game's fogandlight.fsh and stayed marked broken)
        var reloads = game.eventManager.OnReloadShaders;
        for (var i = 0; i < Math.Min(reloads.Count, 1 << 12); i++)
        {
            if (reloads[i]?.Method.DeclaringType?.Assembly is { } owner && assemblies.Contains(owner))
            {
                paused.Reloads.Add((i, reloads[i]));
            }
        }

        foreach (var (_, handler) in paused.Reloads.Bounded(1 << 12))
        {
            _ = reloads.Remove(handler);
        }

        Pauses[mod.Id] = paused;
        _ = Off.Add(mod.Id); // from here the shader loads give its files way (Swap)
        if (mod.Content) _ = Serving.Add(mod.Id);

        Reloaded(api);
        Links.ShaderUi.ModStopped(mod.Id); // Komet's holds for it end (Vulkan-only may draw again)
        api.Logger?.Notification("Komet: shader mod {0} paused ({1} renderers, {2} patches out)", mod.Id,
            paused.Renderers.Count, paused.Patches.Count);
        Changed?.Invoke();
        return true;
    }

    // The mod's Harmony patches off its own methods: drop them before the shaders load again (Swap)
    private static void Unpatched(HashSet<Assembly> assemblies, Paused paused)
    {
        foreach (var target in Harmony.GetAllPatchedMethods().ToArray().Bounded(1 << 14))
        {
            if (Harmony.GetPatchInfo(target) is { } info)
            {
                foreach (var (patches, kind) in (ReadOnlySpan<(IReadOnlyCollection<Patch>, HarmonyPatchType)>)
                         [
                             (info.Prefixes, HarmonyPatchType.Prefix),
                             (info.Postfixes, HarmonyPatchType.Postfix),
                             (info.Transpilers, HarmonyPatchType.Transpiler),
                             (info.Finalizers, HarmonyPatchType.Finalizer)
                         ])
                {
                    foreach (var patch in patches.Where(p => p.PatchMethod.DeclaringType?.Assembly is { } assembly &&
                                                            assemblies.Contains(assembly)).Bounded(256))
                    {
                        paused.Patches.Add((target, patch, kind));
                        new Harmony(patch.owner).Unpatch(target, patch.PatchMethod);
                    }
                }
            }
        }
    }

    private static bool Resume(ICoreClientAPI api, Found mod, Paused paused)
    {
        _ = Off.Remove(mod.Id);
        _ = Serving.Remove(mod.Id);
        foreach (var (target, patch, kind) in paused.Patches.Bounded(1 << 14))
        {
            var method = new HarmonyMethod(patch.PatchMethod, patch.priority, patch.before, patch.after);
            var harmony = new Harmony(patch.owner);
            _ = kind switch
            {
                HarmonyPatchType.Prefix => harmony.Patch(target, method),
                HarmonyPatchType.Postfix => harmony.Patch(target, postfix: method),
                HarmonyPatchType.Transpiler => harmony.Patch(target, transpiler: method),
                _ => harmony.Patch(target, finalizer: method)
            };
        }

        if (api.World is ClientMain game)
        {
            var reloads = game.eventManager.OnReloadShaders;
            foreach (var (at, handler) in paused.Reloads.Bounded(1 << 12))
            {
                reloads.Insert(Math.Min(at, reloads.Count), handler);
            }
        }

        foreach (var (renderer, stage, name) in paused.Renderers.Bounded(1 << 12))
        {
            api.Event.RegisterRenderer(renderer, stage, name);
        }

        // patches that follow the terrain's meshes (SheyderMod mirrors every pool add and remove) missed what changed
        // while they were out: the terrain is built again, so every mesh passes them once more (else the distance
        // drew from stale pool ranges)
        if (paused.Patches.Exists(p => FollowsTerrain(p.Target))) TerrainRebuild.Request();

        Reloaded(api);
        Links.ShaderUi.ModStarted(api, mod.Id);
        api.Logger?.Notification("Komet: shader mod {0} resumed", mod.Id);
        Changed?.Invoke();
        return true;
    }

    private static bool FollowsTerrain(MethodBase target) =>
        target.DeclaringType is { } type && (type == typeof(Vintagestory.API.Client.MeshDataPool) ||
                                             type.Name.Contains("Tesselator", StringComparison.Ordinal) ||
                                             type.Name is "TCTCache" or "ClientWorldMap" or "ChunkRenderer");

    private static bool Owned(IRenderer? renderer, HashSet<Assembly> assemblies) =>
        renderer switch
        {
            null => false,
            DummyRenderer dummy => dummy.action?.Method.DeclaringType?.Assembly is { } own && assemblies.Contains(own),
            _ => assemblies.Contains(renderer.GetType().Assembly)
        };

    // The mods' files and the shaders read again: Swap (on the load) decides which mod's file each shader gets
    private static void Reloaded(ICoreClientAPI api)
    {
        _ = api.Assets?.Reload(AssetCategory.shaders);
        _ = api.Assets?.Reload(AssetCategory.shaderincludes);
        ShaderHub.Reload();
    }

    private static bool Run(ICoreClientAPI api, ModSystem system, Action<ModSystem, ICoreClientAPI> phase)
    {
        try
        {
            phase(system, api);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            api.Logger?.Error("Komet: {0} failed to start late: {1}", system, e);
            return false;
        }
    }
}

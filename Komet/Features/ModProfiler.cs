using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Features;

// Times every renderer and game tick listener and books it to the mod whose assembly owns the code.
// One timestamp pair per call while the HUD shows the mod timings; otherwise the engine loop runs untouched.
internal static class ModProfiler
{
    public sealed class Entry(string mod, string name)
    {
        public string Mod => mod;
        public string Name => name;
        public long Ticks { get; set; }
        public int Calls { get; set; }
        public double SmoothMs { get; set; }
        public double CallsPerSecond { get; set; }
    }

    public const int MaxEntries = 4096, MaxRenderers = 1024, MaxNesting = 16;
    public static bool Enabled { get; set; }
    public static Dictionary<object, Entry> Entries { get; } = [];   // key: renderer Type or handler MethodInfo, main thread only
    private static readonly Dictionary<Assembly, string> ModByAssembly = [];
    private static IModLoader? _loader;

    public static void Install(Harmony harmony, ICoreClientAPI capi)
    {
        if (!Assert(_loader is null) || !NotNull(capi.ModLoader)) return;   // installed once
        _loader = capi.ModLoader;
        var self = typeof(ModProfiler);
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        var entity = AccessTools.Method(typeof(GameTickListener), nameof(GameTickListener.OnTriggered));
        var block = AccessTools.Method(typeof(GameTickListenerBlock), nameof(GameTickListenerBlock.OnTriggered));
        if (!NotNull(stage) || !NotNull(entity) || !NotNull(block)) return;
        if (!NotNull(harmony.Patch(stage, prefix: new HarmonyMethod(self, nameof(RenderStage))))) return;
        if (!NotNull(harmony.Patch(entity, prefix: new HarmonyMethod(self, nameof(Start)), postfix: new HarmonyMethod(self, nameof(EndEntity))))) return;
        _ = NotNull(harmony.Patch(block, prefix: new HarmonyMethod(self, nameof(Start)), postfix: new HarmonyMethod(self, nameof(EndBlock))));
    }

    // ReSharper disable InconsistentNaming – Harmony injects fields and state by name
    private static bool RenderStage(ClientMain ___game, List<RenderHandler>[] ___renderersByStage, EnumRenderStage stage, float dt)
    {
        if (!Enabled) return true;
        if (!Index((int)stage, ___renderersByStage.Length)) return true;
        var list = ___renderersByStage[(int)stage];
        var debug = ___game.extendedDebugInfo;
        for (var i = 0; i < Math.Min(list.Count, MaxRenderers); i++)   // Count is re-read: a renderer may unregister itself
        {
            var handler = list[i];
            if (!NotNull(handler.Renderer)) continue;
            var start = Stopwatch.GetTimestamp();
            handler.Renderer.OnRenderFrame(dt, stage);
            Book(handler.Renderer is DummyRenderer dummy ? dummy.action.Method : handler.Renderer.GetType(), start);
            if (!debug) continue;
            ScreenManager.Platform.CheckGlError(handler.ProfilingName);
            ScreenManager.FrameProfiler.Mark(handler.ProfilingName);
        }
        return false;
    }

    private static void Start(out long __state) => __state = Enabled ? Stopwatch.GetTimestamp() : 0;

    private static void EndEntity(GameTickListener __instance, long __state)
    {
        if (__state == 0 || !NotNull(__instance.Handler)) return;
        Book(__instance.Handler.Method, __state);
    }

    private static void EndBlock(GameTickListenerBlock __instance, long __state)
    {
        if (__state == 0) return;
        Delegate? handler = __instance.Handler ?? (Delegate?)__instance.HandlerBare;
        if (NotNull(handler)) Book(handler.Method, __state);
    }
    // ReSharper restore InconsistentNaming

    private static void Book(object key, long start)
    {
        var ticks = Stopwatch.GetTimestamp() - start;
        if (!Assert(start > 0) || !Assert(ticks >= 0) || !Assert(key is Type or MethodInfo)) return;
        if (!Entries.TryGetValue(key, out var entry))
        {
            if (!Assert(Entries.Count < MaxEntries)) return;
            Entries[key] = entry = Create(key);
        }
        entry.Ticks += ticks;
        entry.Calls++;
    }

    private static Entry Create(object key)
    {
        if (key is Type type) return new Entry(ModOf(type.Assembly), type.Name);
        if (!Assert(key is MethodInfo) || key is not MethodInfo method) return new Entry("?", "?");
        var owner = method.DeclaringType;
        if (!NotNull(owner) || !Assert(method.Name.Length > 0)) return new Entry("?", method.Name);
        for (var depth = 0; depth < MaxNesting && owner.Name.StartsWith('<') && owner.DeclaringType != null; depth++) owner = owner.DeclaringType;   // lambdas live in generated nested classes
        var name = method.Name;
        var close = name.IndexOf('>', StringComparison.Ordinal);
        if (name.StartsWith('<') && Assert(close > 1)) name = name[1..close] + " (lambda)";
        return new Entry(ModOf(owner.Assembly), owner.Name + "." + name);
    }

    private static string ModOf(Assembly assembly)
    {
        if (ModByAssembly.TryGetValue(assembly, out var mod)) return mod;
        mod = assembly.GetName().Name ?? "?";
        if (!Assert(mod.Length > 0)) mod = "?";
        if (mod.StartsWith("Vintagestory", StringComparison.Ordinal)) mod = "game";
        if (!NotNull(_loader)) return mod;
        foreach (var candidate in _loader.Mods.Bounded(Runtime.Diagnostics.ModStats.MaxLoadedMods))
            if (candidate is ModContainer container && container.Assembly == assembly) mod = candidate.Info.ModID;
        return ModByAssembly[assembly] = mod;
    }
}

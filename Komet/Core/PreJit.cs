using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace Komet.Core;

internal enum PreJitState
{
    Idle,
    Running,
    Done,
    Cancelled
}

// None of the game's assemblies is ReadyToRun (all ILOnly, empty ManagedNativeHeader), so the first call of every engine method runs
// the tier-0 JIT on the thread that makes it, and for renderers, entity behaviors and dialogs that is the main thread: the first
// creature, the character dialog (IconUtil's SVG painters take 3-11 ms each to compile) or the handbook pay their JIT inside one frame.
// Every new tier-0 method also restarts the runtime's call-counting delay (TC_CallCountingDelayMs, 100 ms), and while first calls keep
// trickling in, no hot method anywhere in the process is promoted to tier 1.
//
// One background thread compiles the engine up front while the world loads. RuntimeHelpers.PrepareMethod compiles without executing:
// no static constructor runs, the code is the tier-0 code a first call would have produced, and tiering promotes it as usual later.
// Methods come from the metadata tables rather than reflection, so the walk does not build 40k MethodInfo objects. Left out are methods
// without IL (abstract, extern, runtime-implemented), generic code (it only compiles for an instantiation) and everything Harmony has
// patched by then, whose code MonoMod owns. A method patched later is patched over compiled code, which is Harmony's normal case. The
// exception is inlining: tier-0 inlines nothing, but the JIT compiles a few methods fully optimized at once (9 of ~42k in 1.22.7 by
// their MethodLoad tier), and those can keep an inlined copy of a callee that a mod patches only after the walk compiled them.
// The game does the same when it calls them before the patch; the walk only makes it earlier.
internal static partial class PreJit
{
    public const int CheckEvery = 256, Niceness = 10;
    public const string ThreadName = "komet-prejit";
    private const int MaxAssemblies = 8, MaxLoaded = 4096, MaxMethods = 1 << 17, MaxPatched = 1 << 16, MaxNesting = 16;
    private const int MethodDefTable = 0x06, PrioProcess = 0;

    // Behaviours and renderers first, they are what the join and the first minutes call for the first time
    private static readonly string[] Targets =
        ["VSEssentials", "VintagestoryAPI", "VintagestoryLib", "VSSurvivalMod", "VSCreativeMod"];

    private static volatile bool _cancel;
    private static Thread? _thread;
    private static int _skipped;
    private static bool _niced;

    // Read at every checkpoint, so switching it off stops a running walk; switching it on takes effect at the next world join
    public static bool Enabled { get; set; } = true;
    public static PreJitState State { get; private set; }

    // Totals of the last walk, not per interval: the HUD never resets them
    public static int Prepared { get; private set; }
    public static int Failed { get; private set; }
    public static double JitMs { get; private set; } // compile time on the walking thread only
    public static double WallMs { get; private set; }

    // Idle again, the totals zeroed and a cancel withdrawn: the next Start or Walk runs
    internal static void Reset()
    {
        // the walk owns them
        if (!Assert(_thread is not { IsAlive: true }) || !Assert(State != PreJitState.Running)) return;
        (_cancel, State, Prepared, Failed, _skipped, JitMs, WallMs, _niced) =
            (false, PreJitState.Idle, 0, 0, 0, 0, 0, false);
    }

    // Called last in StartClientSide: every Install has run, so Komet's own patches are in the skip set. Leaving the world stops the
    // walk through Stop in KometModSystem.Dispose.
    public static void Start(ILogger logger)
    {
        if (!NotNull(logger) || !Enabled) return;
        // compiled code outlives the world, and a cancelled walk is still winding down
        if (State == PreJitState.Done || _thread is { IsAlive: true }) return;
        Reset();
        var patched = PatchedSet(); // Harmony's registry is read here, on the thread that patches
        _thread = new Thread(() => Run(patched, logger))
        {
            Name = ThreadName, IsBackground = true, Priority = ThreadPriority.BelowNormal // effective on Windows only
        };
        _thread.Start();
    }

    public static void Stop() => _cancel = true;

    private static void Run(HashSet<(Guid, int)> patched, ILogger logger)
    {
        // a foreground walker would hold the process open after the game quits
        if (!NotNull(patched) || !NotNull(logger) || !Assert(Thread.CurrentThread.IsBackground)) return;
        State = PreJitState.Running;
        try
        {
            // inside the catch-all too: a libc that binds but misbehaves must not take the game down with this thread
            Lower();
            var wall = Stopwatch.StartNew();
            var jit = JitInfo.GetCompilationTime(true);
            foreach (var name in Targets.Bounded(MaxAssemblies))
            {
                if (_cancel || !Enabled) break;
                if (Find(name) is { } assembly) Walk(assembly, patched);
                (JitMs, WallMs) = ((JitInfo.GetCompilationTime(true) - jit).TotalMilliseconds,
                    wall.Elapsed.TotalMilliseconds);
            }

            State = _cancel || !Enabled ? PreJitState.Cancelled : PreJitState.Done;
            logger.Notification(
                "Komet: pre-compiled {0} engine methods in {1:F0} ms ({2:F0} ms JIT, {3} failed, {4} skipped, {5}): {6}",
                Prepared, WallMs, JitMs, Failed, _skipped, _niced ? "at nice " + Niceness : "at normal priority",
                State);
        }
        catch (Exception e)
        {
            State = PreJitState.Cancelled;
            logger.Warning("Komet: pre-compiling stopped after {0} methods: {1}", Prepared, e);
        }
    }

    // Thread.Priority is a no-op on Linux .NET; setpriority on the thread id is not. It is one-way without CAP_SYS_NICE, which is fine
    // for a thread that ends, but it must never hit another thread. nice 10 and not 19: at 19 the GC's time-to-suspend grew to 9 ms.
    private static void Lower()
    {
        if (!OperatingSystem.IsLinux() || !Assert(Thread.CurrentThread.Name == ThreadName)) return;
        try
        {
            var tid = GetTid();
            _niced = Assert(tid > 0) && SetPriority(PrioProcess, (uint)tid, Niceness) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _niced = false; // a libc without the symbols: the walk runs at normal priority
        }
    }

    private static Assembly? Find(string name)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        if (!Assert(name.Length > 0) || !Assert(loaded.Length < MaxLoaded)) return null;
        for (var i = 0; i < Math.Min(loaded.Length, MaxLoaded); i++)
            if (!loaded[i].IsDynamic && loaded[i].GetName().Name == name)
                return loaded[i];
        return null; // VSCreativeMod is absent when the mod is disabled
    }

    // The mods' assemblies come from Assembly.UnsafeLoadFrom (ModAssemblyLoader), so each one has a file to read the tables from
    internal static void Walk(Assembly assembly, IReadOnlySet<(Guid, int)> patched)
    {
        if (!NotNull(assembly) || !NotNull(patched) ||
            !Assert(assembly is { IsDynamic: false, Location.Length: > 0 })) return;
        try
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            Walk(pe.GetMetadataReader(), assembly.ManifestModule, patched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException
                                      or InvalidOperationException)
        {
            Failed++; // the whole assembly, unreadable
        }
    }

    // Stops within CheckEvery methods of a cancel or a switch-off
    private static void Walk(MetadataReader md, Module module, IReadOnlySet<(Guid, int)> patched)
    {
        var count = md.GetTableRowCount(TableIndex.MethodDef);
        if (!NotNull(module) || !Assert(count <= MaxMethods)) return;
        var id = module.ModuleVersionId;
        for (var i = 0; i < Math.Min(count, MaxMethods); i++)
        {
            if (i % CheckEvery == 0 && (_cancel || !Enabled)) return;
            var handle = MetadataTokens.MethodDefinitionHandle(i + 1); // rows are 1-based
            if (!Eligible(md, handle, id, patched)) _skipped++;
            else if (Prepare(module, MetadataTokens.GetToken(handle))) Prepared++;
            else Failed++;
        }
    }

    // PrepareMethod needs IL, and an exact instantiation for anything generic; patched methods belong to Harmony
    internal static bool Eligible(MetadataReader md, MethodDefinitionHandle handle, Guid module,
        IReadOnlySet<(Guid, int)> patched)
    {
        if (!NotNull(md) || !NotNull(patched) || !Assert(!handle.IsNil)) return false;
        var method = md.GetMethodDefinition(handle);
        return method.RelativeVirtualAddress != 0 && !IsGeneric(md, method) &&
               !patched.Contains((module, MetadataTokens.GetToken(handle)));
    }

    // A generic method, or any method of a generic type or of a type nested in one
    private static bool IsGeneric(MetadataReader md, MethodDefinition method)
    {
        if (method.GetGenericParameters().Count > 0) return true;
        var type = method.GetDeclaringType();
        if (!NotNull(md) || !Assert(!type.IsNil)) return true; // even global methods have a type, <Module>
        for (var depth = 0; depth < MaxNesting && !type.IsNil; depth++)
        {
            var definition = md.GetTypeDefinition(type);
            if (definition.GetGenericParameters().Count > 0) return true;
            type = definition.GetDeclaringType();
        }

        return !Assert(type.IsNil); // nested deeper than any compiler emits: leave it alone
    }

    // The exceptions a method the runtime cannot load or compile throws; the game would have hit them on the first call instead
    internal static bool Prepare(Module module, int token)
    {
        if (!NotNull(module) || !Assert(token >> 24 == MethodDefTable)) return false;
        try
        {
            RuntimeHelpers.PrepareMethod(module.ModuleHandle.ResolveMethodHandle(token));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                      or MissingMemberException or FileNotFoundException or FileLoadException
                                      or InvalidProgramException or MemberAccessException)
        {
            return false;
        }
    }

    // Keyed by module version id and token: the metadata walk never materialises a MethodBase to compare against
    internal static HashSet<(Guid, int)> PatchedSet()
    {
        HashSet<(Guid, int)> set = [];
        foreach (var method in Harmony.GetAllPatchedMethods().Bounded(MaxPatched))
            _ = NotNull(method) && set.Add((method.Module.ModuleVersionId, method.MetadataToken));
        return set;
    }

    [LibraryImport("libc", EntryPoint = "gettid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetTid();

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetPriority(int which, uint who, int value);
}

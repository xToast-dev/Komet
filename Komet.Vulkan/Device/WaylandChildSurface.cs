using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// A wl_surface of Vulkan's own, a subsurface filling the game's window, for Wayland drivers whose Vulkan WSI cannot
// present to a wl_surface that OpenGL's EGLSurface already owns (NVIDIA: vkCreateSwapchainKHR fails with surface lost).
// The compositor shows the child above GLFW's surface; input, focus, the toplevel and its EGLSurface stay GLFW's, so
// switching back to OpenGL touches nothing Vulkan owned. libwayland is already in the process (GLFW links it).
// Made and destroyed on the engine thread, never while the bridge thread presents: the queue of the registry roundtrip
// is private, so GLFW's events are left queued for GLFW.
internal sealed unsafe class WaylandChildSurface : IDisposable
{
    private const string Lib = "libwayland-client.so.0";
    private const uint MarshalDestroy = 1; // WL_MARSHAL_FLAG_DESTROY

    private const uint DisplayGetRegistry = 1,
        RegistryBind = 0,
        CompositorCreateSurface = 0,
        SubcompositorDestroy = 0,
        SubcompositorGetSubsurface = 1,
        SubsurfaceDestroy = 0,
        SubsurfaceSetPosition = 1,
        SubsurfaceSetDesync = 5,
        SurfaceDestroy = 0,
        SurfaceCommit = 6;

    private const uint CompositorMaxVersion = 6;

    private readonly IntPtr _compositor, _display, _subcompositor, _subsurface, _surface;

    private WaylandChildSurface(IntPtr display, IntPtr compositor, IntPtr subcompositor, IntPtr surface,
        IntPtr subsurface) =>
        (_display, _compositor, _subcompositor, _surface, _subsurface) =
        (display, compositor, subcompositor, surface, subsurface);

    // The wl_surface* Vulkan makes its swapchain for
    public nuint Surface => (nuint)_surface;

    // Null: this compositor offers no wl_compositor/wl_subcompositor (or libwayland is missing); the caller uses the
    // window's own surface as before
    public static WaylandChildSurface? Create(IntPtr display, nuint parent)
    {
        if (!OperatingSystem.IsLinux() || display == IntPtr.Zero || parent == 0) return null;
        if (!Symbols.Load(out var symbols)) return null;
        if (!Globals(display, symbols, out var compositor, out var version, out var subcompositor)) return null;

        var surface = symbols.CreateSurface(compositor, version);
        if (surface == IntPtr.Zero)
        {
            wl_proxy_destroy(subcompositor);
            wl_proxy_destroy(compositor);
            return null;
        }

        var subsurface = symbols.GetSubsurface(subcompositor, surface, parent);
        if (subsurface == IntPtr.Zero)
        {
            _ = Symbols.Flags(surface, SurfaceDestroy, IntPtr.Zero, 0, MarshalDestroy);
            wl_proxy_destroy(subcompositor);
            wl_proxy_destroy(compositor);
            return null;
        }

        // Filling the window at the top left, commits from Vulkan applied without waiting for GLFW's (otherwise the
        // engine's skipped SwapBuffers in Vulkan-only would hold the child's buffers back).
        _ = Symbols.SetPosition(subsurface, 0, 0);
        _ = Symbols.SetDesync(subsurface);
        _ = Symbols.Commit(surface);
        _ = wl_display_flush(display);
        return new WaylandChildSurface(display, compositor, subcompositor, surface, subsurface);
    }

    // Bind the two globals on a private queue: a registry roundtrip there leaves GLFW's event queue alone
    private static bool Globals(IntPtr display, Symbols symbols, out IntPtr compositor, out uint version,
        out IntPtr subcompositor)
    {
        (compositor, version, subcompositor) = (IntPtr.Zero, 0u, IntPtr.Zero);
        var queue = wl_display_create_queue(display);
        if (queue == IntPtr.Zero) return false;

        var bindings = stackalloc Bindings[1];
        var registry = symbols.GetRegistry(display);
        if (registry == IntPtr.Zero)
        {
            wl_event_queue_destroy(queue);
            return false;
        }

        wl_proxy_set_queue(registry, queue);
        var listener = stackalloc IntPtr[2]; // wl_registry_listener: global, global_remove
        listener[0] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, uint, void>)&Global;
        listener[1] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&GlobalRemove;
        if (wl_proxy_add_listener(registry, listener, (IntPtr)bindings) < 0 ||
            wl_display_roundtrip_queue(display, queue) < 0)
        {
            if (bindings->Compositor != IntPtr.Zero) wl_proxy_destroy(bindings->Compositor);
            if (bindings->Subcompositor != IntPtr.Zero) wl_proxy_destroy(bindings->Subcompositor);
            wl_proxy_destroy(registry);
            wl_event_queue_destroy(queue);
            return false;
        }

        wl_proxy_destroy(registry);
        // Proxies made while our queue dispatched inherit it: the globals go back to GLFW's default queue before the
        // private one is destroyed
        compositor = bindings->Compositor;
        version = bindings->CompositorVersion;
        subcompositor = bindings->Subcompositor;
        if (compositor != IntPtr.Zero) wl_proxy_set_queue(compositor, IntPtr.Zero);
        if (subcompositor != IntPtr.Zero) wl_proxy_set_queue(subcompositor, IntPtr.Zero);
        wl_event_queue_destroy(queue);

        if (compositor != IntPtr.Zero && subcompositor != IntPtr.Zero) return true;
        if (compositor != IntPtr.Zero) wl_proxy_destroy(compositor);
        if (subcompositor != IntPtr.Zero) wl_proxy_destroy(subcompositor);
        (compositor, version, subcompositor) = (IntPtr.Zero, 0u, IntPtr.Zero);
        return false;
    }

    // The compositor's globals, bound while the registry's events are dispatched. Unmanaged: libwayland fills it in
    // during the roundtrip on this thread.
    private struct Bindings
    {
        public IntPtr Compositor;
        public uint CompositorVersion;
        public IntPtr Subcompositor;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Global(IntPtr data, IntPtr registry, uint name, IntPtr @interface, uint version)
    {
        if (data == IntPtr.Zero || @interface == IntPtr.Zero || !Symbols.Load(out var symbols)) return;

        var bindings = (Bindings*)data;
        var known = Marshal.PtrToStringUTF8(@interface);
        if (known == "wl_compositor" && bindings->Compositor == IntPtr.Zero)
        {
            var at = Math.Min(version, CompositorMaxVersion);
            bindings->Compositor = symbols.Bind(registry, name, Symbols.Interface.Compositor, at);
            bindings->CompositorVersion = at;
        }
        else if (known == "wl_subcompositor" && bindings->Subcompositor == IntPtr.Zero)
        {
            bindings->Subcompositor = symbols.Bind(registry, name, Symbols.Interface.Subcompositor, 1);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GlobalRemove(IntPtr data, IntPtr registry, uint name)
    {
        // The window's compositor disappears with the display: nothing bound here outlives it
        _ = (data, registry, name);
    }

    public void Dispose()
    {
        if (_surface == IntPtr.Zero) return;

        if (_subsurface != IntPtr.Zero) _ = Symbols.Flags(_subsurface, SubsurfaceDestroy, IntPtr.Zero, 0, MarshalDestroy);
        _ = Symbols.Flags(_surface, SurfaceDestroy, IntPtr.Zero, 0, MarshalDestroy);
        if (_subcompositor != IntPtr.Zero)
        {
            _ = Symbols.Flags(_subcompositor, SubcompositorDestroy, IntPtr.Zero, 0, MarshalDestroy);
        }

        if (_compositor != IntPtr.Zero) wl_proxy_destroy(_compositor);

        _ = wl_display_flush(_display);
    }

    // The libwayland interface descriptions, resolved once. The protocol functions are generated inline in C, so the
    // one exported marshaller carries every request: (proxy, opcode, interface, version, flags, arguments).
    private sealed class Symbols
    {
        public enum Interface
        {
            Compositor,
            Subcompositor
        }

        private static Symbols? _loaded;

        private readonly IntPtr _compositorInterface, _registryInterface, _subcompositorInterface,
            _subsurfaceInterface, _surfaceInterface;

        private Symbols(IntPtr compositor, IntPtr registry, IntPtr surface, IntPtr subcompositor, IntPtr subsurface) =>
            (_compositorInterface, _registryInterface, _surfaceInterface, _subcompositorInterface,
                _subsurfaceInterface) =
            (compositor, registry, surface, subcompositor, subsurface);

        public static bool Load(out Symbols symbols)
        {
            if (_loaded is { } known)
            {
                symbols = known;
                return true;
            }

            symbols = null!;
            if (!NativeLibrary.TryLoad(Lib, out var library)) return false;

            symbols = _loaded = new Symbols(NativeLibrary.GetExport(library, "wl_compositor_interface"),
                NativeLibrary.GetExport(library, "wl_registry_interface"),
                NativeLibrary.GetExport(library, "wl_surface_interface"),
                NativeLibrary.GetExport(library, "wl_subcompositor_interface"),
                NativeLibrary.GetExport(library, "wl_subsurface_interface"));
            return true;
        }

        // nusn: name, interface->name (the struct's first field), version, then the new id's slot
        public IntPtr Bind(IntPtr registry, uint name, Interface @interface, uint version)
        {
            var iface = @interface == Interface.Compositor ? _compositorInterface : _subcompositorInterface;
            return Flags(registry, RegistryBind, iface, version, 0, (IntPtr)name, *(IntPtr*)iface, (IntPtr)version,
                IntPtr.Zero);
        }

        public IntPtr GetRegistry(IntPtr display) =>
            Flags(display, DisplayGetRegistry, _registryInterface, 1, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero);

        public IntPtr CreateSurface(IntPtr compositor, uint version) =>
            Flags(compositor, CompositorCreateSurface, _surfaceInterface, version, 0, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero);

        // noo: the new id's slot comes first in the varargs (libwayland ignores its value but reads it)
        public IntPtr GetSubsurface(IntPtr subcompositor, IntPtr surface, nuint parent) =>
            Flags(subcompositor, SubcompositorGetSubsurface, _subsurfaceInterface, 1, 0, IntPtr.Zero, surface,
                (IntPtr)parent, IntPtr.Zero);

        public static IntPtr SetPosition(IntPtr subsurface, int x, int y) =>
            Flags(subsurface, SubsurfaceSetPosition, IntPtr.Zero, 0, 0, x, y, IntPtr.Zero, IntPtr.Zero);

        public static IntPtr SetDesync(IntPtr subsurface) =>
            Flags(subsurface, SubsurfaceSetDesync, IntPtr.Zero, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero);

        public static IntPtr Commit(IntPtr surface) =>
            Flags(surface, SurfaceCommit, IntPtr.Zero, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        [DllImport(Lib, EntryPoint = "wl_proxy_marshal_flags")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern IntPtr Flags(IntPtr proxy, uint opcode, IntPtr @interface, uint version, uint flags,
            IntPtr a0 = default, IntPtr a1 = default, IntPtr a2 = default, IntPtr a3 = default);
    }

    [DllImport(Lib, EntryPoint = "wl_proxy_add_listener")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int wl_proxy_add_listener(IntPtr proxy, IntPtr* implementation, IntPtr data);

    [DllImport(Lib, EntryPoint = "wl_proxy_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void wl_proxy_destroy(IntPtr proxy);

    [DllImport(Lib, EntryPoint = "wl_proxy_set_queue")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void wl_proxy_set_queue(IntPtr proxy, IntPtr queue);

    [DllImport(Lib, EntryPoint = "wl_display_create_queue")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr wl_display_create_queue(IntPtr display);

    [DllImport(Lib, EntryPoint = "wl_event_queue_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void wl_event_queue_destroy(IntPtr queue);

    [DllImport(Lib, EntryPoint = "wl_display_roundtrip_queue")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int wl_display_roundtrip_queue(IntPtr display, IntPtr queue);

    [DllImport(Lib, EntryPoint = "wl_display_flush")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int wl_display_flush(IntPtr display);
}

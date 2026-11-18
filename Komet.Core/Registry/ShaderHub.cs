using Komet.Api;

namespace Komet.Core;

// Shaders of other mods (KapiShaders) and Komet's shader packs: defines, uniforms and source edits for the game's own
// programs, and fullscreen passes. The calls land in the shader module (IShaderBackend); without it every
// registration is refused. Registrations last until disposed or until the world closes. Main thread only.
internal static class ShaderHub
{
    // How many defines, uniforms, edits and passes Komet takes from all mods together; more are refused and logged
    public const int MaxDefines = 128, MaxUniforms = 256, MaxEdits = 64, MaxPasses = 32;

    private const string RendererChangedName = "komet.Shaders.RendererChanged";

    // The renderer drawing the world now; RendererChanged tells when it switches (the options menu, a fault)
    public static KapiRenderer Renderer => VulkanDraws ? KapiRenderer.Vulkan : KapiRenderer.OpenGL;

    // The registered services (Modules.Bind): the shader module behind the calls below, and the Vulkan module's answer
    // who draws (OpenGL without it)
    internal static bool VulkanDraws => Services.Find<IRenderPath>()?.VulkanDraws ?? false;

    // Vulkan is asked for, drawing or not yet: programs linked now are ported, so they need their source
    internal static bool VulkanWanted => Services.Find<IRenderPath>()?.VulkanWanted ?? false;
    private static IShaderBackend? Backend => Services.Find<IShaderBackend>();

    // Komet can accept shader registrations in this client
    public static bool Available => Backend is not null;

    // The renderer switched; Komet reloads the game's shaders and rebuilds the passes itself
    public static event EventHandler? RendererChanged;

    // #define name value after the programs' #version line, value read at each load; none: every program and pass
    public static IDisposable Define(string modId, string name, Func<string> value, params string[] programs) =>
        Backend?.Define(modId, name, value, programs) ?? Nothing.Instance;

    // A uniform set on every program and pass that declares it, 1 to 4 numbers asked once per frame
    public static IDisposable Uniform(string modId, string name, Func<float[]> value) =>
        Backend?.Uniform(modId, name, value) ?? Nothing.Instance;

    // A float uniform
    public static IDisposable Uniform(string modId, string name, Func<float> value)
    {
        if (!NotNull(value) || !NotNull(name)) return Nothing.Instance;
        // One array per registration, not per call: it is asked once per frame, and the upload reads it at once
        var one = new float[1];
        return Backend?.Uniform(modId, name, () =>
        {
            one[0] = value();
            return one;
        }) ?? Nothing.Instance;
    }

    // Rewrites a program's source as it loads; program * for every program
    public static IDisposable Edit(string modId, string program, System.Func<string, string>? vertex,
        System.Func<string, string>? fragment) => Backend?.Edit(modId, program, vertex, fragment) ?? Nothing.Instance;

    // Builds the game's shaders anew with the current defines and edits; several calls in one frame reload once
    public static void Reload() => Backend?.RequestReload();

    // A fullscreen pass of the mod's own, drawn each frame while its Enabled says so; the same mod and id replace it
    public static IDisposable AddPass(string modId, KapiPass pass) =>
        Backend?.AddPass(modId, pass) ?? Nothing.Instance;

    // Compiles and registers the passes together on the render thread, or none of them
    public static PassResult TryAddEffect(string modId, params KapiPass[] passes) =>
        Backend?.TryAddEffect(modId, passes) ?? new PassResult(null, "Komet shader backend unavailable");

    // Rewrites the fragment of a registered pass before it compiles, for passes registered later too
    public static IDisposable EditPass(string modId, string targetModId, string passId,
        System.Func<string, string> fragment) =>
        Backend?.EditPass(modId, targetModId, passId, fragment) ?? Nothing.Instance;

    internal static void RaiseRendererChanged() =>
        ModEvents.Raise(RendererChanged, h => h(null, EventArgs.Empty), h => RendererChanged -= h, RendererChangedName);

    internal static void Clear() => RendererChanged = null;
}

// The registration on success or a diagnostic message on failure
internal readonly record struct PassResult(IDisposable? Registration, string? Error);

// Who draws the world: the Vulkan module's answer, registered by the composition root (Modules.Bind)
internal interface IRenderPath
{
    bool VulkanDraws { get; }
    bool VulkanWanted { get; }
}

// What the shader module does for ShaderHub; the composition root (Modules.Bind) registers the module's implementation
internal interface IShaderBackend
{
    IDisposable Define(string modId, string name, Func<string> value, string[] programs);
    IDisposable Uniform(string modId, string name, Func<float[]> value);

    IDisposable Edit(string modId, string program, System.Func<string, string>? vertex,
        System.Func<string, string>? fragment);

    void RequestReload();
    IDisposable AddPass(string modId, KapiPass pass);
    PassResult TryAddEffect(string modId, KapiPass[] passes);
    IDisposable EditPass(string modId, string targetModId, string passId, System.Func<string, string> fragment);
}

// A registration that was refused or made before the shader module was registered: disposing it does nothing
internal sealed class Nothing : IDisposable
{
    public static readonly Nothing Instance = new();

    public void Dispose()
    {
    }
}

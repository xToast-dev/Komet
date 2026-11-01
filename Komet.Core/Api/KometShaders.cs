
namespace Komet.Api;

/// <summary>Which renderer draws the world.</summary>
public enum KometRenderer
{
    /// <summary>The game's own OpenGL renderer.</summary>
    OpenGL,

    /// <summary>Komet's Vulkan-only renderer (Komet › Overview › Renderer).</summary>
    Vulkan
}

/// <summary>Where a <see cref="KometPass"/> draws.</summary>
public enum KometPassStage
{
    /// <summary>
    /// After the game's final composition, before the GUI (<c>EnumRenderStage.AfterFinalComposition</c>): the finished
    /// picture, tone mapped, with bloom, god rays and vignettes, in the primary framebuffer; its depth is the scene's.
    /// </summary>
    Final
}

/// <summary>
/// Shaders for other mods: defines, uniforms and source edits for the game's own shader programs, and fullscreen
/// passes of a mod's own.
/// </summary>
/// <remarks>
/// <para>Defines, uniforms and edits reach the game's programs as they load. Passes are fullscreen fragment shaders
/// drawn at a <see cref="KometPassStage"/>.</para>
/// <para>Everything runs on whichever renderer draws. Under Vulkan, Komet ports every program to SPIR-V itself and
/// draws it in Vulkan's frame; the mod does nothing extra. Every program, pass included, has <c>#define KOMET 1</c>,
/// and <c>#define KOMET_VULKAN 1</c> while Vulkan draws.</para>
/// <para>Registrations last until disposed or until the world closes. Main thread only. Call from a class of your own
/// only when <c>api.ModLoader.IsModEnabled("komet")</c>.</para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// var warmth = KometShaders.Uniform("mymod", "mymodWarmth", () =&gt; config.Warmth);
/// var pass = KometShaders.AddPass(new KometPass("mymod", "grade", KometPassStage.Final, source)
/// {
///     Enabled = () =&gt; config.Grade,
///     Uniforms = { ["mymodStrength"] = () =&gt; [config.Strength] }
/// });
/// // at world close Komet drops both; dispose them earlier to take them away
/// </code>
/// </example>
public static class KometShaders
{
    /// <summary>How many defines, uniforms, edits and passes Komet takes from all mods together; more are refused and logged.</summary>
    public const int MaxDefines = 128, MaxUniforms = 256, MaxEdits = 64, MaxPasses = 32;

    /// <summary>The renderer drawing the world now. <see cref="RendererChanged"/> tells when it switches (the options menu, a fault).</summary>
    public static KometRenderer Renderer => VulkanDraws() ? KometRenderer.Vulkan : KometRenderer.OpenGL;

    // Set by Modules.Wire: the shader module behind the calls below, and the Vulkan module's answer who draws
    internal static IShaderBackend? Backend { get; set; }
    internal static Func<bool> VulkanDraws { get; set; } = static () => false;

    /// <summary>The renderer switched. Komet reloads the game's shaders and rebuilds the passes itself, so <c>KOMET_VULKAN</c> is right again.</summary>
    public static event EventHandler? RendererChanged;

    /// <summary>
    /// Adds <c>#define name value</c> to programs, after their <c>#version</c> line. <paramref name="value"/> is read at
    /// each load: after changing it, call <see cref="Reload"/>.
    /// </summary>
    /// <param name="modId">The registering mod.</param>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">The define's value, read at each load.</param>
    /// <param name="programs">
    /// The game's program names (<c>chunkopaque</c>, <c>final</c>, ...) or, for a pass of the same mod,
    /// <c>pass:id</c>; none gives it to every program and every pass of the mod.
    /// </param>
    /// <returns>The registration; dispose it to take the define away (after the next reload).</returns>
    public static IDisposable Define(string modId, string name, Func<string> value, params string[] programs) =>
        Backend?.Define(modId, name, value, programs) ?? Nothing.Instance;

    /// <summary>
    /// A uniform Komet sets on every program that declares it, each time the program is used, and on the passes that
    /// declare it, before each draw. Live: no reload needed.
    /// </summary>
    /// <param name="modId">The registering mod.</param>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">1 to 4 numbers: <c>float</c>, <c>vec2</c>, <c>vec3</c> or <c>vec4</c> by the length. Asked once per frame.</param>
    /// <returns>The registration; dispose it to stop setting the uniform.</returns>
    public static IDisposable Uniform(string modId, string name, Func<float[]> value) =>
        Backend?.Uniform(modId, name, value) ?? Nothing.Instance;

    /// <summary>A <c>float</c> uniform; see <see cref="Uniform(string, string, Func{float[]})"/>.</summary>
    /// <param name="modId">The registering mod.</param>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">The value, asked once per frame.</param>
    /// <returns>The registration; dispose it to stop setting the uniform.</returns>
    public static IDisposable Uniform(string modId, string name, Func<float> value)
    {
        if (!NotNull(value) || !NotNull(name)) return Nothing.Instance;
        // One array per registration, not per call: it is asked at each use of a program that declares the uniform, and
        // the upload reads it at once
        var one = new float[1];
        return Backend?.Uniform(modId, name, () =>
        {
            one[0] = value();
            return one;
        }) ?? Nothing.Instance;
    }

    /// <summary>
    /// Rewrites a program's source as it loads. Komet's own edits come first, so match lines loosely.
    /// </summary>
    /// <param name="modId">The registering mod.</param>
    /// <param name="program">The game's program name, or <c>*</c> for every program.</param>
    /// <param name="vertex">Takes the vertex stage's source and gives the new one; null leaves it alone.</param>
    /// <param name="fragment">Takes the fragment stage's source and gives the new one; null leaves it alone.</param>
    /// <returns>The registration; dispose it to take the edit away (after the next reload).</returns>
    public static IDisposable Edit(string modId, string program, System.Func<string, string>? vertex, System.Func<string, string>? fragment) =>
        Backend?.Edit(modId, program, vertex, fragment) ?? Nothing.Instance;

    /// <summary>Builds the game's shaders anew with the current defines and edits. Several calls in one frame reload once.</summary>
    public static void Reload() => Backend?.RequestReload();

    private const string RendererChangedName = "KometShaders.RendererChanged";

    /// <summary>
    /// Adds a fullscreen fragment shader of the mod's own, drawn at its stage each frame while
    /// <see cref="KometPass.Enabled"/> says so. The same mod and id replace the pass added before.
    /// </summary>
    /// <param name="pass">The pass.</param>
    /// <returns>The registration; dispose it to remove the pass.</returns>
    public static IDisposable AddPass(KometPass pass) => Backend?.AddPass(pass) ?? Nothing.Instance;

    internal static void RaiseRendererChanged() =>
        ApiEvents.Raise(RendererChanged, h => h(null, EventArgs.Empty), h => RendererChanged -= h, RendererChangedName);

    internal static void Clear() => RendererChanged = null;
}

/// <summary>
/// A fullscreen pass of a mod's own, for <see cref="KometShaders.AddPass"/>.
/// </summary>
/// <remarks>
/// <see cref="Fragment"/> is GLSL 330 that defines <c>main()</c> and writes <c>color</c>. Komet puts this prelude
/// before it; uniforms of <see cref="Uniforms"/> and of <see cref="KometShaders.Uniform(string, string, Func{float[]})"/>
/// are set by name before each draw:
/// <code>
/// #version 330 core
/// #define KOMET 1
/// #define KOMET_VULKAN 1          // only while Vulkan draws
/// in vec2 uv;                 // 0..1, (0, 0) bottom left
/// out vec4 color;
/// uniform sampler2D scene;    // the picture so far (a copy: read any pixel)
/// uniform sampler2D depth;    // the scene's depth, 0..1
/// uniform vec2 resolution;    // in pixels
/// uniform float time;         // seconds, for animation
/// </code>
/// Passes of one stage draw in <see cref="Order"/>, lower first. Line numbers in compile errors are the mod's.
/// </remarks>
/// <param name="modId">The mod the pass belongs to.</param>
/// <param name="id">The pass's name, unique within the mod.</param>
/// <param name="stage">Where it draws.</param>
/// <param name="fragment">The GLSL body after the prelude.</param>
public sealed class KometPass(string modId, string id, KometPassStage stage, string fragment)
{
    /// <summary>The mod the pass belongs to.</summary>
    public string ModId { get; } = NotNull(modId) ? modId : "";
    /// <summary>The pass's name, unique within the mod.</summary>
    public string Id { get; } = NotNull(id) ? id : "";
    /// <summary>Where it draws.</summary>
    public KometPassStage Stage { get; } = stage;
    /// <summary>The GLSL body after the prelude: <c>main()</c> and whatever it needs.</summary>
    public string Fragment { get; } = NotNull(fragment) ? fragment : "";
    /// <summary>Passes of one stage draw lower first.</summary>
    public int Order { get; init; }
    /// <summary>Asked each frame; null draws always. A callback that throws turns the pass off.</summary>
    public Func<bool>? Enabled { get; init; }
    /// <summary>Uniforms of this pass by name, set before each draw (1 to 4 numbers each).</summary>
    public Dictionary<string, Func<float[]>> Uniforms { get; } = [];
}

// What the shader module does for KometShaders; Modules.Wire hands the module's implementation in
internal interface IShaderBackend
{
    IDisposable Define(string modId, string name, Func<string> value, string[] programs);
    IDisposable Uniform(string modId, string name, Func<float[]> value);
    IDisposable Edit(string modId, string program, System.Func<string, string>? vertex, System.Func<string, string>? fragment);
    void RequestReload();
    IDisposable AddPass(KometPass pass);
}

// A registration that was refused or made before the shader module was wired: disposing it does nothing
internal sealed class Nothing : IDisposable
{
    public static readonly Nothing Instance = new();

    public void Dispose()
    {
    }
}

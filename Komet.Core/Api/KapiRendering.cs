using System.Diagnostics;
using Komet.Core;

namespace Komet.Api;

/// <summary>Where in Komet's frame a <see cref="KapiRenderStep" /> runs, in the order they come.</summary>
public enum KapiRenderStage
{
    /// <summary>Before anything of the frame is drawn.</summary>
    Before,

    /// <summary>With the opaque world: terrain, entities, blocks with a renderer.</summary>
    Opaque,

    /// <summary>With the transparent world (water, glass), order-independent transparency.</summary>
    Transparent,

    /// <summary>After the transparent world, before post-processing.</summary>
    AfterTransparent,

    /// <summary>Into the far shadow map.</summary>
    ShadowFar,

    /// <summary>Into the near shadow map.</summary>
    ShadowNear,

    /// <summary>After post-processing (bloom, god rays), on the scene in linear colour.</summary>
    AfterPostProcessing,

    /// <summary>After the scene was copied to the screen's framebuffer.</summary>
    AfterBlit,

    /// <summary>The 2D GUI, in screen coordinates.</summary>
    Gui,

    /// <summary>After the final composition: the finished picture, before the GUI.</summary>
    AfterFinal,

    /// <summary>The frame is done.</summary>
    Done
}

/// <summary>
///     A render step of the mod's own, for <see cref="KapiRendering.AddStep" />: Komet calls it each frame at its
///     <see cref="Stage" />, on OpenGL and under Vulkan alike, times it, and shows it on its Connected mods page.
/// </summary>
/// <remarks>
///     The step draws with the game's render API as an <c>IRenderer</c> of the same stage would. A step that throws is
///     switched off for the world and reported; the player can switch a mod's steps off on the Connected mods page.
/// </remarks>
/// <param name="id">The step's name, unique within the mod.</param>
/// <param name="stage">Where it runs.</param>
/// <param name="render">Draws; gets the frame's time in seconds. Main thread.</param>
public sealed class KapiRenderStep(string id, KapiRenderStage stage, Action<float> render)
{
    /// <summary>The step's name, unique within the mod.</summary>
    public string Id { get; } = NotNull(id) ? id : "";

    /// <summary>Where it runs.</summary>
    public KapiRenderStage Stage { get; } = stage;

    /// <summary>Draws; gets the frame's time in seconds.</summary>
    public Action<float> Render { get; } = NotNull(render) ? render : static _ => { };

    /// <summary>Within its stage, steps and the game's renderers run lower first; 0.5 by default.</summary>
    public double Order { get; init; } = 0.5;

    /// <summary>The game's render range for the step; 0 by default.</summary>
    public int Range { get; init; }

    /// <summary>Asked each frame before the step runs; null runs always.</summary>
    public Func<bool>? Enabled { get; init; }
}

/// <summary>
///     The mod's render steps, through <see cref="KapiMod.Rendering" />: code of its own at named places of Komet's
///     frame, known to Komet, timed, and switchable by the player.
/// </summary>
/// <example>
///     <code language="csharp">
/// var outline = komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, DrawOutline)
/// {
///     Enabled = () =&gt; config.Outline
/// });
/// </code>
/// </example>
public sealed class KapiRendering
{
    /// <summary>How many render steps all mods together may add.</summary>
    public const int MaxSteps = 64;

    private readonly KapiMod _mod;

    // The mod's step registrations by id: one replaced goes as a registration too, so the mod's counts drop it
    private readonly Dictionary<string, KapiRegistration> _steps = new(StringComparer.Ordinal);

    internal KapiRendering(KapiMod mod) => _mod = mod;

    /// <summary>The renderer drawing the world now.</summary>
    public static KapiRenderer Renderer => ShaderHub.Renderer;

    /// <summary>
    ///     Adds a render step. The same id replaces the mod's step added before. It runs from the next frame until it
    ///     is disposed, the connection ends or the world closes.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The registration; refused for an empty id or when the table is full.</returns>
    public KapiRegistration AddStep(KapiRenderStep step)
    {
        if (!NotNull(step) || step.Id.Length == 0) return _mod.Keep(Nothing.Instance, "a step with an id is needed");

        if (_steps.Remove(step.Id, out var old)) old.Dispose();

        var made = RenderSteps.Add(_mod.ModId, step) is { } entry
            ? _mod.Keep(entry, kind: KapiKind.Step)
            : _mod.Keep(Nothing.Instance, $"more than {MaxSteps} render steps");
        if (made.Active) _steps[step.Id] = made;
        return made;
    }
}

// Every mod's render steps; the rendering module (IRenderStepHost) runs them in the game's frame
internal static class RenderSteps
{
    private static readonly List<StepEntry> Steps = [];

    static RenderSteps() => WorldEnd.On(nameof(RenderSteps), Clear);

    internal static IReadOnlyList<StepEntry> All => [.. Steps.Bounded(KapiRendering.MaxSteps)];

    private static IRenderStepHost? Host => Services.Find<IRenderStepHost>();

    internal static StepEntry? Add(string modId, KapiRenderStep step)
    {
        var old = Steps.FindIndex(s => s.ModId == modId && s.Step.Id == step.Id);
        if (old >= 0) Steps[old].Dispose();
        if (Steps.Count >= KapiRendering.MaxSteps) return null;

        var entry = new StepEntry(modId, step);
        Steps.Add(entry);
        Host?.Add(entry);
        return entry;
    }

    internal static void Removed(StepEntry entry)
    {
        _ = Steps.Remove(entry);
        Host?.Remove(entry);
    }

    // The rendering module started: the steps added before run from now on
    internal static void Attach(IRenderStepHost host)
    {
        foreach (var entry in Steps.Bounded(KapiRendering.MaxSteps)) host.Add(entry);
    }

    private static void Clear()
    {
        foreach (var entry in (StepEntry[])[.. Steps.Bounded(KapiRendering.MaxSteps)]) entry.Dispose();
        Steps.Clear();
    }
}

// One mod's step: runs it guarded and timed, and keeps what the Connected mods page shows
internal sealed class StepEntry(string modId, KapiRenderStep step) : IDisposable
{
    private const int MaxError = 300;

    private bool _gone;

    public string ModId { get; } = modId;
    public KapiRenderStep Step { get; } = step;
    public long Calls { get; private set; }
    public long Ticks { get; private set; }
    public string? Failure { get; private set; }

    public double AverageMs => Calls > 0 ? Ticks * 1000.0 / Stopwatch.Frequency / Calls : 0;

    // The game's stage number for EnumRenderStage
    public int Stage => Step.Stage switch
    {
        KapiRenderStage.Before => 0,
        KapiRenderStage.Opaque => 1,
        KapiRenderStage.Transparent => 2,
        KapiRenderStage.AfterTransparent => 3,
        KapiRenderStage.ShadowFar => 4,
        KapiRenderStage.ShadowNear => 5,
        KapiRenderStage.AfterPostProcessing => 6,
        KapiRenderStage.AfterBlit => 7,
        KapiRenderStage.Gui => 8,
        KapiRenderStage.AfterFinal => 9,
        _ => 10
    };

    // Called by the host each frame at the step's stage
    public void Run(float seconds)
    {
        if (_gone || Failure is not null || Kapi.IsSilenced(ModId)) return;

        try
        {
            if (Step.Enabled is { } enabled && !enabled()) return;

            var start = Stopwatch.GetTimestamp();
            Step.Render(seconds);
            var ticks = Stopwatch.GetTimestamp() - start;
            (Calls, Ticks) = (Calls + 1, Ticks + ticks);
            if (DebugTimings.Capturing) DebugTimings.Book(ModId + ":" + Step.Id, ticks);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var text = e.GetType().Name + ": " + e.Message;
            Failure = text.Length > MaxError ? text[..MaxError] : text;
            ModEvents.Logger?.Error("Komet: render step {0}:{1} threw and is off for this world: {2}", ModId, Step.Id,
                e);
        }
    }

    public void Dispose()
    {
        if (_gone) return;

        _gone = true;
        RenderSteps.Removed(this);
    }
}

// The rendering module's side: it registers each step with the game's renderer (Modules.Bind)
internal interface IRenderStepHost
{
    void Add(StepEntry entry);
    void Remove(StepEntry entry);
}

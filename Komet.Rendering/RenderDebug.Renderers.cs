using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

internal static partial class RenderDebug
{
    private const int MaxRendererRows = 4096;

    internal static RendererInfo[] Renderers(ICoreClientAPI api)
    {
        if (api.World is not ClientMain game || game.eventManager?.renderersByStage is not { } stages) return [];

        var owners = (api.ModLoader?.Systems ?? []).Where(s => s?.Mod?.Info is not null)
            .Take(4096).GroupBy(s => s.GetType().Assembly)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Mod.Info.ModID).Distinct().ToArray());
        List<RendererInfo> result = [];
        for (var stage = 0; stage < Math.Min(stages.Length, 64) && result.Count < MaxRendererRows; stage++)
        {
            var handlers = stages[stage];
            // This is a diagnostic sample. Large stages are valid in the engine.
            for (var index = 0;
                 index < MaxRendererRows && index < handlers.Count && result.Count < MaxRendererRows;
                 index++)
            {
                var handler = handlers[index];
                if (handler.Renderer is not { } renderer) continue;

                var type = renderer.GetType();
                result.Add(new RendererInfo((EnumRenderStage)stage, handler.ProfilingName ?? "",
                    type.FullName ?? type.Name,
                    type.Assembly.GetName().Name ?? "", owners.GetValueOrDefault(type.Assembly) ?? []));
            }
        }

        return [.. result];
    }

    internal sealed record RendererInfo(
        EnumRenderStage Stage,
        string Name,
        string Type,
        string Assembly,
        string[] Mods);
}

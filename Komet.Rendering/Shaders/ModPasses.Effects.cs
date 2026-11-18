using System.Diagnostics.CodeAnalysis;
using Komet.Core;
using Komet.Api;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Rendering;

internal sealed partial class ModPasses
{
    [SuppressMessage("SonarAnalyzer.CSharp", "S6640", Justification = "GLFW returns a native context pointer.")]
    [SuppressMessage("Reliability", "CA2000", Justification = "The result transfers ownership to the caller.")]
    internal static unsafe PassResult AddEffect(string modId, KapiPass?[] passes)
    {
        if (passes is not { Length: > 0 and <= ShaderHub.MaxPasses })
        {
            return new PassResult(null, "Expected one or more fullscreen passes within the pass limit");
        }

        if (_renderer is null || GLFW.GetCurrentContext() == null)
        {
            return new PassResult(null, "No active fullscreen renderer or current graphics context");
        }

        if (Passes.Count + passes.Length > ShaderHub.MaxPasses)
        {
            return new PassResult(null, "Fullscreen pass limit reached");
        }

        List<Entry> staged = [];
        HashSet<(string Mod, string Id)> keys = [];
        var committed = false;
        try
        {
            foreach (var pass in passes.Bounded(ShaderHub.MaxPasses))
            {
                if (pass is null || !NotNull(modId) || modId.Length == 0 || pass.Id.Length == 0 ||
                    pass.Stage != KapiStage.Final || pass.Fragment.Length is 0 or > MaxSource)
                {
                    return new PassResult(null, "Invalid fullscreen pass");
                }

                if (!keys.Add((modId, pass.Id)) || Passes.Any(e => e.ModId == modId && e.Pass.Id == pass.Id))
                {
                    return new PassResult(null, $"Duplicate pass {modId}:{pass.Id}");
                }

                var entry = new Entry(modId, pass) { Effect = staged };
                staged.Add(entry);
                if (!Built(entry)) return new PassResult(null, $"{modId}:{pass.Id}: {entry.FullError}");
            }

            Passes.AddRange(staged);
            Passes.Sort((a, b) => a.Pass.Order.CompareTo(b.Pass.Order));
            committed = true;
            ModShaders.Touch();
            return new PassResult(new Registration(() => RemoveEffect(staged)), null);
        }
        finally
        {
            if (!committed) RemoveEffect(staged);
        }
    }

    private static void RebuildEffect(List<Entry> effect)
    {
        List<Entry> candidates = [];
        try
        {
            foreach (var entry in effect.Bounded(ShaderHub.MaxPasses))
            {
                var candidate = new Entry(entry.ModId, entry.Pass);
                candidates.Add(candidate);
                if (Built(candidate)) continue;

                entry.Why = candidate.Why;
                return;
            }

            for (var i = 0; i < Math.Min(effect.Count, ShaderHub.MaxPasses); i++)
            {
                var (entry, candidate) = (effect[i], candidates[i]);
                Drop(entry);
                (entry.Program, entry.Locations) = (candidate.Program, candidate.Locations);
                candidate.Program = 0;
                (entry.Failed, entry.Why) = (false, "");
                entry.OwnAt.Clear();
            }
        }
        finally
        {
            foreach (var candidate in candidates.Bounded(ShaderHub.MaxPasses))
            {
                Drop(candidate);
            }
        }
    }

    private static void RemoveEffect(List<Entry> entries)
    {
        foreach (var entry in entries.Bounded(ShaderHub.MaxPasses))
        {
            _ = Passes.Remove(entry);
            Drop(entry);
        }

        ModShaders.Touch();
    }
}

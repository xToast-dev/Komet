using System.Reflection;
using HarmonyLib;
using Komet.Api;

namespace Komet.Core;

// Evidence captured by guards, not inferred from a mod's name or the presence of a patch alone.
internal static class CompatibilityLog
{
    private const int MaxEntries = FeatureTable.MaxFeatures * 4, MaxText = 32768, MaxLinks = 64;
    private static readonly Dictionary<(string Feature, string Kind), string> Entries = [];
    private static readonly Lock Gate = new();

    internal static void Record(string feature, string kind, string text)
    {
        var key = (feature, kind);
        lock (Gate)
        {
            if (Entries.Count < MaxEntries || Entries.ContainsKey(key))
            {
                Entries[key] = text[..Math.Min(text.Length, MaxText)];
            }
        }
    }

    internal static void Remove(string feature, string kind)
    {
        lock (Gate)
        {
            _ = Entries.Remove((feature, kind));
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }

    internal static string Details(string feature)
    {
        lock (Gate)
        {
            return string.Join("\n\n",
                Entries.Where(e =>
                        e.Key.Feature == feature || e.Key.Feature.StartsWith(feature + " ", StringComparison.Ordinal))
                    .Select(e => e.Key.Feature + " [" + e.Key.Kind + "]:\n" + e.Value));
        }
    }

    internal static bool HasDetails(string feature)
    {
        lock (Gate)
        {
            return Entries.Keys.Any(key => key.Feature == feature ||
                                           key.Feature.StartsWith(feature + " ", StringComparison.Ordinal));
        }
    }

    internal static void Patches(string feature, ReadOnlySpan<MethodBase?> methods, KapiPatchKinds kinds, string? owner,
        ReadOnlySpan<Type> own)
    {
        try
        {
            CapturePatches(feature, methods, kinds, owner, own);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Record(feature, "patches", "Could not describe this guard's patches: " + e);
        }
    }

    internal static void Fingerprint(string feature, ulong expected, ulong found, ReadOnlySpan<MethodBase?> methods)
    {
        var text = $"Expected {expected:X16}; found {found:X16}\n";
        try
        {
            text += string.Join("\n",
                methods.ToArray().Select(m => m is null ? "Missing method" : EngineShape.Describe(m)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            text += "Method details could not be resolved: " + e;
        }

        Record(feature, "engine", text);
    }

    private static void CapturePatches(string feature, ReadOnlySpan<MethodBase?> methods, KapiPatchKinds kinds,
        string? owner,
        ReadOnlySpan<Type> own)
    {
        var lines = new List<string>();
        if (methods.Length == 0) lines.Add("No method details were provided by this guard. See client-main.log.");

        if (methods.Length > EngineShape.MaxSeams) lines.Add("The watched-method limit was exceeded.");

        foreach (var method in methods.Bounded(EngineShape.MaxSeams))
        {
            if (method is null) continue;

            var target = EngineShape.Describe(method);
            if (Harmony.GetPatchInfo(method) is not { } patches) continue;

            foreach (var (list, kind) in new[]
                     {
                         (patches.Prefixes, KapiPatchKinds.Prefix), (patches.Postfixes, KapiPatchKinds.Postfix),
                         (patches.Transpilers, KapiPatchKinds.Transpiler), (patches.Finalizers, KapiPatchKinds.Finalizer),
                         (patches.InnerPrefixes, KapiPatchKinds.InnerPrefix),
                         (patches.InnerPostfixes, KapiPatchKinds.InnerPostfix)
                     }.Bounded(6))
            {
                foreach (var patch in list.Bounded(MaxLinks))
                {
                    if ((kinds & kind) != 0 && !EngineShape.Own(patch, owner, own) && lines.Count < MaxLinks)
                    {
                        lines.Add(
                        $"{target}\n  {kind}: Harmony ID {patch.owner}\n  Patch: {EngineShape.Describe(patch.PatchMethod)}");
                    }
                }
            }
        }

        if (lines.Count == 0) lines.Add("See client-main.log for the guard that disabled this path.");

        Record(feature, "patches", string.Join("\n", lines));
    }
}

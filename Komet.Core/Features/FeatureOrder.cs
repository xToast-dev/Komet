namespace Komet.Core;

// Komet's install order from what the features declare (FeaturePlace): a feature goes after those it Requires and After,
// before those Before names. Of the features free to go next, the lowest Order goes first, then the lower id
// (ordinal): the same lists give the same order, and Order alone decides wherever nothing is declared, so it keeps the
// knobs' order in komet.json and on the settings pages. An unknown id, a duplicate id or Order, or a cycle throws: Komet
// does not start with a table it cannot order. Iterative (Kahn): one pass a placed feature, each a scan for the next.
internal static class FeatureOrder
{
    private const int MaxModules = 16, MaxLinks = 8;

    public static Feature[] Sort(Feature[][] modules)
    {
        var features = Collected(modules);
        var after = Edges(features);
        var waiting = new int[features.Length];
        for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
        {
            waiting[i] = after[i].Count;
        }

        var (placed, order) = (new bool[features.Length], new Feature[features.Length]);
        for (var n = 0; n < Math.Min(features.Length, FeatureTable.MaxBuiltIn); n++)
        {
            var next = Next(features, waiting, placed);
            if (next < 0)
            {
                throw new InvalidOperationException("the features' order has a cycle among: " + string.Join(", ",
                    features.Where((_, i) => !placed[i]).Select(static f => f.Id)));
            }

            (placed[next], order[n]) = (true, features[next]);
            for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
            {
                if (after[i].Contains(next)) waiting[i]--;
            }
        }

        return order;
    }

    // Every module's features, each id and each Order once
    private static Feature[] Collected(Feature[][] modules)
    {
        if (modules.Length > MaxModules) throw new InvalidOperationException("too many feature modules");

        var all = new List<Feature>(FeatureTable.MaxBuiltIn);
        foreach (var module in modules.Bounded(MaxModules))
        {
            if (all.Count + module.Length > FeatureTable.MaxBuiltIn)
            {
                throw new InvalidOperationException("the modules list more than " + FeatureTable.MaxBuiltIn +
                                                    " features");
            }

            all.AddRange(module);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (var feature in all.Bounded(FeatureTable.MaxBuiltIn))
        {
            if (!ids.Add(feature.Id)) throw new InvalidOperationException("feature listed twice: " + feature.Id);

            if (!orders.Add(feature.Order))
            {
                throw new InvalidOperationException("feature Order taken twice: " + feature.Id + " " + feature.Order);
            }
        }

        return [.. all];
    }

    // For each feature, the features that must install before it
    private static HashSet<int>[] Edges(Feature[] features)
    {
        var index = new Dictionary<string, int>(features.Length, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
        {
            index[features[i].Id] = i;
        }

        var after = new HashSet<int>[features.Length];
        for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
        {
            after[i] = [];
        }

        for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
        {
            var place = features[i].Place;
            foreach (var id in (string[])[.. place.Requires.Bounded(MaxLinks), .. place.After.Bounded(MaxLinks)])
            {
                _ = after[i].Add(Known(index, features[i], id));
            }

            foreach (var id in place.Before.Bounded(MaxLinks))
            {
                _ = after[Known(index, features[i], id)].Add(i);
            }
        }

        return after;
    }

    private static int Known(Dictionary<string, int> index, Feature feature, string id) =>
        index.TryGetValue(id, out var at) && at != index[feature.Id]
            ? at
            : throw new InvalidOperationException(feature.Id + " names an unknown feature in its order: " + id);

    // The free feature (all before it placed) with the lowest Order, then id; -1 when none is free
    private static int Next(Feature[] features, int[] waiting, bool[] placed)
    {
        var best = -1;
        for (var i = 0; i < Math.Min(features.Length, FeatureTable.MaxBuiltIn); i++)
        {
            if (placed[i] || waiting[i] > 0) continue;

            if (best < 0 || Earlier(features[i], features[best])) best = i;
        }

        return best;
    }

    private static bool Earlier(Feature a, Feature b) =>
        a.Order != b.Order ? a.Order < b.Order : string.CompareOrdinal(a.Id, b.Id) < 0;
}

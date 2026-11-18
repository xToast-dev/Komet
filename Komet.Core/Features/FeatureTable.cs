namespace Komet.Core;

// The features by index: Komet's in install order (FeatureOrder, set once by the mod's composition root), then other mods'
// in registration order (ExternalFeatures, per world). Every per-feature array of the registry is indexed alike.
internal static class FeatureTable
{
    public const int MaxBuiltIn = 160, MaxExternal = 32, MaxFeatures = MaxBuiltIn + MaxExternal;

    private static readonly List<Feature> Added = [];
    private static Feature[] _builtIn = [];

    public static int BuiltInCount => _builtIn.Length;
    public static int Count => _builtIn.Length + Added.Count;

    // The modules' lists, in any order: the features' declarations order them. Called again with the same lists, it keeps
    // the table (tests register more than once); with other lists it throws.
    public static void Use(Feature[][] modules)
    {
        if (!NotNull(modules)) return;

        var table = FeatureOrder.Sort(modules);
        if (_builtIn.AsSpan().SequenceEqual(table)) return;

        if (_builtIn.Length > 0)
        {
            throw new InvalidOperationException("the feature registry is already initialized with different features");
        }

        _builtIn = table;
        Knobs.Collect(_builtIn);
    }

    // An index outside the table answers with the first feature: callers check Index first
    public static Feature At(int feature)
    {
        if (!Index(feature, Count)) return _builtIn[0];

        return feature < _builtIn.Length ? _builtIn[feature] : Added[feature - _builtIn.Length];
    }

    // Feature id or knob key (UploadCap: ChunkBudget); -1 for neither
    public static int Find(string id)
    {
        if (!NotNull(id)) return -1;

        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            if (string.Equals(At(i).Id, id, StringComparison.Ordinal)) return i;
        }

        var knob = Knobs.Find(id);
        return knob >= 0 ? Knobs.At(knob).Owner : -1;
    }

    // Another mod's feature takes the next index
    internal static int Add(Feature feature)
    {
        if (!NotNull(feature) || !Assert(Count < MaxFeatures)) return -1;

        Added.Add(feature);
        return Count - 1;
    }

    // The other mods' features go with their world
    internal static void Truncate() => Added.Clear();
}

using System.Reflection;

namespace Komet.Core;

// Feature.Replaces: another mod's replacing patch on one of a feature's methods stands it down (a hold on its knobs),
// that mod's going lets it back. Asked at install and at every recheck. Komet's own patches, whatever Harmony id
// installed them (a test rig's own), never count as rivals.
internal static class FeatureGuards
{
    private static readonly bool[] Rivals = new bool[FeatureTable.MaxFeatures];
    private static string? _owner;

    public static bool Rivalled(int feature) => Index(feature, FeatureTable.MaxFeatures) && Rivals[feature];

    public static void Own(string harmonyId) => _owner = harmonyId;

    public static void Guard(int feature)
    {
        if (!Index(feature, FeatureTable.Count) || FeatureTable.At(feature).Guard.Replaces is not { } replaces ||
            FeatureTable.At(feature).Knobs.Length == 0)
        {
            return;
        }

        var (id, rivals) = (FeatureTable.At(feature).Id, FeatureTable.At(feature).Guard.Rivals);
        MethodBase?[] seams;
        try
        {
            seams = [.. replaces().Take(EngineShape.MaxSeams + 1)];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            FeatureLog.Log?.Warning("Komet {0}: its methods could not be found, it stands down: {1}", id, e);
            seams = [null];
        }

        var foreign = EngineShape.Report(FeatureLog.Log, id, Rivals[feature],
            seams.Length > 0 && EngineShape.Foreign(seams, rivals, _owner), seams, rivals, _owner);
        if (foreign == Rivals[feature]) return;

        Rivals[feature] = foreign;
        _ = foreign ? FeatureHolds.Hold(feature) : FeatureHolds.Release(feature);
    }

    internal static void Clear() => Array.Clear(Rivals);
}

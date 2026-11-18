namespace Komet.Core;

// One choice for the knobs a player weighs, quality against speed against loading; every other knob follows the computer
// (HardwareProfile) or Komet's default. "auto" is balanced, or speed on a weak computer. A knob the player sets himself
// (KnobSettings.Chosen) keeps his value whatever the preset.
internal static class Presets
{
    private const string Auto = "auto";
    public static readonly string[] Names = [Auto, "quality", "balanced", "speed"];

    // The preset in use (auto, quality, balanced or speed) and its changes: mod adapters follow it
    public static string Current { get; private set; } = Auto;

    public static event Action? Changed;

    internal static void Activate(string preset)
    {
        if (Known(preset))
        {
            Current = preset;
            Changed?.Invoke();
        }
    }

    // The knob's preset values (Knob.Presets: quality, balanced, speed), null where no preset sets it
    private static PresetValues? Of(string key) =>
        NotNull(key) && Knobs.Find(key) is var knob and >= 0 ? Knobs.At(knob).Presets : null;

    public static bool Governs(string key) => Of(key) is not null;

    public static bool Known(string name) => NotNull(name) && Array.IndexOf(Names, name) >= 0;

    // A named preset's value for the knob, null where it sets none
    public static int? Value(string preset, string key)
    {
        if (Of(key) is not { } values || !Known(preset)) return null;

        var (quality, balanced, speed) = values;
        return Resolved(preset) switch
        {
            "quality" => quality,
            "speed" => speed,
            _ => Assert(balanced >= 0) ? balanced : 0
        };
    }

    public static string Resolved(string preset)
    {
        if (preset != Auto) return preset;

        return HardwareProfile.Weak ? "speed" : "balanced";
    }
}

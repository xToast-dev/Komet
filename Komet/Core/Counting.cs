namespace Komet.Core;

// Whether the features keep their counters, totals that only grow (readers take differences). Two owners, so neither can switch the
// other's counting off. Hud: every counter, while the HUD shows them or runs its own bench. Bench: a scripted benchmark records; it
// reads only the tessellation counters (TessAccounting, TessSchedule.NearWaiting), which count while On. The others stay off for it:
// their cost falls on the feature arms alone, never on the engine arm.
internal static class Counting
{
    public static bool On { get; private set; } // Hud or Bench

    // Raised each time Hud turns true. Before it the HUD's frames and the totals did not run together (the HUD-only totals stood still,
    // or the HUD was hidden while the bench kept the tessellation totals going), so a difference across it is no rate.
    public static int Epoch { get; private set; }

    public static bool Hud
    {
        get;
        set
        {
            if (value && !field) Epoch++;
            field = value;
            Update();
        }
    }

    public static bool Bench
    {
        get;
        set
        {
            field = value;
            Update();
        }
    }

    private static void Update()
    {
        On = Hud || Bench;
        _ = Assert(Epoch >= 0) && Assert(!Hud || Epoch > 0);
    }
}

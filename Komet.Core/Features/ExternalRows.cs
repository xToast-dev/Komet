namespace Komet.Core;

// Other mods' feature knobs as option rows: on Komet's own pages and on the mods' pages (OptionPages)
internal static class ExternalRows
{
    // Other mods' knobs in registration order under their group (on Komet's pages: mod id), locked while held
    public static OptionPage AddRows(OptionPage page, bool komet)
    {
        if (!NotNull(page)) return page;

        string? group = null;
        foreach (var external in ExternalFeatures.All.Bounded(FeatureTable.MaxExternal))
        {
            var (definition, knob) = (external.Definition, external.Knob);
            if (definition.Page != page.Id || definition.Knob is not { } k || knob < 0) continue;

            var heading = definition.Group ?? (komet ? external.ModId : null);
            if (heading is not null && heading != group) _ = page.Group(heading);

            (group, var hint, var unit) = (heading, definition.Hint, k.Unit.Length > 0 ? " " + k.Unit : "");
            _ = (k.IsSwitch
                ? page.Switch(definition.Title, () => ExternalFeatures.Chosen(external) != 0,
                    on => ExternalFeatures.Choose(external, on ? 1 : 0), hint)
                : page.Slider(definition.Title, k.Min, k.Max, 1, () => ExternalFeatures.Chosen(external),
                    v => ExternalFeatures.Choose(external, (int)v), unit, hint)).LockedWhen(() =>
                FeatureHolds.LockText(knob));
        }

        return page;
    }

    public static OptionPage Placed(OptionPage page)
    {
        if (!NotNull(page)) return page;

        foreach (var external in ExternalFeatures.All.Bounded(FeatureTable.MaxExternal))
        {
            if (external.Definition.Page == page.Id && external.Knob >= 0) return AddRows(page.Copy(), false);
        }

        return page;
    }
}

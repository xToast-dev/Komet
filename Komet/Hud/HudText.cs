using System.Globalization;

namespace Komet.Hud;

// Text the HUD, its dialogs, a dump and a bench report print. Numbers are metric and culture-invariant.
internal static class HudText
{
    private const int MaxCached = 256;
    private static readonly Dictionary<(string Prefix, string Name), string> Cache = [];
    private static string? _locale;

    // NaN is a value not measured yet, and an infinite one is a division by an empty window. Neither is a number to show; both print
    // as nothing, never as "NaN" or "Infinity".
    public static string Format(double value, string format) =>
        double.IsFinite(value) && Assert(format.Length is 2 or 3)
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : "";

    // Lang.Get hands the key back when the translation is missing
    public static string Translate(string key, params object[] args)
    {
        var full = "komet:" + key;
        var text = Lang.Get(full, args);
        return Assert(key.Length > 0) && Assert(text != full) ? text : key;
    }

    // A row's fixed label, translated once per language: Lang.CurrentLocale is another string after a change of language
    public static Func<string> Once(string key, params object[] args)
    {
        if (!Assert(key.Length > 0) || !NotNull(args)) return static () => "";
        string? locale = null, text = null;
        return () =>
        {
            if (text is null || !ReferenceEquals(locale, Lang.CurrentLocale))
                (locale, text) = (Lang.CurrentLocale, Translate(key, args));
            return text;
        };
    }

    // A key made of a prefix and a name the HUD holds (a render pass, a spike cause), once per language; the '~' that starts
    // Komet's own pseudo marks is not part of the key
    public static string Cached(string prefix, string name)
    {
        if (!ReferenceEquals(_locale, Lang.CurrentLocale) || Cache.Count >= MaxCached)
        {
            Cache.Clear();
            _locale = Lang.CurrentLocale;
        }

        if (!Cache.TryGetValue((prefix, name), out var text))
            Cache[(prefix, name)] = text = Translate(prefix + name.TrimStart('~'));
        return Assert(Cache.Count <= MaxCached) ? text : "";
    }

    // ISO 8601 UTC (what CI stamps and what GitHub reports) as local time in the language's format, minute precision; "" when absent
    // or unreadable
    public static string LocalTime(string iso)
    {
        if (iso.Length == 0 || !Assert(iso.Length <= 40)) return "";
        var format = Translate("time-format");
        var ok = DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var time);
        return ok && Assert(time.Year > 2000) && Assert(format.Length > 0)
            ? time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)
            : "";
    }
}
